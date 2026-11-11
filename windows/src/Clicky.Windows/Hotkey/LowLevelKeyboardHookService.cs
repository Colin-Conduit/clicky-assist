using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Clicky.Core.PushToTalk;
using Clicky.Windows.Diagnostics;
using static Clicky.Windows.Hotkey.KeyboardHookNativeMethods;

namespace Clicky.Windows.Hotkey;

/// <summary>
/// System-wide push-to-talk shortcut monitor. Windows port of GlobalPushToTalkShortcutMonitor
/// on the Mac, which uses a listen-only CGEvent tap. The Windows equivalent is a low-level
/// keyboard hook (WH_KEYBOARD_LL): it sees every key event in the session no matter which
/// app has focus, and like the Mac tap it only listens and never swallows keys.
///
/// The hook runs on its own background thread with its own message loop, because Windows
/// delivers low-level hook calls by sending a message to the installing thread. Keeping that
/// thread free of everything else means a busy UI thread can never delay system-wide typing.
///
/// All press and release rules live in <see cref="PushToTalkShortcutStateMachine"/>; this class
/// only feeds it key events and hands its transitions to the UI thread.
/// </summary>
public sealed class LowLevelKeyboardHookService : IDisposable
{
    private const string LogCategory = "Hotkey";
    private const string HookThreadName = "Clicky keyboard hook";

    /// <summary>ERROR_TIMEOUT, used when the hook thread does not report back in time.</summary>
    private const int Win32ErrorCodeTimeout = 1460;

    /// <summary>How often the hook thread compares its key state with the real keyboard.</summary>
    private const uint StuckKeyCheckIntervalInMilliseconds = 250;

    /// <summary>
    /// Windows calls the hook before it updates the asynchronous key state, and other apps' hooks
    /// may still be running after ours returns. A stuck-key check that lands in that gap would read
    /// a key that was just pressed as "up". Skipping checks for a moment after any key event avoids
    /// that, and costs nothing for real stuck keys: on the secure desktop no key events arrive at all.
    /// </summary>
    private const long MinimumMillisecondsSinceLastKeyEventBeforeStuckKeyCheck = 50;

    private static readonly TimeSpan HookInstallationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HookThreadShutdownTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Cached so the timer tick does not allocate a new delegate every 250 ms.</summary>
    private static readonly Func<int, bool> IsVirtualKeyPhysicallyDown =
        virtualKeyCode => (GetAsyncKeyState(virtualKeyCode) & AsyncKeyStateKeyIsDownBit) != 0;

    private readonly Dispatcher userInterfaceDispatcher;

    /// <summary>
    /// Fed from the hook thread (key events and timer ticks) and from any thread through
    /// <see cref="ChangeShortcutOption"/>, so every call goes through <see cref="shortcutStateMachineLock"/>.
    /// </summary>
    private readonly PushToTalkShortcutStateMachine shortcutStateMachine;
    private readonly object shortcutStateMachineLock = new();

    /// <summary>Serializes <see cref="Start"/> and <see cref="Dispose"/>.</summary>
    private readonly object lifecycleLock = new();

    /// <summary>
    /// The hook callback handed to SetWindowsHookEx. Native code only holds a raw function pointer,
    /// which does not keep the managed delegate alive, so this field must hold it for as long as the
    /// hook is installed or the garbage collector could free it and crash the next key press.
    /// </summary>
    private readonly LowLevelKeyboardProcedure lowLevelKeyboardProcedure;

    // Lifecycle state, guarded by lifecycleLock.
    private Thread? runningHookThread;
    private uint runningHookThreadNativeIdentifier;
    private volatile bool isDisposed;

    // Hook thread state.
    private IntPtr installedHookHandle;
    private volatile bool isHookInstalled;
    private bool hasReportedHookCallbackFailure;

    // Guarded by shortcutStateMachineLock.
    private long lastKeyEventTickCountInMilliseconds;

