using Clicky.Core.Pointing;
using Clicky.Core.Screen;

namespace Clicky.Core.Tests;

public sealed class ScreenCoordinateMapperTests
{
    private const double CoordinatePrecision = 6;

    [Theory]
    [InlineData(1920, 1080, 1280, 720)]
    [InlineData(2560, 1440, 1280, 720)]
    [InlineData(3840, 2160, 1280, 720)]
    [InlineData(2560, 1600, 1280, 800)]
    [InlineData(1080, 1920, 720, 1280)]
    [InlineData(3440, 1440, 1280, 535)]
    public void ScreenshotDimensionsKeepAspectRatioWithLongestEdge1280(int monitorWidth, int monitorHeight, int expectedWidth, int expectedHeight)
    {
        ScreenshotDimensions screenshotDimensions = ScreenCoordinateMapper.ComputeScreenshotDimensions(monitorWidth, monitorHeight);

        Assert.Equal(new ScreenshotDimensions(expectedWidth, expectedHeight), screenshotDimensions);
    }

    [Fact]
    public void MapsScreenshotPointToPhysicalPixelsAndDipsAt100PercentScaling()
    {
        PhysicalPixelRectangle primaryMonitorBounds = new(0, 0, 1920, 1080);
        ScreenshotDimensions screenshotDimensions = ScreenCoordinateMapper.ComputeScreenshotDimensions(1920, 1080);

        PhysicalPixelPoint physicalPoint = ScreenCoordinateMapper.MapScreenshotCoordinateToPhysicalPixels(
            new ScreenshotPixelCoordinate(640, 360), screenshotDimensions, primaryMonitorBounds);
        OverlayDipPoint overlayPoint = ScreenCoordinateMapper.MapPhysicalPixelsToOverlayDips(physicalPoint, primaryMonitorBounds, monitorDpiScale: 1.0);

        Assert.Equal(960, physicalPoint.X, CoordinatePrecision);
        Assert.Equal(540, physicalPoint.Y, CoordinatePrecision);
        Assert.Equal(960, overlayPoint.X, CoordinatePrecision);
        Assert.Equal(540, overlayPoint.Y, CoordinatePrecision);
    }

    [Fact]
    public void MapsScreenshotPointToPhysicalPixelsAndDipsAt150PercentScaling()
    {
        // A 2560x1440 panel at 150% is 1706.67 x 960 DIPs.
        PhysicalPixelRectangle primaryMonitorBounds = new(0, 0, 2560, 1440);
        ScreenshotDimensions screenshotDimensions = ScreenCoordinateMapper.ComputeScreenshotDimensions(2560, 1440);

        PhysicalPixelPoint physicalPoint = ScreenCoordinateMapper.MapScreenshotCoordinateToPhysicalPixels(
            new ScreenshotPixelCoordinate(640, 360), screenshotDimensions, primaryMonitorBounds);
        OverlayDipPoint overlayPoint = ScreenCoordinateMapper.MapPhysicalPixelsToOverlayDips(physicalPoint, primaryMonitorBounds, monitorDpiScale: 1.5);

        Assert.Equal(1280, physicalPoint.X, CoordinatePrecision);
        Assert.Equal(720, physicalPoint.Y, CoordinatePrecision);
        Assert.Equal(853.333333, overlayPoint.X, 5);
        Assert.Equal(480, overlayPoint.Y, CoordinatePrecision);

        (double overlayWidthInDips, double overlayHeightInDips) = ScreenCoordinateMapper.ComputeOverlaySizeInDips(primaryMonitorBounds, 1.5);
        Assert.Equal(1706.666667, overlayWidthInDips, 5);
        Assert.Equal(960, overlayHeightInDips, CoordinatePrecision);
    }

