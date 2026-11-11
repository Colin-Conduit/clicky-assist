using System.Runtime.InteropServices;

namespace Clicky.Windows.Hotkey;

/// <summary>
/// Win32 declarations for the global push-to-talk keyboard hook. Constant values and
/// structure layouts follow winuser.h as documented on Microsoft Learn; the Win32 name of
/// each item is noted next to it so it can be looked up.
/// </summary>
internal static class KeyboardHookNativeMethods
{
    /// <summary>WH_KEYBOARD_LL: system-wide low-level keyboard hook, called on the installing thread.</summary>
    internal const int LowLevelKeyboardHookType = 13;

    /// <summary>HC_ACTION: wParam and lParam describe a keyboard event.</summary>
    internal const int HookCodeAction = 0;

    /// <summary>WM_KEYDOWN.</summary>
    internal const int KeyDownMessage = 0x0100;

    /// <summary>WM_KEYUP.</summary>
    internal const int KeyUpMessage = 0x0101;

    /// <summary>WM_SYSKEYDOWN: key down while Alt is held, or F10, or Alt itself.</summary>
    internal const int SystemKeyDownMessage = 0x0104;

    /// <summary>WM_SYSKEYUP.</summary>
    internal const int SystemKeyUpMessage = 0x0105;

    /// <summary>WM_TIMER.</summary>
    internal const uint TimerMessage = 0x0113;

    /// <summary>WM_QUIT: makes GetMessage return 0 so the hook thread's loop ends.</summary>
    internal const uint QuitMessage = 0x0012;

    /// <summary>WM_USER: start of the private message range, used only to create the message queue.</summary>
    internal const uint FirstPrivateMessage = 0x0400;

    /// <summary>PM_NOREMOVE: PeekMessage leaves any message in the queue.</summary>
    internal const uint PeekMessageNoRemove = 0x0000;

    /// <summary>LLKHF_INJECTED: the event came from SendInput or keybd_event rather than a keyboard.</summary>
    internal const uint LowLevelKeyboardFlagInjected = 0x00000010;

    /// <summary>The high-order bit of GetAsyncKeyState's result is set while the key is down.</summary>
    internal const ushort AsyncKeyStateKeyIsDownBit = 0x8000;

    /// <summary>LowLevelKeyboardProc (HOOKPROC). Must be kept alive in a field while the hook is installed.</summary>
    internal delegate IntPtr LowLevelKeyboardProcedure(int hookCode, IntPtr keyboardMessageIdentifier, IntPtr keyboardEventDataPointer);

    /// <summary>KBDLLHOOKSTRUCT.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LowLevelKeyboardEventData
    {
        /// <summary>vkCode.</summary>
        public uint VirtualKeyCode;

        /// <summary>scanCode.</summary>
        public uint HardwareScanCode;

        /// <summary>flags (LLKHF_*).</summary>
        public uint EventFlags;

        /// <summary>time, in milliseconds, comparable to GetMessageTime.</summary>
        public uint EventTimestampInMilliseconds;

        /// <summary>dwExtraInfo (ULONG_PTR).</summary>
        public UIntPtr ExtraInformation;
    }

    /// <summary>POINT.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// MSG, including the trailing lPrivate field from current winuser.h. GetMessage writes the
    /// full native structure, so the managed copy must never be smaller than it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMessage
    {
        public IntPtr WindowHandle;
        public uint MessageIdentifier;
        public IntPtr WordParameter;
        public IntPtr LongParameter;
        public uint MessageTimestampInMilliseconds;
        public NativePoint CursorPosition;
        public uint PrivateData;
    }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    internal static extern IntPtr SetWindowsHookEx(int hookType, LowLevelKeyboardProcedure hookProcedure, IntPtr moduleHandle, uint threadIdentifier);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hookHandle, int hookCode, IntPtr keyboardMessageIdentifier, IntPtr keyboardEventDataPointer);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    /// <summary>Returns a positive value for a message, 0 for WM_QUIT and -1 on error.</summary>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetMessageW")]
    internal static extern int GetMessage(out NativeMessage receivedMessage, IntPtr windowHandle, uint minimumMessageFilter, uint maximumMessageFilter);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(out NativeMessage peekedMessage, IntPtr windowHandle, uint minimumMessageFilter, uint maximumMessageFilter, uint removalOptions);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint threadIdentifier, uint messageIdentifier, IntPtr wordParameter, IntPtr longParameter);

    /// <summary>With a null window and a null callback, WM_TIMER is posted to the calling thread's queue.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern UIntPtr SetTimer(IntPtr windowHandle, UIntPtr timerIdentifier, uint intervalInMilliseconds, IntPtr timerCallback);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool KillTimer(IntPtr windowHandle, UIntPtr timerIdentifier);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKeyCode);
}