    // UI thread state.
    private bool isShortcutCurrentlyPressedOnUserInterfaceThread;

    public LowLevelKeyboardHookService(PushToTalkShortcutOption initialShortcutOption, Dispatcher userInterfaceDispatcher)
    {
        ArgumentNullException.ThrowIfNull(userInterfaceDispatcher);

        this.userInterfaceDispatcher = userInterfaceDispatcher;
        shortcutStateMachine = new PushToTalkShortcutStateMachine(initialShortcutOption);
        lowLevelKeyboardProcedure = HandleLowLevelKeyboardHookCall;
    }

    /// <summary>Raised on the UI dispatcher, only for Pressed and Released (never None).</summary>
    public event Action<PushToTalkShortcutTransition>? ShortcutTransitionOccurred;

    /// <summary>UI-thread mirror of the last transition raised.</summary>
    public bool IsShortcutCurrentlyPressed => isShortcutCurrentlyPressedOnUserInterfaceThread;

    /// <summary>
    /// True while the hook thread holds an installed hook. Windows can remove a low-level hook
    /// silently (when a callback exceeds LowLevelHooksTimeout) without telling the app, so this
    /// can stay true after such a removal.
    /// </summary>
    public bool IsHookInstalled => isHookInstalled;

    /// <summary>
    /// Installs the hook on a dedicated background thread and waits up to 5 seconds for the
    /// result. A second call while the hook thread is running does nothing, like the Mac
    /// start() guard. A call after a failed attempt tries again.
    /// </summary>
    /// <exception cref="Win32Exception">SetWindowsHookEx failed (carrying its Win32 error), or the hook thread did not report back in time (ERROR_TIMEOUT).</exception>
    public void Start()
    {
        lock (lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);

            if (runningHookThread is not null)
            {
                return;
            }

            // RunContinuationsAsynchronously keeps any future continuation off the hook thread,
            // which must stay free to answer hook calls.
            TaskCompletionSource<HookInstallationOutcome> hookInstallationOutcomeSource =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            Thread newHookThread = new(() => RunHookThread(hookInstallationOutcomeSource))
            {
                Name = HookThreadName,
                // Background so a forgotten Dispose never keeps the process alive on exit.
                IsBackground = true,
                // Every key press in the session waits for this thread to answer the hook call,
                // so let it win the CPU over ordinary work when the machine is busy.
                Priority = ThreadPriority.AboveNormal,
            };
            newHookThread.Start();

            bool didHookThreadReportInTime = hookInstallationOutcomeSource.Task.Wait(HookInstallationTimeout);

            // TrySetCanceled and the hook thread's TrySetResult race; exactly one wins. If the
            // cancel wins, the hook thread sees its result rejected and removes the hook itself.
            if (!didHookThreadReportInTime && hookInstallationOutcomeSource.TrySetCanceled())
            {
                ClickyApplicationLog.Write(LogCategory, $"Keyboard hook thread did not report back within {HookInstallationTimeout.TotalSeconds:0} seconds. Push-to-talk is unavailable.");
                throw new Win32Exception(Win32ErrorCodeTimeout, "Timed out waiting for the keyboard hook to be installed.");
            }

            HookInstallationOutcome hookInstallationOutcome = hookInstallationOutcomeSource.Task.Result;

            if (!hookInstallationOutcome.WasHookInstalled)
            {
                Win32Exception hookInstallationException = CreateHookInstallationException(hookInstallationOutcome.HookInstallationWin32ErrorCode);
                ClickyApplicationLog.Write(LogCategory, $"Could not install the keyboard hook (Win32 error {hookInstallationOutcome.HookInstallationWin32ErrorCode}): {hookInstallationException.Message}");
                throw hookInstallationException;
            }

            runningHookThread = newHookThread;
            runningHookThreadNativeIdentifier = hookInstallationOutcome.HookThreadNativeIdentifier;

            PushToTalkShortcutOption currentShortcutOption;
            lock (shortcutStateMachineLock)
            {
                currentShortcutOption = shortcutStateMachine.ShortcutOption;
            }

            ClickyApplicationLog.Write(LogCategory, $"Keyboard hook installed. Push-to-talk shortcut: {PushToTalkShortcutDefinitions.GetDisplayText(currentShortcutOption)}.");

            if (!hookInstallationOutcome.WasStuckKeyCheckTimerCreated)
            {
                ClickyApplicationLog.Write(LogCategory, $"Could not create the stuck-key check timer (Win32 error {hookInstallationOutcome.StuckKeyCheckTimerWin32ErrorCode}). A shortcut held during Ctrl + Alt + Del or Win + L may stay pressed until it is pressed again.");
            }
        }
    }

    /// <summary>
    /// Switches the shortcut the hook listens for. Safe from any thread. If the old shortcut was
    /// held, Released is raised so the caller can end the recording in progress.
    /// </summary>
    public void ChangeShortcutOption(PushToTalkShortcutOption newShortcutOption)
    {
        lock (shortcutStateMachineLock)
        {
            PushToTalkShortcutTransition shortcutChangeTransition = shortcutStateMachine.ChangeShortcutOption(newShortcutOption);

            // Posted rather than raised directly, even when already on the UI thread, so it can
            // never overtake a Pressed from the hook thread that is still waiting in the dispatcher.
            PostTransitionToUserInterfaceThreadIfAny(shortcutChangeTransition);
        }
    }

    /// <summary>
    /// Tells the hook thread to quit, which removes the hook on that thread, and waits up to
    /// 2 seconds for it. Safe to call more than once. Transitions still queued in the dispatcher
    /// are dropped once this has been called.
    /// </summary>
    public void Dispose()
    {
        Thread? hookThreadToStop;
        uint hookThreadNativeIdentifierToStop;

        lock (lifecycleLock)
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            hookThreadToStop = runningHookThread;
            hookThreadNativeIdentifierToStop = runningHookThreadNativeIdentifier;
            runningHookThread = null;
        }

        if (hookThreadToStop is null)
        {
            return;
        }

        // UnhookWindowsHookEx has to run on the thread that installed the hook, so ask that thread
        // to leave its message loop instead of unhooking from here.
        bool wasQuitMessagePosted = PostThreadMessage(hookThreadNativeIdentifierToStop, QuitMessage, IntPtr.Zero, IntPtr.Zero);
        if (!wasQuitMessagePosted)
        {
            int postThreadMessageWin32ErrorCode = Marshal.GetLastWin32Error();
            ClickyApplicationLog.Write(LogCategory, $"Could not ask the keyboard hook thread to quit (Win32 error {postThreadMessageWin32ErrorCode}). It may already have exited.");
        }

        bool didHookThreadExitInTime = hookThreadToStop.Join(HookThreadShutdownTimeout);
        if (!didHookThreadExitInTime)
        {
            // Windows removes a thread's hooks when the thread ends, and the thread is a background
            // thread, so the hook still goes away when the process exits.
            ClickyApplicationLog.Write(LogCategory, $"Keyboard hook thread did not exit within {HookThreadShutdownTimeout.TotalSeconds:0} seconds.");
        }
    }

    private static Win32Exception CreateHookInstallationException(int hookInstallationWin32ErrorCode)
    {
        // Win32Exception(int) looks up the system's text for the error code.
        string systemErrorDescription = new Win32Exception(hookInstallationWin32ErrorCode).Message;
        return new Win32Exception(hookInstallationWin32ErrorCode, $"Could not install the keyboard hook: {systemErrorDescription}");
    }

    /// <summary>
    /// Body of the dedicated hook thread: installs the hook, reports the result to
    /// <see cref="Start"/>, runs the message loop until WM_QUIT, then removes the hook.
    /// </summary>
    private void RunHookThread(TaskCompletionSource<HookInstallationOutcome> hookInstallationOutcomeSource)
    {
        uint hookThreadNativeIdentifier = GetCurrentThreadId();

        // A thread gets a message queue only once it calls a message function. Creating it now,
        // before reporting success, guarantees the WM_QUIT that Dispose posts has somewhere to go.
        PeekMessage(out _, IntPtr.Zero, FirstPrivateMessage, FirstPrivateMessage, PeekMessageNoRemove);

        // Low-level hooks are never injected into other processes, but SetWindowsHookEx still
        // requires a module handle for a global hook. The app's own executable module works.
        IntPtr applicationModuleHandle = GetModuleHandle(null);
        IntPtr newHookHandle = SetWindowsHookEx(LowLevelKeyboardHookType, lowLevelKeyboardProcedure, applicationModuleHandle, 0);

        if (newHookHandle == IntPtr.Zero)
        {
            // Read immediately: the last Win32 error is per thread and the next call overwrites it.
            int hookInstallationWin32ErrorCode = Marshal.GetLastWin32Error();
            hookInstallationOutcomeSource.TrySetResult(new HookInstallationOutcome(
                WasHookInstalled: false,
                HookInstallationWin32ErrorCode: hookInstallationWin32ErrorCode,
                HookThreadNativeIdentifier: hookThreadNativeIdentifier,
                WasStuckKeyCheckTimerCreated: false,
                StuckKeyCheckTimerWin32ErrorCode: 0));
            return;
        }

        installedHookHandle = newHookHandle;
        isHookInstalled = true;

        // Stuck-key protection. Ctrl + Alt + Del, Win + L and UAC prompts move input to the secure
        // desktop, and the hook never sees the key-ups that happen there, so the state machine would
        // believe the shortcut is still held. A thread timer (null window, so WM_TIMER arrives in this
        // thread's own queue) periodically checks the real key state instead.
        UIntPtr stuckKeyCheckTimerIdentifier = SetTimer(IntPtr.Zero, UIntPtr.Zero, StuckKeyCheckIntervalInMilliseconds, IntPtr.Zero);
        bool wasStuckKeyCheckTimerCreated = stuckKeyCheckTimerIdentifier != UIntPtr.Zero;
        int stuckKeyCheckTimerWin32ErrorCode = wasStuckKeyCheckTimerCreated ? 0 : Marshal.GetLastWin32Error();

        bool didStartAcceptInstallation = hookInstallationOutcomeSource.TrySetResult(new HookInstallationOutcome(
            WasHookInstalled: true,
            HookInstallationWin32ErrorCode: 0,
            HookThreadNativeIdentifier: hookThreadNativeIdentifier,
            WasStuckKeyCheckTimerCreated: wasStuckKeyCheckTimerCreated,
            StuckKeyCheckTimerWin32ErrorCode: stuckKeyCheckTimerWin32ErrorCode));

        int messageLoopWin32ErrorCode = 0;
        if (didStartAcceptInstallation)
        {
            messageLoopWin32ErrorCode = RunMessageLoopUntilQuit(stuckKeyCheckTimerIdentifier);
        }

        // Either Dispose posted WM_QUIT, or Start timed out and has already thrown, in which case
        // nobody else will ever stop this thread, so it removes its own hook straight away.
        if (wasStuckKeyCheckTimerCreated)
        {
            KillTimer(IntPtr.Zero, stuckKeyCheckTimerIdentifier);
        }

        bool wasHookRemoved = UnhookWindowsHookEx(installedHookHandle);
        int hookRemovalWin32ErrorCode = wasHookRemoved ? 0 : Marshal.GetLastWin32Error();
        installedHookHandle = IntPtr.Zero;
        isHookInstalled = false;

        // Logging is safe from here on: the hook is gone, so no key press is waiting on this thread.
        if (messageLoopWin32ErrorCode != 0)
        {
            ClickyApplicationLog.Write(LogCategory, $"Keyboard hook message loop stopped with Win32 error {messageLoopWin32ErrorCode}. Push-to-talk is unavailable.");
        }

        if (!wasHookRemoved)
        {
            ClickyApplicationLog.Write(LogCategory, $"UnhookWindowsHookEx failed (Win32 error {hookRemovalWin32ErrorCode}). Windows removes the hook when this thread ends.");
        }
    }

    /// <summary>
    /// Pumps this thread's queue until WM_QUIT. Windows delivers the hook callback while the
    /// thread waits inside GetMessage, so the loop itself only has to handle the timer.
    /// Returns 0 after WM_QUIT, or the Win32 error if GetMessage failed.
    /// </summary>
    private int RunMessageLoopUntilQuit(UIntPtr stuckKeyCheckTimerIdentifier)
    {
        while (true)
        {
            int getMessageResult = GetMessage(out NativeMessage receivedMessage, IntPtr.Zero, 0, 0);

            if (getMessageResult == 0)
            {
                return 0;
            }

            if (getMessageResult == -1)
            {
                return Marshal.GetLastWin32Error();
            }

            bool isStuckKeyCheckTimerTick = receivedMessage.MessageIdentifier == TimerMessage
                && (nuint)receivedMessage.WordParameter == stuckKeyCheckTimerIdentifier;

            if (isStuckKeyCheckTimerTick)
            {
                ReleaseShortcutIfKeysAreNoLongerPhysicallyHeld();
            }

            // This thread owns no windows, so there is nothing to translate or dispatch.
        }
    }

    /// <summary>
    /// Low-level keyboard hook callback, called on the hook thread for every key event in the
    /// session. Windows silently removes a low-level hook whose callback runs longer than
    /// LowLevelHooksTimeout, and every key press system-wide waits for this method, so it does the
    /// minimum: read the event, feed the state machine under a short lock, and if that produced a
    /// transition, queue it for the UI thread with BeginInvoke (which only enqueues and never
    /// waits). No logging, file I/O or UI work happens here.
    /// </summary>
    private IntPtr HandleLowLevelKeyboardHookCall(int hookCode, IntPtr keyboardMessageIdentifier, IntPtr keyboardEventDataPointer)
    {
        // A negative code means Windows wants the event passed on without being looked at.
        if (hookCode == HookCodeAction)
        {
            try
            {
                FeedKeyboardEventToShortcutStateMachine(keyboardMessageIdentifier, keyboardEventDataPointer);
            }
            catch (Exception unexpectedHookCallbackException)
            {
                // An exception escaping into native code would terminate the process. Report the first
                // one from a thread-pool thread so the file write never happens inside the callback.
                ReportFirstHookCallbackFailureInBackground(unexpectedHookCallbackException);
            }
        }

        // Always pass the event on. Like the Mac's listen-only event tap, this hook only observes and
        // never swallows keys, so the shortcut still reaches whatever app has focus.
        return CallNextHookEx(installedHookHandle, hookCode, keyboardMessageIdentifier, keyboardEventDataPointer);
    }

    private unsafe void FeedKeyboardEventToShortcutStateMachine(IntPtr keyboardMessageIdentifier, IntPtr keyboardEventDataPointer)
    {
        int keyboardMessage = (int)keyboardMessageIdentifier;

        // WM_SYSKEYDOWN and WM_SYSKEYUP are what Windows sends while Alt is held (and for Alt itself),
        // so they count as ordinary key-downs and key-ups here.
        bool isKeyDownEvent = keyboardMessage is KeyDownMessage or SystemKeyDownMessage;
        bool isKeyUpEvent = keyboardMessage is KeyUpMessage or SystemKeyUpMessage;
        if (!isKeyDownEvent && !isKeyUpEvent)
        {
            return;
        }

        LowLevelKeyboardEventData keyboardEventData = *(LowLevelKeyboardEventData*)keyboardEventDataPointer;
        int virtualKeyCode = (int)keyboardEventData.VirtualKeyCode;

        // Injected events (LLKHF_INJECTED set in EventFlags: SendInput, on-screen keyboards, remote
        // control and automation tools) are processed like physical ones on purpose, because the Mac's
        // session event tap sees synthetic events too.

        lock (shortcutStateMachineLock)
        {
            lastKeyEventTickCountInMilliseconds = Environment.TickCount64;

            // Ctrl + Alt equals AltGr on some keyboard layouts; PushToTalkShortcutStateMachine documents the details.
            PushToTalkShortcutTransition keyEventTransition = shortcutStateMachine.HandleKeyEvent(virtualKeyCode, isKeyDownEvent);
            PostTransitionToUserInterfaceThreadIfAny(keyEventTransition);
        }
    }

    /// <summary>Runs on the hook thread for each stuck-key timer tick.</summary>
    private void ReleaseShortcutIfKeysAreNoLongerPhysicallyHeld()
    {
        lock (shortcutStateMachineLock)
        {
            long millisecondsSinceLastKeyEvent = Environment.TickCount64 - lastKeyEventTickCountInMilliseconds;
            if (millisecondsSinceLastKeyEvent < MinimumMillisecondsSinceLastKeyEventBeforeStuckKeyCheck)
            {
                return;
            }

            // Runs on every tick, not only while pressed: it also forgets modifiers whose key-up was
            // lost, which would otherwise complete a later shortcut on their own.
            PushToTalkShortcutTransition reconciliationTransition = shortcutStateMachine.ReconcileWithPhysicalKeyState(IsVirtualKeyPhysicallyDown);
            if (reconciliationTransition == PushToTalkShortcutTransition.Released)
            {
                PostTransitionToUserInterfaceThreadIfAny(reconciliationTransition);
            }
        }
    }

    /// <summary>
    /// Queues a transition for the UI thread. Always called while holding
    /// <see cref="shortcutStateMachineLock"/>, and always at the same dispatcher priority, so the
    /// order the UI sees matches the order the state machine produced.
    /// </summary>
    private void PostTransitionToUserInterfaceThreadIfAny(PushToTalkShortcutTransition shortcutTransition)
    {
        if (shortcutTransition == PushToTalkShortcutTransition.None)
        {
            return;
        }

        // BeginInvoke never blocks. Dispatcher.Invoke must never be used here: it would stall the hook
        // callback and could deadlock against Dispose joining this thread from the UI thread.
        userInterfaceDispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() => RaiseShortcutTransitionOnUserInterfaceThread(shortcutTransition)));
    }

    private void RaiseShortcutTransitionOnUserInterfaceThread(PushToTalkShortcutTransition shortcutTransition)
    {
        if (isDisposed)
        {
            return;
        }

        isShortcutCurrentlyPressedOnUserInterfaceThread = shortcutTransition == PushToTalkShortcutTransition.Pressed;
        ShortcutTransitionOccurred?.Invoke(shortcutTransition);
    }

    private void ReportFirstHookCallbackFailureInBackground(Exception unexpectedHookCallbackException)
    {
        if (hasReportedHookCallbackFailure)
        {
            return;
        }

        hasReportedHookCallbackFailure = true;
        string hookCallbackFailureDescription = $"{unexpectedHookCallbackException.GetType().Name}: {unexpectedHookCallbackException.Message}";
        ThreadPool.QueueUserWorkItem(_ => ClickyApplicationLog.Write(LogCategory, $"Keyboard hook callback failed and the event was passed on unprocessed. {hookCallbackFailureDescription}"));
    }

    /// <summary>What the hook thread reports back to <see cref="Start"/>.</summary>
    private readonly record struct HookInstallationOutcome(
        bool WasHookInstalled,
        int HookInstallationWin32ErrorCode,
        uint HookThreadNativeIdentifier,
        bool WasStuckKeyCheckTimerCreated,
        int StuckKeyCheckTimerWin32ErrorCode);
}
