using Clicky.Core.Pointing;

namespace Clicky.Core.Screen;

/// <summary>
/// One monitor's screenshot, ready to send to Claude, plus the geometry needed to
/// map Claude's answer back onto that monitor.
/// </summary>
/// <param name="JpegImageBytes">JPEG at <see cref="ScreenshotDimensions"/>, longest edge 1280 pixels.</param>
/// <param name="ScreenLabel">Label text sent next to the image, for example "user's screen (cursor is here)".</param>
/// <param name="IsCursorScreen">True for the monitor the mouse cursor was on at capture time.</param>
/// <param name="MonitorDeviceName">Win32 device name such as \\.\DISPLAY1, for logs and debugging.</param>
/// <param name="MonitorBoundsInPhysicalPixels">Monitor rectangle in virtual-screen physical pixels.</param>
/// <param name="MonitorDpiScale">Monitor DPI divided by 96.</param>
/// <param name="ScreenshotDimensions">Pixel size of <see cref="JpegImageBytes"/>; Claude's coordinates are in this space.</param>
public sealed record CapturedScreen(
    byte[] JpegImageBytes,
    string ScreenLabel,
    bool IsCursorScreen,
    string MonitorDeviceName,
    PhysicalPixelRectangle MonitorBoundsInPhysicalPixels,
    double MonitorDpiScale,
    ScreenshotDimensions ScreenshotDimensions)
{
    /// <summary>The label plus the pixel dimensions, which is the exact text block Claude receives after the image.</summary>
    public string LabelWithImageDimensions => ScreenCaptureLabeling.AppendImageDimensions(ScreenLabel, ScreenshotDimensions);
}
