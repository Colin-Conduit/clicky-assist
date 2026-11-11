using Clicky.Core.Screen;

namespace Clicky.Core.Pointing;

/// <summary>
/// Where the blue cursor should fly: a physical-pixel point and the screen it is on.
/// </summary>
public sealed record PointTarget(PhysicalPixelPoint TargetLocationInPhysicalPixels, CapturedScreen TargetScreen, string? ElementLabel);

/// <summary>
/// Picks the screenshot Claude was pointing into and converts its coordinate to a
/// physical-pixel target. Port of the target-screen selection in
/// CompanionManager.sendTranscriptToClaudeWithScreenshot.
/// </summary>
public static class PointTargetResolver
{
    /// <summary>
    /// Uses ":screenN" when it names a captured screen, otherwise falls back to the cursor
    /// screen. Returns null when neither exists.
    /// </summary>
    public static CapturedScreen? ResolveTargetScreen(int? oneBasedScreenNumber, IReadOnlyList<CapturedScreen> capturedScreensInPromptOrder)
    {
        if (oneBasedScreenNumber is int requestedScreenNumber
            && requestedScreenNumber >= 1
            && requestedScreenNumber <= capturedScreensInPromptOrder.Count)
        {
            return capturedScreensInPromptOrder[requestedScreenNumber - 1];
        }

        return capturedScreensInPromptOrder.FirstOrDefault(capturedScreen => capturedScreen.IsCursorScreen);
    }

    /// <summary>
    /// Returns null for [POINT:none], for replies without a tag, and when no screen matches.
    /// </summary>
    public static PointTarget? ResolvePointTarget(PointTagParseResult pointTagParseResult, IReadOnlyList<CapturedScreen> capturedScreensInPromptOrder)
    {
        if (pointTagParseResult.ScreenshotCoordinate is not ScreenshotPixelCoordinate screenshotCoordinate)
        {
            return null;
        }

        CapturedScreen? targetScreen = ResolveTargetScreen(pointTagParseResult.ScreenNumber, capturedScreensInPromptOrder);
        if (targetScreen is null)
        {
            return null;
        }

        PhysicalPixelPoint targetLocationInPhysicalPixels = ScreenCoordinateMapper.MapScreenshotCoordinateToPhysicalPixels(
            screenshotCoordinate,
            targetScreen.ScreenshotDimensions,
            targetScreen.MonitorBoundsInPhysicalPixels);

        return new PointTarget(targetLocationInPhysicalPixels, targetScreen, pointTagParseResult.ElementLabel);
    }
}
