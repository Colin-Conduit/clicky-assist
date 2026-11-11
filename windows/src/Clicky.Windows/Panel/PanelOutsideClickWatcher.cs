using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Clicky.Core.Pointing;
using Clicky.Windows.Diagnostics;

namespace Clicky.Windows.Panel;

/// <summary>
/// Reports every mouse button press anywhere on screen while the panel is open, so the panel
/// can hide itself when the press lands outside it. This is the Windows counterpart of the Mac's
/// NSEvent global monitor for leftMouseDown and rightMouseDown.
///
/// Why a low-level hook on its own thread: the panel never activates (WS_EX_NOACTIVATE), so it
/// gets no deactivation message when the user clicks elsewhere, and WPF has no global mouse
/// monitor. A WH_MOUSE_LL hook is called on the thread that installed it, inside that thread's
/// message loop, and Windows drops the hook for the event if the callback takes too long. A
/// dedicated thread keeps the hook responsive even while the UI thread is busy, and the callback
/// only posts the press point to the UI dispatcher.
/// </summary>
public sealed class PanelOutsideClickWatcher : IDisposable
{
    private static readonly TimeSpan HookThreadStartTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HookThreadStopTimeout = TimeSpan.FromSeconds(1);

    private readonly Dispatcher userInterfaceDispatcher;
    private readonly Action<PhysicalPixelPoint> mouseButtonPressedHandler;

    // Kept in a field for the watcher's whole lifetime: the native hook holds only a function
    // pointer to this delegate, so without a managed reference the garbage collector could free
    // it while the hook is still installed and the next mouse event would crash the process.
    private readonly PanelNativeMethods.LowLevelMouseHookCallback lowLevelMouseHookCallback;

    private Thread? hookThread;

    // Written by the hook thread, read by the UI thread in StopWatching.
    private volatile uint hookThreadIdentifier;
    private bool isDisposed;

    /// <param name="userInterfaceDispatcher">Dispatcher of the panel's UI thread; presses are delivered there.</param>
    /// <param name="mouseButtonPressedHandler">Called on the UI thread with the press location in physical pixels.</param>
    public PanelOutsideClickWatcher(Dispatcher userInterfaceDispatcher, Action<PhysicalPixelPoint> mouseButtonPressedHandler)
    {
        this.userInterfaceDispatcher = userInterfaceDispatcher;
        this.mouseButtonPressedHandler = mouseButtonPressedHandler;
        lowLevelMouseHookCallback = HandleLowLevelMouseEvent;
    }

    public bool IsWatching => hookThread is not null;

    /// <summary>Installs the hook. Call from the UI thread. Does nothing if already watching.</summary>
    public void StartWatching()
    {
        if (isDisposed || hookThread is not null)
        {
            return;
        }

        ManualResetEventSlim hookThreadReadySignal = new(initialState: false);
        Thread newHookThread = new(() => RunHookThread(hookThreadReadySignal))
        {
            IsBackground = true,
            Name = "Clicky panel outside-click hook",
        };
        newHookThread.Start();

        // Wait until the hook thread owns a message queue and has recorded its identifier.
        // Without this, a StopWatching that follows immediately could post WM_QUIT before the
        // queue exists; the post would fail and the hook thread would never exit.
        bool didHookThreadSignalReady = hookThreadReadySignal.Wait(HookThreadStartTimeout);
        if (didHookThreadSignalReady)
        {
            hookThreadReadySignal.Dispose();
        }
        else
        {
            // The hook thread will still call Set on the event later, so it is not disposed here
            // (Set on a disposed event would throw on that thread); the garbage collector frees it.
            ClickyApplicationLog.Write("Panel", "Outside-click hook thread did not start in time.");
        }

        hookThread = newHookThread;
    }