    [Fact]
    public void MapsOntoSecondaryMonitorAt150PercentWithNonZeroOrigin()
    {
        // Primary 1920x1080 at 100% on the left, secondary 2560x1440 at 150% to its right.
        PhysicalPixelRectangle secondaryMonitorBounds = new(1920, 0, 2560, 1440);
        ScreenshotDimensions screenshotDimensions = ScreenCoordinateMapper.ComputeScreenshotDimensions(2560, 1440);

        PhysicalPixelPoint physicalPoint = ScreenCoordinateMapper.MapScreenshotCoordinateToPhysicalPixels(
            new ScreenshotPixelCoordinate(100, 50), screenshotDimensions, secondaryMonitorBounds);
        OverlayDipPoint overlayPoint = ScreenCoordinateMapper.MapPhysicalPixelsToOverlayDips(physicalPoint, secondaryMonitorBounds, 1.5);

        Assert.Equal(1920 + 200, physicalPoint.X, CoordinatePrecision);
        Assert.Equal(100, physicalPoint.Y, CoordinatePrecision);
        Assert.Equal(133.333333, overlayPoint.X, 5);
        Assert.Equal(66.666667, overlayPoint.Y, 5);
    }

    [Fact]
    public void HandlesMonitorsWithNegativeOrigins()
    {
        // A monitor to the left of and above the primary has negative virtual-screen coordinates.
        PhysicalPixelRectangle leftMonitorBounds = new(-1920, -200, 1920, 1080);
        ScreenshotDimensions screenshotDimensions = ScreenCoordinateMapper.ComputeScreenshotDimensions(1920, 1080);

        PhysicalPixelPoint physicalPoint = ScreenCoordinateMapper.MapScreenshotCoordinateToPhysicalPixels(
            new ScreenshotPixelCoordinate(0, 0), screenshotDimensions, leftMonitorBounds);
        OverlayDipPoint overlayPoint = ScreenCoordinateMapper.MapPhysicalPixelsToOverlayDips(physicalPoint, leftMonitorBounds, 1.0);

        Assert.Equal(-1920, physicalPoint.X, CoordinatePrecision);
        Assert.Equal(-200, physicalPoint.Y, CoordinatePrecision);
        Assert.Equal(0, overlayPoint.X, CoordinatePrecision);
        Assert.Equal(0, overlayPoint.Y, CoordinatePrecision);
    }

