using System.Runtime.InteropServices;

namespace Clicky.Windows.Panel;

/// <summary>
/// Win32 calls the companion panel needs: extended window styles, moving the window in
/// physical pixels, and the low-level mouse hook used for click-outside dismissal.
/// Constant values match the Win32 metadata (WinUser.h); the original Win32 name is in each comment.
/// </summary>
internal static class PanelNativeMethods
{
    /// <summary>GWL_EXSTYLE: index of the extended window style for Get/SetWindowLongPtr.</summary>
    internal const int ExtendedWindowStyleIndex = -20;

    /// <summary>WS_EX_TOOLWINDOW: keeps the window out of Alt+Tab.</summary>
    internal const long ExtendedWindowStyleToolWindow = 0x00000080;

    /// <summary>WS_EX_NOACTIVATE: clicks do not activate the window, so it never takes focus from the app the user is in.</summary>
    internal const long ExtendedWindowStyleNoActivate = 0x08000000;

    /// <summary>HWND_TOPMOST: places the window at the top of the always-on-top band.</summary>
    internal static readonly IntPtr TopmostWindowInsertAfterHandle = new(-1);

    /// <summary>SWP_NOSIZE: keep the current size (WPF owns the size through SizeToContent).</summary>
    internal const uint SetWindowPositionNoSize = 0x0001;

    /// <summary>SWP_NOZORDER: keep the current z-order.</summary>
    internal const uint SetWindowPositionNoZOrder = 0x0004;

    /// <summary>SWP_NOACTIVATE: do not activate the window.</summary>
    internal const uint SetWindowPositionNoActivate = 0x0010;

    /// <summary>WH_MOUSE_LL: system-wide low-level mouse hook.</summary>
    internal const int LowLevelMouseHookType = 14;

    /// <summary>WM_LBUTTONDOWN.</summary>
    internal const int LeftButtonDownMessage = 0x0201;

    /// <summary>WM_RBUTTONDOWN.</summary>
    internal const int RightButtonDownMessage = 0x0204;

    /// <summary>WM_MBUTTONDOWN.</summary>
    internal const int MiddleButtonDownMessage = 0x0207;

    /// <summary>WM_XBUTTONDOWN (the side buttons on many mice).</summary>
    internal const int ExtraButtonDownMessage = 0x020B;

    /// <summary>WM_QUIT: ends the hook thread's GetMessage loop.</summary>
    internal const uint QuitMessage = 0x0012;

    /// <summary>WM_USER: any message number works for the PeekMessage call that creates the queue; this one is never posted.</summary>
    internal const uint FirstPrivateMessage = 0x0400;

    /// <summary>PM_NOREMOVE.</summary>
    internal const uint PeekMessageNoRemove = 0x0000;

    /// <summary>HOOKPROC signature for WH_MOUSE_LL.</summary>
    internal delegate IntPtr LowLevelMouseHookCallback(int hookCode, IntPtr mouseMessageIdentifier, IntPtr mouseHookDataPointer);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>MSLLHOOKSTRUCT. The point is in per-monitor-aware screen coordinates, which for this PerMonitorV2 app are physical pixels.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LowLevelMouseHookData
    {
        public NativePoint CursorPoint;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    /// <summary>MSG (48 bytes on 64-bit Windows).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMessage
    {
        public IntPtr WindowHandle;
        public uint MessageIdentifier;
        public IntPtr WParameter;
        public IntPtr LParameter;
        public uint Time;
        public NativePoint CursorPoint;
    }

    /// <summary>
    /// ORs extra bits into the window's extended style. GetWindowLongPtr and SetWindowLongPtr
    /// only exist as exports in 64-bit user32; a 32-bit process has to use the Long variants.
    /// </summary>
    internal static void AddExtendedWindowStyles(IntPtr windowHandle, long extendedStyleBitsToAdd)
    {
        if (IntPtr.Size == 8)
        {
            long currentExtendedStyle = GetWindowLongPtr64(windowHandle, ExtendedWindowStyleIndex).ToInt64();
            SetWindowLongPtr64(windowHandle, ExtendedWindowStyleIndex, new IntPtr(currentExtendedStyle | extendedStyleBitsToAdd));
        }
        else
        {
            // The style is a 32-bit flag set: read it as unsigned so widening to long does not sign-extend.
            uint currentExtendedStyle = unchecked((uint)GetWindowLong32(windowHandle, ExtendedWindowStyleIndex));
            long combinedExtendedStyle = currentExtendedStyle | extendedStyleBitsToAdd;
            SetWindowLong32(windowHandle, ExtendedWindowStyleIndex, unchecked((int)combinedExtendedStyle));
        }
    }

    /// <summary>
    /// Moves the window's top-left corner to a physical-pixel position without resizing or activating it.
    /// When <paramref name="shouldBringToTopOfTopmostBand"/> is true the window also goes above other
    /// always-on-top windows such as the taskbar, which the user just clicked.
    /// </summary>
    internal static bool MoveWindowWithoutActivating(IntPtr windowHandle, int leftInPhysicalPixels, int topInPhysicalPixels, bool shouldBringToTopOfTopmostBand)
    {
        uint setWindowPositionFlags = SetWindowPositionNoSize | SetWindowPositionNoActivate;
        if (!shouldBringToTopOfTopmostBand)
        {
            setWindowPositionFlags |= SetWindowPositionNoZOrder;
        }

        return SetWindowPos(windowHandle, TopmostWindowInsertAfterHandle, leftInPhysicalPixels, topInPhysicalPixels, 0, 0, setWindowPositionFlags);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfterWindowHandle, int left, int top, int width, int height, uint setWindowPositionFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int valueIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int valueIndex, IntPtr newValue);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr windowHandle, int valueIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr windowHandle, int valueIndex, int newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int hookType, LowLevelMouseHookCallback hookCallback, IntPtr moduleHandle, uint threadIdentifier);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hookHandle, int hookCode, IntPtr messageIdentifier, IntPtr hookDataPointer);

    /// <summary>Returns a positive value for a normal message, 0 for WM_QUIT and -1 on error.</summary>
    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    internal static extern int GetMessage(out NativeMessage receivedMessage, IntPtr windowHandleFilter, uint firstMessageFilter, uint lastMessageFilter);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(out NativeMessage peekedMessage, IntPtr windowHandleFilter, uint firstMessageFilter, uint lastMessageFilter, uint removeFlags);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint threadIdentifier, uint messageIdentifier, IntPtr wParameter, IntPtr lParameter);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);
}
