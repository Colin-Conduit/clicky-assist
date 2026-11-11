using System.Runtime.InteropServices;
using Clicky.Core.Pointing;
using Clicky.Windows.Display;

namespace Clicky.Windows.Tray;

/// <summary>
/// The notification area (tray) icon: the Windows counterpart of the Mac's NSStatusItem. Like the
/// Mac status item it only opens the companion panel, so it has no context menu; both left and
/// right clicks are reported through <see cref="TrayIconClicked"/>.
///
/// Create it on the WPF UI thread. NotifyIcon receives its messages through a hidden window that
/// belongs to the creating thread, and WPF's dispatcher pumps that thread's Win32 messages, so the
/// click event is raised on the UI thread.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private const string TrayIconTooltipText = "Clicky";

    /// <summary>The notification area's icon size at 100% scale (SM_CXSMICON at 96 DPI).</summary>
    private const int SmallTrayIconSizeInPixels = 16;

    /// <summary>The icon size used for every scale above 100% (Windows shrinks it to 20 or 24 px as needed).</summary>
    private const int LargeTrayIconSizeInPixels = 32;

    // The Mac glyph's proportions (makeClickyMenuBarIcon in MenuBarPanelManager.swift): an equilateral
    // triangle 70% of the icon wide, centered, rotated 35 degrees so the tip leans like a mouse pointer.
    private const double TriangleSideLengthAsFractionOfIconSize = 0.7;
    private const double TriangleRotationInDegrees = 35.0;

    /// <summary>
    /// DS.Colors.overlayCursorBlue (#3380FF). The Mac uses a black template image that macOS recolors
    /// for light and dark menu bars; the notification area has no template images, and this mid blue
    /// reads on both light and dark taskbars.
    /// </summary>
    private static readonly System.Drawing.Color TriangleFillColor = System.Drawing.Color.FromArgb(0x33, 0x80, 0xFF);

    private readonly System.Windows.Forms.NotifyIcon notifyIcon;
    private readonly RuntimeDrawnTrayIcon smallTriangleIcon;
    private readonly RuntimeDrawnTrayIcon largeTriangleIcon;
    private bool isDisposed;

    public TrayIconManager()
    {
        smallTriangleIcon = CreateTriangleIcon(SmallTrayIconSizeInPixels);
        largeTriangleIcon = CreateTriangleIcon(LargeTrayIconSizeInPixels);

        notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = ChooseIconForTaskbarScale().Icon,
            Text = TrayIconTooltipText,
        };
        notifyIcon.MouseClick += HandleNotifyIconMouseClick;
        notifyIcon.Visible = true;
    }

    /// <summary>
    /// Raised on the UI thread when the icon is clicked with the left or right button. The argument is
    /// the cursor position in physical pixels at click time, which the panel uses as its anchor.
    /// </summary>
    public event Action<PhysicalPixelPoint>? TrayIconClicked;

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;

        // Remove the icon from the notification area before the icon images it uses are destroyed.
        notifyIcon.MouseClick -= HandleNotifyIconMouseClick;
        notifyIcon.Visible = false;
        notifyIcon.Dispose();

        smallTriangleIcon.Release();
        largeTriangleIcon.Release();
    }

    /// <summary>
    /// NotifyIcon raises MouseClick on button-up. That ordering matters: the panel's outside-click
    /// hook acts on the button-down of the same click, so an open panel is already hidden (and the
    /// panel knows to stay closed) by the time this toggle request arrives.
    /// </summary>
    private void HandleNotifyIconMouseClick(object? sender, System.Windows.Forms.MouseEventArgs mouseEventArgs)
    {
        if (mouseEventArgs.Button is System.Windows.Forms.MouseButtons.Left or System.Windows.Forms.MouseButtons.Right)
        {
            TrayIconClicked?.Invoke(CursorPositionReader.GetCursorPositionInPhysicalPixels());
        }
    }

    /// <summary>
    /// The notification area draws icons at the small-icon size for the primary monitor's scale
    /// (16 px at 100%, 20 at 125%, 24 at 150%, 32 at 200%) and resizes whatever image it is given.
    /// Shrinking the 32 px drawing looks cleaner than enlarging the 16 px one, so any scale above
    /// 100% uses the 32 px icon. The choice is made once at startup.
    /// </summary>
    private RuntimeDrawnTrayIcon ChooseIconForTaskbarScale()
    {
        double primaryMonitorDpiScale = 1.0;
        try
        {
            DisplayMonitorInfo? primaryMonitor = DisplayMonitorEnumerator.EnumerateMonitors().FirstOrDefault(monitor => monitor.IsPrimary);
            if (primaryMonitor is not null)
            {
                primaryMonitorDpiScale = primaryMonitor.DpiScale;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Monitor enumeration failing is not worth losing the tray icon over; assume 100%.
        }

        double requiredTrayIconSizeInPixels = SmallTrayIconSizeInPixels * primaryMonitorDpiScale;
        return requiredTrayIconSizeInPixels > SmallTrayIconSizeInPixels ? largeTriangleIcon : smallTriangleIcon;
    }

    private static RuntimeDrawnTrayIcon CreateTriangleIcon(int iconSizeInPixels)
    {
        using System.Drawing.Bitmap triangleBitmap = new(iconSizeInPixels, iconSizeInPixels, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (System.Drawing.Graphics bitmapGraphics = System.Drawing.Graphics.FromImage(triangleBitmap))
        {
            bitmapGraphics.Clear(System.Drawing.Color.Transparent);
            bitmapGraphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // Treats pixel centers as half-integer coordinates, so the antialiased edges land where the
            // Mac geometry (which works in continuous coordinates) puts them.
            bitmapGraphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            using System.Drawing.SolidBrush triangleBrush = new(TriangleFillColor);
            bitmapGraphics.FillPolygon(triangleBrush, CalculateTriangleCorners(iconSizeInPixels));
        }

        // GetHicon copies the pixels, alpha included, into a new HICON. Icon.FromHandle wraps that
        // handle without taking ownership, so it has to be freed with DestroyIcon (see Release).
        IntPtr nativeIconHandle = triangleBitmap.GetHicon();
        return new RuntimeDrawnTrayIcon(System.Drawing.Icon.FromHandle(nativeIconHandle), nativeIconHandle);
    }

    /// <summary>
    /// The three corners of the Mac menu bar triangle, scaled to <paramref name="iconSizeInPixels"/>.
    /// The points are computed exactly as in makeClickyMenuBarIcon, in AppKit's coordinate system
    /// (origin bottom-left, y up), and only then flipped into GDI+ coordinates (origin top-left, y down).
    /// Flipping last keeps the 35 degree rotation identical, so the tip leans up and to the left.
    /// </summary>
    private static System.Drawing.PointF[] CalculateTriangleCorners(int iconSizeInPixels)
    {
        double triangleSideLength = iconSizeInPixels * TriangleSideLengthAsFractionOfIconSize;
        double iconCenterX = iconSizeInPixels * 0.5;
        double iconCenterY = iconSizeInPixels * 0.5;
        double triangleHeight = triangleSideLength * Math.Sqrt(3.0) / 2.0;

        // Tip above the center, base below it, so the triangle's centroid sits on the icon center.
        (double X, double Y)[] unrotatedCornersWithYUp =
        [
            (iconCenterX, iconCenterY + (triangleHeight / 1.5)),
            (iconCenterX - (triangleSideLength / 2), iconCenterY - (triangleHeight / 3)),
            (iconCenterX + (triangleSideLength / 2), iconCenterY - (triangleHeight / 3)),
        ];

        double rotationInRadians = TriangleRotationInDegrees * Math.PI / 180.0;
        double rotationCosine = Math.Cos(rotationInRadians);
        double rotationSine = Math.Sin(rotationInRadians);

        System.Drawing.PointF[] rotatedCornersWithYDown = new System.Drawing.PointF[unrotatedCornersWithYUp.Length];
        for (int cornerIndex = 0; cornerIndex < unrotatedCornersWithYUp.Length; cornerIndex++)
        {
            double offsetFromCenterX = unrotatedCornersWithYUp[cornerIndex].X - iconCenterX;
            double offsetFromCenterY = unrotatedCornersWithYUp[cornerIndex].Y - iconCenterY;

            double rotatedXWithYUp = iconCenterX + (rotationCosine * offsetFromCenterX) - (rotationSine * offsetFromCenterY);
            double rotatedYWithYUp = iconCenterY + (rotationSine * offsetFromCenterX) + (rotationCosine * offsetFromCenterY);

            rotatedCornersWithYDown[cornerIndex] = new System.Drawing.PointF(
                (float)rotatedXWithYUp,
                (float)(iconSizeInPixels - rotatedYWithYUp));
        }

        return rotatedCornersWithYDown;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    /// <summary>An icon drawn at runtime together with the HICON it wraps, which this class owns.</summary>
    private sealed record RuntimeDrawnTrayIcon(System.Drawing.Icon Icon, IntPtr NativeIconHandle)
    {
        public void Release()
        {
            Icon.Dispose();
            DestroyIcon(NativeIconHandle);
        }
    }
}