    [Fact]
    public void ClampsOutOfRangeCoordinatesToTheScreenshot()
    {
        PhysicalPixelRectangle monitorBounds = new(0, 0, 2560, 1440);
        ScreenshotDimensions screenshotDimensions = new(1280, 720);

        PhysicalPixelPoint physicalPoint = ScreenCoordinateMapper.MapScreenshotCoordinateToPhysicalPixels(
            new ScreenshotPixelCoordinate(5000, 9000), screenshotDimensions, monitorBounds);

        Assert.Equal(2560, physicalPoint.X, CoordinatePrecision);
        Assert.Equal(1440, physicalPoint.Y, CoordinatePrecision);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void DipConversionRoundTrips(double monitorDpiScale)
    {
        PhysicalPixelRectangle monitorBounds = new(1920, -300, 2560, 1440);
        PhysicalPixelPoint originalPhysicalPoint = new(2345.5, 700.25);

        OverlayDipPoint overlayPoint = ScreenCoordinateMapper.MapPhysicalPixelsToOverlayDips(originalPhysicalPoint, monitorBounds, monitorDpiScale);
        PhysicalPixelPoint roundTrippedPhysicalPoint = ScreenCoordinateMapper.MapOverlayDipsToPhysicalPixels(overlayPoint, monitorBounds, monitorDpiScale);

        Assert.Equal(originalPhysicalPoint.X, roundTrippedPhysicalPoint.X, CoordinatePrecision);
        Assert.Equal(originalPhysicalPoint.Y, roundTrippedPhysicalPoint.Y, CoordinatePrecision);
    }

    [Fact]
    public void RejectsNonPositiveDpiScale()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScreenCoordinateMapper.MapPhysicalPixelsToOverlayDips(new PhysicalPixelPoint(0, 0), new PhysicalPixelRectangle(0, 0, 10, 10), 0));
    }

    [Fact]
    public void MonitorContainmentIsHalfOpenSoSharedEdgesBelongToOneMonitor()
    {
        PhysicalPixelRectangle leftMonitorBounds = new(0, 0, 1920, 1080);
        PhysicalPixelRectangle rightMonitorBounds = new(1920, 0, 1920, 1080);

        Assert.False(leftMonitorBounds.ContainsPoint(1920, 500));
        Assert.True(rightMonitorBounds.ContainsPoint(1920, 500));
        Assert.True(leftMonitorBounds.ContainsPoint(1919.9, 500));
    }

    [Fact]
    public void PointTargetUsesNamedScreenNumber()
    {
        CapturedScreen cursorScreen = BuildCapturedScreen(isCursorScreen: true, new PhysicalPixelRectangle(0, 0, 1920, 1080), 1.0);
        CapturedScreen secondaryScreen = BuildCapturedScreen(isCursorScreen: false, new PhysicalPixelRectangle(1920, 0, 2560, 1440), 1.5);
        PointTagParseResult parseResult = PointTagParser.Parse("over there [POINT:640,360:terminal:screen2]");

        PointTarget? pointTarget = PointTargetResolver.ResolvePointTarget(parseResult, [cursorScreen, secondaryScreen]);

        Assert.NotNull(pointTarget);
        Assert.Same(secondaryScreen, pointTarget.TargetScreen);
        Assert.Equal(1920 + 1280, pointTarget.TargetLocationInPhysicalPixels.X, CoordinatePrecision);
        Assert.Equal(720, pointTarget.TargetLocationInPhysicalPixels.Y, CoordinatePrecision);
        Assert.Equal("terminal", pointTarget.ElementLabel);
    }

    [Theory]
    [InlineData("here [POINT:10,10:button]")]
    [InlineData("here [POINT:10,10:button:screen9]")]
    [InlineData("here [POINT:10,10:button:screen0]")]
    public void PointTargetFallsBackToCursorScreen(string replyText)
    {
        CapturedScreen secondaryScreen = BuildCapturedScreen(isCursorScreen: false, new PhysicalPixelRectangle(1920, 0, 1920, 1080), 1.0);
        CapturedScreen cursorScreen = BuildCapturedScreen(isCursorScreen: true, new PhysicalPixelRectangle(0, 0, 1920, 1080), 1.0);

        PointTarget? pointTarget = PointTargetResolver.ResolvePointTarget(PointTagParser.Parse(replyText), [secondaryScreen, cursorScreen]);

        Assert.NotNull(pointTarget);
        Assert.Same(cursorScreen, pointTarget.TargetScreen);
    }

    [Fact]
    public void PointNoneProducesNoTarget()
    {
        CapturedScreen cursorScreen = BuildCapturedScreen(isCursorScreen: true, new PhysicalPixelRectangle(0, 0, 1920, 1080), 1.0);

        Assert.Null(PointTargetResolver.ResolvePointTarget(PointTagParser.Parse("nothing to show [POINT:none]"), [cursorScreen]));
    }

    private static CapturedScreen BuildCapturedScreen(bool isCursorScreen, PhysicalPixelRectangle monitorBounds, double monitorDpiScale)
    {
        return new CapturedScreen(
            JpegImageBytes: [0xFF, 0xD8],
            ScreenLabel: "test screen",
            IsCursorScreen: isCursorScreen,
            MonitorDeviceName: "TEST",
            MonitorBoundsInPhysicalPixels: monitorBounds,
            MonitorDpiScale: monitorDpiScale,
            ScreenshotDimensions: ScreenCoordinateMapper.ComputeScreenshotDimensions(monitorBounds.Width, monitorBounds.Height));
    }
}