    /// <summary>Removes the hook and ends its thread. Call from the UI thread. Does nothing if not watching.</summary>
    public void StopWatching()
    {
        Thread? runningHookThread = hookThread;
        if (runningHookThread is null)
        {
            return;
        }

        hookThread = null;

        if (hookThreadIdentifier != 0)
        {
            // The hook thread unhooks itself after its message loop sees WM_QUIT, because
            // UnhookWindowsHookEx is expected to run on the thread that installed the hook.
            PanelNativeMethods.PostThreadMessage(hookThreadIdentifier, PanelNativeMethods.QuitMessage, IntPtr.Zero, IntPtr.Zero);
        }

        // Safe to wait here: the hook callback never blocks on the UI thread (it only posts).
        if (!runningHookThread.Join(HookThreadStopTimeout))
        {
            ClickyApplicationLog.Write("Panel", "Outside-click hook thread did not stop in time.");
        }

        hookThreadIdentifier = 0;
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        StopWatching();
        isDisposed = true;
    }

    private void RunHookThread(ManualResetEventSlim hookThreadReadySignal)
    {
        hookThreadIdentifier = PanelNativeMethods.GetCurrentThreadId();

        // A thread gets a message queue the first time it calls a message function. Peeking once
        // creates it, so PostThreadMessage(WM_QUIT) from StopWatching always has somewhere to land.
        PanelNativeMethods.PeekMessage(
            out _,
            IntPtr.Zero,
            PanelNativeMethods.FirstPrivateMessage,
            PanelNativeMethods.FirstPrivateMessage,
            PanelNativeMethods.PeekMessageNoRemove);

        // Low-level hooks run in the installing process, so any module handle of this process
        // satisfies SetWindowsHookEx; the executable's own handle is the conventional choice.
        IntPtr executableModuleHandle = PanelNativeMethods.GetModuleHandle(null);
        IntPtr mouseHookHandle = PanelNativeMethods.SetWindowsHookEx(
            PanelNativeMethods.LowLevelMouseHookType,
            lowLevelMouseHookCallback,
            executableModuleHandle,
            0);
        int hookInstallErrorCode = Marshal.GetLastWin32Error();

        hookThreadReadySignal.Set();

        if (mouseHookHandle == IntPtr.Zero)
        {
            ClickyApplicationLog.Write("Panel", $"Could not install the outside-click mouse hook: {new Win32Exception(hookInstallErrorCode).Message}");
            return;
        }

        try
        {
            // The hook callback is invoked from inside GetMessage. The loop ends when GetMessage
            // returns 0 (WM_QUIT from StopWatching) or -1 (error).
            while (PanelNativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
        }
        finally
        {
            PanelNativeMethods.UnhookWindowsHookEx(mouseHookHandle);
        }
    }

    private IntPtr HandleLowLevelMouseEvent(int hookCode, IntPtr mouseMessageIdentifier, IntPtr mouseHookDataPointer)
    {
        try
        {
            // A negative hook code means the event must be passed on without processing.
            if (hookCode >= 0 && IsMouseButtonDownMessage(mouseMessageIdentifier.ToInt32()))
            {
                PanelNativeMethods.LowLevelMouseHookData mouseHookData =
                    Marshal.PtrToStructure<PanelNativeMethods.LowLevelMouseHookData>(mouseHookDataPointer);
                PhysicalPixelPoint pressPointInPhysicalPixels = new(mouseHookData.CursorPoint.X, mouseHookData.CursorPoint.Y);

                // Send is the highest dispatcher priority, so the panel hides before the button-up
                // that follows (which is when the tray icon reports its click) is handled.
                userInterfaceDispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    mouseButtonPressedHandler,
                    pressPointInPhysicalPixels);
            }
        }
        catch (Exception hookCallbackException)
        {
            // An exception escaping into native code would end the process, so it stops here.
            ClickyApplicationLog.Write("Panel", $"Outside-click hook callback failed: {hookCallbackException.Message}");
        }

        // Always pass the event on so every other hook and the target window still receive it.
        return PanelNativeMethods.CallNextHookEx(IntPtr.Zero, hookCode, mouseMessageIdentifier, mouseHookDataPointer);
    }

    private static bool IsMouseButtonDownMessage(int mouseMessageIdentifier)
    {
        return mouseMessageIdentifier is PanelNativeMethods.LeftButtonDownMessage
            or PanelNativeMethods.RightButtonDownMessage
            or PanelNativeMethods.MiddleButtonDownMessage
            or PanelNativeMethods.ExtraButtonDownMessage;
    }
}
