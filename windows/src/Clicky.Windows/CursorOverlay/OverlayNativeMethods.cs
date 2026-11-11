using System.Runtime.InteropServices;

namespace Clicky.Windows.CursorOverlay;

/// <summary>
/// Win32 calls the cursor overlay windows need beyond what WPF exposes.
/// Constant values are copied from WinUser.h in the Windows SDK.
/// </summary>
internal static class OverlayNativeMethods
{
    /// <summary>GWL_EXSTYLE: index of the extended window style for GetWindowLongPtr and SetWindowLongPtr.</summary>
    public const int ExtendedWindowStyleIndex = -20;

    /// <summary>WS_EX_TRANSPARENT: mouse input falls through the window to whatever is underneath.</summary>
    public const long ExtendedStyleTransparent = 0x00000020;

    /// <summary>WS_EX_TOOLWINDOW: the window never appears in Alt+Tab or Task View.</summary>
    public const long ExtendedStyleToolWindow = 0x00000080;

    /// <summary>WS_EX_NOACTIVATE: showing or clicking the window never makes it the foreground window.</summary>
    public const long ExtendedStyleNoActivate = 0x08000000;

    /// <summary>HWND_TOPMOST: places the window above every non-topmost window and keeps it there.</summary>
    public static readonly IntPtr TopmostInsertAfterWindowHandle = new(-1);

    /// <summary>SWP_NOACTIVATE: SetWindowPos must not activate the window.</summary>
    public const uint SetWindowPositionNoActivate = 0x0010;

    /// <summary>SWP_FRAMECHANGED: makes Windows re-read styles changed with SetWindowLongPtr.</summary>
    public const uint SetWindowPositionFrameChanged = 0x0020;

    /// <summary>SWP_NOOWNERZORDER: leave the hidden owner window WPF creates (ShowInTaskbar false) where it is.</summary>
    public const uint SetWindowPositionNoOwnerZOrder = 0x0200;

    /// <summary>WM_MOUSEACTIVATE: asks the window whether a click should activate it.</summary>
    public const int WindowMessageMouseActivate = 0x0021;

    /// <summary>WM_NCHITTEST: asks the window which part of it is under the mouse.</summary>
    public const int WindowMessageNonClientHitTest = 0x0084;

    /// <summary>WM_DPICHANGED: the window moved to a monitor with a different scale, or the scale changed.</summary>
    public const int WindowMessageDpiChanged = 0x02E0;

    /// <summary>MA_NOACTIVATE: answer to WM_MOUSEACTIVATE meaning "do not activate".</summary>
    public const int MouseActivateNoActivate = 3;

    /// <summary>HTTRANSPARENT: answer to WM_NCHITTEST meaning "pass the mouse to the window underneath".</summary>
    public const int HitTestTransparent = -1;

    /// <summary>WDA_EXCLUDEFROMCAPTURE: the window shows on the monitor but is left out of screenshots and screen capture (Windows 10 2004 and later).</summary>
    public const uint DisplayAffinityExcludeFromCapture = 0x00000011;

    public static long GetExtendedWindowStyle(IntPtr windowHandle)
    {
        // 64-bit user32 exports GetWindowLongPtrW; 32-bit user32 only exports GetWindowLongW
        // (the C header maps one name to the other with a macro, which P/Invoke cannot see).
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, ExtendedWindowStyleIndex).ToInt64()
            : GetWindowLong32(windowHandle, ExtendedWindowStyleIndex);
    }

    /// <returns>0 when the style was set, otherwise the Win32 error code.</returns>
    public static int SetExtendedWindowStyle(IntPtr windowHandle, long newExtendedStyle)
    {
        // SetWindowLongPtr returns the previous value, and 0 is a legal previous value. The documented
        // way to detect failure is to clear the thread's last error first and read it back after a 0 result.
        Marshal.SetLastSystemError(0);
        long previousExtendedStyle = IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, ExtendedWindowStyleIndex, new IntPtr(newExtendedStyle)).ToInt64()
            : SetWindowLong32(windowHandle, ExtendedWindowStyleIndex, unchecked((int)newExtendedStyle));

        return previousExtendedStyle == 0 ? Marshal.GetLastWin32Error() : 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfterWindowHandle,
        int windowLeftInPhysicalPixels,
        int windowTopInPhysicalPixels,
        int windowWidthInPhysicalPixels,
        int windowHeightInPhysicalPixels,
        uint setWindowPositionFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr windowHandle, uint displayAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowDisplayAffinity(IntPtr windowHandle, out uint displayAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr windowHandle, out OverlayNativeRectangle windowRectangle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int valueIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr windowHandle, int valueIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int valueIndex, IntPtr newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr windowHandle, int valueIndex, int newValue);
}

/// <summary>Win32 RECT: edges in physical pixels, right and bottom exclusive.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OverlayNativeRectangle
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}
