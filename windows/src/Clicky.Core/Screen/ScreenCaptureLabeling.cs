using Clicky.Core.Pointing;

namespace Clicky.Core.Screen;

/// <summary>
/// Screen ordering and label text, ported from CompanionScreenCaptureUtility.swift.
/// The cursor screen is always first, so it is "screen 1", and the system prompt's
/// ":screenN" suffix indexes into this same order. The labels are model input, not
/// UI text, so they match the Mac strings exactly (including the em dash, written
/// as an escape so this file stays free of the literal character).
/// </summary>
public static class ScreenCaptureLabeling
{
    // Built from its code point (U+2014) so this source file never contains the character itself.
    private static readonly string EmDash = ((char)0x2014).ToString();

    public const string SingleScreenLabel = "user's screen (cursor is here)";

    /// <summary>
    /// Returns the label for the screen at <paramref name="zeroBasedScreenIndex"/> once the list
    /// has been ordered with <see cref="OrderCursorScreenFirst{T}"/>.
    /// </summary>
    public static string BuildScreenLabel(int zeroBasedScreenIndex, int screenCount, bool isCursorScreen)
    {
        if (screenCount == 1)
        {
            return SingleScreenLabel;
        }

        int oneBasedScreenNumber = zeroBasedScreenIndex + 1;
        if (isCursorScreen)
        {
            return $"screen {oneBasedScreenNumber} of {screenCount} {EmDash} cursor is on this screen (primary focus)";
        }

        return $"screen {oneBasedScreenNumber} of {screenCount} {EmDash} secondary screen";
    }

    /// <summary>Appends the pixel size so Claude's coordinate space matches the image it sees.</summary>
    public static string AppendImageDimensions(string screenLabel, ScreenshotDimensions screenshotDimensions)
    {
        return $"{screenLabel} (image dimensions: {screenshotDimensions.WidthInPixels}x{screenshotDimensions.HeightInPixels} pixels)";
    }

    /// <summary>
    /// Stable ordering that moves the cursor screen to the front and keeps every other
    /// monitor in its original enumeration order.
    /// </summary>
    public static IReadOnlyList<TMonitor> OrderCursorScreenFirst<TMonitor>(
        IReadOnlyList<TMonitor> monitorsInEnumerationOrder,
        Func<TMonitor, bool> isCursorScreen)
    {
        List<TMonitor> cursorScreens = monitorsInEnumerationOrder.Where(isCursorScreen).ToList();
        List<TMonitor> otherScreens = monitorsInEnumerationOrder.Where(monitor => !isCursorScreen(monitor)).ToList();
        return [.. cursorScreens, .. otherScreens];
    }
}
