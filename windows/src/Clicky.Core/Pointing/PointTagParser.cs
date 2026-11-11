using System.Globalization;
using System.Text.RegularExpressions;

namespace Clicky.Core.Pointing;

/// <summary>
/// A pixel coordinate in the screenshot Claude was shown (top-left origin,
/// x grows right, y grows down).
/// </summary>
public readonly record struct ScreenshotPixelCoordinate(double X, double Y);

/// <summary>
/// Result of parsing a trailing [POINT:...] tag from Claude's reply.
/// </summary>
/// <param name="SpokenText">The reply with the tag removed. This is what gets spoken and stored in history.</param>
/// <param name="ScreenshotCoordinate">The parsed coordinate, or null for [POINT:none] or when no tag was found.</param>
/// <param name="ElementLabel">Short element description such as "search bar", "none" for [POINT:none], or null when there was no tag or no label.</param>
/// <param name="ScreenNumber">One-based screen number from ":screenN", or null to use the cursor screen.</param>
public sealed record PointTagParseResult(
    string SpokenText,
    ScreenshotPixelCoordinate? ScreenshotCoordinate,
    string? ElementLabel,
    int? ScreenNumber)
{
    public bool HasPointCoordinate => ScreenshotCoordinate is not null;
}

/// <summary>
/// Port of CompanionManager.parsePointingCoordinates from the Mac app. Matches
/// [POINT:none], [POINT:x,y], [POINT:x,y:label], [POINT:x,y:label:screenN] and
/// [POINT:x,y:screenN], but only when the tag is at the very end of the reply
/// (trailing whitespace allowed). Anything else is treated as "no tag".
/// </summary>
public static class PointTagParser
{
    // Same pattern as the Swift app. Group 1 = x, group 2 = y, group 3 = label,
    // group 4 = screen number. The label may not start with ']', ':' or whitespace
    // and may not contain ']' or ':'.
    private static readonly Regex PointTagPattern = new(
        @"\[POINT:(?:none|(\d+)\s*,\s*(\d+)(?::([^\]:\s][^\]:]*?))?(?::screen(\d+))?)\]\s*$",
        RegexOptions.CultureInvariant);

    public static PointTagParseResult Parse(string responseText)
    {
        Match pointTagMatch = PointTagPattern.Match(responseText);
        if (!pointTagMatch.Success)
        {
            return new PointTagParseResult(responseText, null, null, null);
        }

        string spokenText = responseText[..pointTagMatch.Index].Trim();

        Group xCoordinateGroup = pointTagMatch.Groups[1];
        Group yCoordinateGroup = pointTagMatch.Groups[2];
        bool isPointNoneTag = !xCoordinateGroup.Success || !yCoordinateGroup.Success;
        if (isPointNoneTag)
        {
            return new PointTagParseResult(spokenText, null, "none", null);
        }

        // Digit-only groups always parse; TryParse guards against values too large for double.
        bool didParseCoordinates =
            double.TryParse(xCoordinateGroup.Value, NumberStyles.None, CultureInfo.InvariantCulture, out double parsedXCoordinate)
            & double.TryParse(yCoordinateGroup.Value, NumberStyles.None, CultureInfo.InvariantCulture, out double parsedYCoordinate);
        if (!didParseCoordinates)
        {
            return new PointTagParseResult(spokenText, null, "none", null);
        }

        string? elementLabel = null;
        Group elementLabelGroup = pointTagMatch.Groups[3];
        if (elementLabelGroup.Success)
        {
            elementLabel = elementLabelGroup.Value.Trim(' ', '\t');
        }

        int? screenNumber = null;
        Group screenNumberGroup = pointTagMatch.Groups[4];
        if (screenNumberGroup.Success
            && int.TryParse(screenNumberGroup.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedScreenNumber))
        {
            screenNumber = parsedScreenNumber;
        }

        return new PointTagParseResult(
            spokenText,
            new ScreenshotPixelCoordinate(parsedXCoordinate, parsedYCoordinate),
            elementLabel,
            screenNumber);
    }
}
