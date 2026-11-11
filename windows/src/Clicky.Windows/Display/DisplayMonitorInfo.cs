using System.ComponentModel;
using System.Runtime.InteropServices;
using Clicky.Core.Pointing;

namespace Clicky.Windows.Display;

/// <summary>
/// One connected monitor, in physical pixels. The app is per-monitor DPI aware v2,
/// so Win32 hands back unscaled coordinates and the DPI of each monitor separately.
/// </summary>
/// <param name="MonitorHandle">HMONITOR, valid until the display configuration changes.</param>
/// <param name="DeviceName">Win32 device name such as \\.\DISPLAY1.</param>
/// <param name="BoundsInPhysicalPixels">Full monitor rectangle in virtual-screen coordinates.</param>
/// <param name="WorkAreaInPhysicalPixels">Monitor rectangle minus the taskbar and docked app bars.</param>
/// <param name="DpiScale">Effective DPI divided by 96 (1.0 at 100%, 1.5 at 150%).</param>
/// <param name="IsPrimary">True for the primary monitor (the one whose top-left is 0,0).</param>
public sealed record DisplayMonitorInfo(
    IntPtr MonitorHandle,
    string DeviceName,
    PhysicalPixelRectangle BoundsInPhysicalPixels,
    PhysicalPixelRectangle WorkAreaInPhysicalPixels,
    double DpiScale,
    bool IsPrimary);

/// <summary>Lists monitors with EnumDisplayMonitors, GetMonitorInfo and GetDpiForMonitor.</summary>
public static class DisplayMonitorEnumerator
{
    private const int MonitorInfoFlagPrimary = 0x00000001;
    private const int MonitorDpiTypeEffective = 0;
    private const double DefaultDpi = 96.0;

    /// <summary>Monitors in the order Windows enumerates them.</summary>
    public static IReadOnlyList<DisplayMonitorInfo> EnumerateMonitors()
    {
        List<DisplayMonitorInfo> enumeratedMonitors = [];

        bool EnumerateSingleMonitor(IntPtr monitorHandle, IntPtr deviceContextHandle, ref NativeRectangle monitorRectangle, IntPtr callbackData)
        {
            DisplayMonitorInfo? monitorInfo = ReadMonitorInfo(monitorHandle);
            if (monitorInfo is not null)
            {
                enumeratedMonitors.Add(monitorInfo);
            }

            return true;
        }

        MonitorEnumerationCallback monitorEnumerationCallback = EnumerateSingleMonitor;
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, monitorEnumerationCallback, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumDisplayMonitors failed.");
        }

        GC.KeepAlive(monitorEnumerationCallback);
        return enumeratedMonitors;
    }

    /// <summary>The monitor containing the point, or the nearest one if the point is off every monitor.</summary>
    public static DisplayMonitorInfo? FindMonitorContainingPoint(IReadOnlyList<DisplayMonitorInfo> monitors, PhysicalPixelPoint physicalPixelPoint)
    {
        DisplayMonitorInfo? containingMonitor = monitors.FirstOrDefault(monitor =>
            monitor.BoundsInPhysicalPixels.ContainsPoint(physicalPixelPoint.X, physicalPixelPoint.Y));
        if (containingMonitor is not null)
        {
            return containingMonitor;
        }

        return monitors
            .OrderBy(monitor => Math.Pow(monitor.BoundsInPhysicalPixels.CenterX - physicalPixelPoint.X, 2) + Math.Pow(monitor.BoundsInPhysicalPixels.CenterY - physicalPixelPoint.Y, 2))
            .FirstOrDefault();
    }

    private static DisplayMonitorInfo? ReadMonitorInfo(IntPtr monitorHandle)
    {
        NativeMonitorInfoEx nativeMonitorInfo = new() { StructureSizeInBytes = Marshal.SizeOf<NativeMonitorInfoEx>() };
        if (!GetMonitorInfo(monitorHandle, ref nativeMonitorInfo))
        {
            return null;
        }

        double dpiScale = 1.0;
        if (GetDpiForMonitor(monitorHandle, MonitorDpiTypeEffective, out uint horizontalDpi, out _) == 0 && horizontalDpi > 0)
        {
            dpiScale = horizontalDpi / DefaultDpi;
        }

        return new DisplayMonitorInfo(
            monitorHandle,
            nativeMonitorInfo.DeviceName,
            nativeMonitorInfo.MonitorRectangle.ToPhysicalPixelRectangle(),
            nativeMonitorInfo.WorkAreaRectangle.ToPhysicalPixelRectangle(),
            dpiScale,
            (nativeMonitorInfo.Flags & MonitorInfoFlagPrimary) != 0);
    }

    private delegate bool MonitorEnumerationCallback(IntPtr monitorHandle, IntPtr deviceContextHandle, ref NativeRectangle monitorRectangle, IntPtr callbackData);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly PhysicalPixelRectangle ToPhysicalPixelRectangle() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeMonitorInfoEx
    {
        public int StructureSizeInBytes;
        public NativeRectangle MonitorRectangle;
        public NativeRectangle WorkAreaRectangle;
        public int Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContextHandle, IntPtr clipRectangle, MonitorEnumerationCallback monitorEnumerationCallback, IntPtr callbackData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref NativeMonitorInfoEx monitorInfo);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitorHandle, int dpiType, out uint horizontalDpi, out uint verticalDpi);
}

/// <summary>Reads the mouse position in physical virtual-screen pixels.</summary>
public static class CursorPositionReader
{
    public static PhysicalPixelPoint GetCursorPositionInPhysicalPixels()
    {
        // GetPhysicalCursorPos would also work; for a PerMonitorV2 process GetCursorPos already
        // returns physical pixels, and it behaves better while the secure desktop is up.
        return GetCursorPos(out NativePoint cursorPoint)
            ? new PhysicalPixelPoint(cursorPoint.X, cursorPoint.Y)
            : new PhysicalPixelPoint(0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint cursorPoint);
}
