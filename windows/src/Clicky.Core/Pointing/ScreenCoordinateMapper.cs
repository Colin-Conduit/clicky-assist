namespace Clicky.Core.Pointing;

/// <summary>
/// A monitor rectangle in physical pixels, in Windows virtual-screen coordinates
/// (top-left origin, can be negative for monitors left of or above the primary).
/// The app is per-monitor DPI aware v2, so Win32 reports these values unscaled.
/// </summary>
public readonly record struct PhysicalPixelRectangle(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public double CenterX => Left + (Width / 2.0);
    public double CenterY => Top + (Height / 2.0);

    /// <summary>Half-open containment so a point on the shared edge of two monitors belongs to exactly one.</summary>
    public bool ContainsPoint(double physicalPixelX, double physicalPixelY)
    {
        return physicalPixelX >= Left && physicalPixelX < Right
            && physicalPixelY >= Top && physicalPixelY < Bottom;
    }
}

/// <summary>A point in physical pixels, in virtual-screen coordinates.</summary>
public readonly record struct PhysicalPixelPoint(double X, double Y);

/// <summary>
/// A point in device-independent pixels (1/96 inch) relative to the top-left corner of
/// one monitor's overlay window. This is the coordinate space WPF draws in.
/// </summary>
public readonly record struct OverlayDipPoint(double X, double Y);

/// <summary>Pixel size of a screenshot as sent to Claude.</summary>
public readonly record struct ScreenshotDimensions(int WidthInPixels, int HeightInPixels);

/// <summary>
/// Pure coordinate math between the three spaces the app uses:
/// screenshot pixels (what Claude sees), physical pixels (what Win32 uses),
/// and overlay DIPs (what WPF draws in). Physical pixels are the hub; DIPs only
/// appear at the WPF edge, so DPI scaling is applied in exactly one place.
/// </summary>
public static class ScreenCoordinateMapper
{
    /// <summary>Longest edge of every screenshot sent to Claude, same as the Mac app.</summary>
    public const int MaximumScreenshotDimensionInPixels = 1280;

    /// <summary>
    /// Mirrors CompanionScreenCaptureUtility on the Mac: the longest edge becomes
    /// <paramref name="maximumDimensionInPixels"/> and the other edge keeps the
    /// aspect ratio, truncated to a whole pixel.
    /// </summary>
    public static ScreenshotDimensions ComputeScreenshotDimensions(
        int monitorWidthInPixels,
        int monitorHeightInPixels,
        int maximumDimensionInPixels = MaximumScreenshotDimensionInPixels)
    {
        if (monitorWidthInPixels <= 0 || monitorHeightInPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorWidthInPixels), "Monitor dimensions must be positive.");
        }

        double monitorAspectRatio = (double)monitorWidthInPixels / monitorHeightInPixels;

        if (monitorWidthInPixels >= monitorHeightInPixels)
        {
            int scaledHeightInPixels = (int)(maximumDimensionInPixels / monitorAspectRatio);
            return new ScreenshotDimensions(maximumDimensionInPixels, Math.Max(1, scaledHeightInPixels));
        }

        int scaledWidthInPixels = (int)(maximumDimensionInPixels * monitorAspectRatio);
        return new ScreenshotDimensions(Math.Max(1, scaledWidthInPixels), maximumDimensionInPixels);
    }

    /// <summary>
    /// Converts a coordinate Claude returned (in screenshot pixels) to a physical-pixel point
    /// on the monitor that screenshot came from. The coordinate is clamped to the screenshot
    /// first, exactly as the Mac app does, so an out-of-range answer still lands on screen.
    /// </summary>
    public static PhysicalPixelPoint MapScreenshotCoordinateToPhysicalPixels(
        ScreenshotPixelCoordinate screenshotCoordinate,
        ScreenshotDimensions screenshotDimensions,
        PhysicalPixelRectangle monitorBoundsInPhysicalPixels)
    {
        double clampedScreenshotX = Math.Clamp(screenshotCoordinate.X, 0, screenshotDimensions.WidthInPixels);
        double clampedScreenshotY = Math.Clamp(screenshotCoordinate.Y, 0, screenshotDimensions.HeightInPixels);

        double horizontalScaleFromScreenshotToMonitor = (double)monitorBoundsInPhysicalPixels.Width / screenshotDimensions.WidthInPixels;
        double verticalScaleFromScreenshotToMonitor = (double)monitorBoundsInPhysicalPixels.Height / screenshotDimensions.HeightInPixels;

        double monitorLocalPhysicalX = clampedScreenshotX * horizontalScaleFromScreenshotToMonitor;
        double monitorLocalPhysicalY = clampedScreenshotY * verticalScaleFromScreenshotToMonitor;

        // Windows and screenshots both use a top-left origin, so unlike the Mac
        // (AppKit is bottom-left) there is no Y flip here.
        return new PhysicalPixelPoint(
            monitorBoundsInPhysicalPixels.Left + monitorLocalPhysicalX,
            monitorBoundsInPhysicalPixels.Top + monitorLocalPhysicalY);
    }

    /// <summary>
    /// Converts a virtual-screen physical-pixel point to DIPs inside the overlay window that
    /// covers <paramref name="monitorBoundsInPhysicalPixels"/>.
    /// <paramref name="monitorDpiScale"/> is the monitor DPI divided by 96 (1.0 at 100%, 1.5 at 150%).
    /// </summary>
    public static OverlayDipPoint MapPhysicalPixelsToOverlayDips(
        PhysicalPixelPoint physicalPixelPoint,
        PhysicalPixelRectangle monitorBoundsInPhysicalPixels,
        double monitorDpiScale)
    {
        ValidateDpiScale(monitorDpiScale);

        return new OverlayDipPoint(
            (physicalPixelPoint.X - monitorBoundsInPhysicalPixels.Left) / monitorDpiScale,
            (physicalPixelPoint.Y - monitorBoundsInPhysicalPixels.Top) / monitorDpiScale);
    }

    /// <summary>Inverse of <see cref="MapPhysicalPixelsToOverlayDips"/>.</summary>
    public static PhysicalPixelPoint MapOverlayDipsToPhysicalPixels(
        OverlayDipPoint overlayDipPoint,
        PhysicalPixelRectangle monitorBoundsInPhysicalPixels,
        double monitorDpiScale)
    {
        ValidateDpiScale(monitorDpiScale);

        return new PhysicalPixelPoint(
            monitorBoundsInPhysicalPixels.Left + (overlayDipPoint.X * monitorDpiScale),
            monitorBoundsInPhysicalPixels.Top + (overlayDipPoint.Y * monitorDpiScale));
    }

    /// <summary>Overlay window size in DIPs for a monitor at the given scale.</summary>
    public static (double WidthInDips, double HeightInDips) ComputeOverlaySizeInDips(
        PhysicalPixelRectangle monitorBoundsInPhysicalPixels,
        double monitorDpiScale)
    {
        ValidateDpiScale(monitorDpiScale);
        return (monitorBoundsInPhysicalPixels.Width / monitorDpiScale, monitorBoundsInPhysicalPixels.Height / monitorDpiScale);
    }

    private static void ValidateDpiScale(double monitorDpiScale)
    {
        if (monitorDpiScale <= 0 || double.IsNaN(monitorDpiScale) || double.IsInfinity(monitorDpiScale))
        {
            throw new ArgumentOutOfRangeException(nameof(monitorDpiScale), "DPI scale must be a positive finite number.");
        }
    }
}
