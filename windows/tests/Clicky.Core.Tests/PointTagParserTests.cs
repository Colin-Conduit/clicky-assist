using Clicky.Core.Pointing;

namespace Clicky.Core.Tests;

public sealed class PointTagParserTests
{
    [Fact]
    public void ParsesCoordinateWithLabel()
    {
        PointTagParseResult parseResult = PointTagParser.Parse(
            "click that and you'll get all the color wheels and curves. [POINT:1100,42:color inspector]");

        Assert.Equal("click that and you'll get all the color wheels and curves.", parseResult.SpokenText);
        Assert.Equal(new ScreenshotPixelCoordinate(1100, 42), parseResult.ScreenshotCoordinate);
        Assert.Equal("color inspector", parseResult.ElementLabel);
        Assert.Null(parseResult.ScreenNumber);
        Assert.True(parseResult.HasPointCoordinate);
    }

    [Fact]
    public void ParsesCoordinateWithLabelAndScreenNumber()
    {
        PointTagParseResult parseResult = PointTagParser.Parse(
            "that's over on your other monitor, see the terminal window? [POINT:400,300:terminal:screen2]");

        Assert.Equal("that's over on your other monitor, see the terminal window?", parseResult.SpokenText);
        Assert.Equal(new ScreenshotPixelCoordinate(400, 300), parseResult.ScreenshotCoordinate);
        Assert.Equal("terminal", parseResult.ElementLabel);
        Assert.Equal(2, parseResult.ScreenNumber);
    }

    [Fact]
    public void ParsesCoordinateWithoutLabel()
    {
        PointTagParseResult parseResult = PointTagParser.Parse("right there [POINT:10,20]");

        Assert.Equal("right there", parseResult.SpokenText);
        Assert.Equal(new ScreenshotPixelCoordinate(10, 20), parseResult.ScreenshotCoordinate);
        Assert.Null(parseResult.ElementLabel);
        Assert.Null(parseResult.ScreenNumber);
    }

    [Fact]
    public void ScreenSuffixWithoutLabelIsReadAsTheLabelSameAsTheMacRegex()
    {
        // The Swift regex tries the label group first, so "screen3" is captured as the label
        // and no screen number is set. The system prompt always asks for a label before
        // ":screenN", and this port keeps the Mac behavior rather than changing it.
        PointTagParseResult parseResult = PointTagParser.Parse("over there [POINT:10,20:screen3]");

        Assert.Equal(new ScreenshotPixelCoordinate(10, 20), parseResult.ScreenshotCoordinate);
        Assert.Equal("screen3", parseResult.ElementLabel);
        Assert.Null(parseResult.ScreenNumber);
    }

    [Fact]
    public void AllowsSpacesAroundTheCommaAndTrailingWhitespace()
    {
        PointTagParseResult parseResult = PointTagParser.Parse("see it [POINT:285 , 11:source control]  \n");

        Assert.Equal("see it", parseResult.SpokenText);
        Assert.Equal(new ScreenshotPixelCoordinate(285, 11), parseResult.ScreenshotCoordinate);
        Assert.Equal("source control", parseResult.ElementLabel);
    }

    [Fact]
    public void ParsesPointNone()
    {
        PointTagParseResult parseResult = PointTagParser.Parse(
            "html stands for hypertext markup language. [POINT:none]");

        Assert.Equal("html stands for hypertext markup language.", parseResult.SpokenText);
        Assert.Null(parseResult.ScreenshotCoordinate);
        Assert.Equal("none", parseResult.ElementLabel);
        Assert.Null(parseResult.ScreenNumber);
        Assert.False(parseResult.HasPointCoordinate);
    }

    [Fact]
    public void ReturnsWholeTextWhenThereIsNoTag()
    {
        const string replyWithoutTag = "just a normal answer with no tag.";

        PointTagParseResult parseResult = PointTagParser.Parse(replyWithoutTag);

        Assert.Equal(replyWithoutTag, parseResult.SpokenText);
        Assert.Null(parseResult.ScreenshotCoordinate);
        Assert.Null(parseResult.ElementLabel);
    }

    [Theory]
    [InlineData("bad [POINT:abc,def:label]")]
    [InlineData("bad [POINT:-5,10:label]")]
    [InlineData("bad [POINT:5.5,10:label]")]
    [InlineData("bad [POINT:10:label]")]
    [InlineData("bad [POINT:10,20:label")]
    [InlineData("bad [POINT:10,20::label]")]
    [InlineData("bad [point:10,20:label]")]
    [InlineData("bad [POINT:10,20:label:screenX]")]
    public void TreatsMalformedTagsAsNoTag(string replyWithMalformedTag)
    {
        PointTagParseResult parseResult = PointTagParser.Parse(replyWithMalformedTag);

        Assert.Equal(replyWithMalformedTag, parseResult.SpokenText);
        Assert.Null(parseResult.ScreenshotCoordinate);
        Assert.Null(parseResult.ElementLabel);
    }

    [Fact]
    public void IgnoresTagThatIsNotAtTheEnd()
    {
        const string replyWithTagInMiddle = "look [POINT:10,20:button] and then keep reading";

        PointTagParseResult parseResult = PointTagParser.Parse(replyWithTagInMiddle);

        Assert.Equal(replyWithTagInMiddle, parseResult.SpokenText);
        Assert.Null(parseResult.ScreenshotCoordinate);
    }

    [Fact]
    public void UsesOnlyTheTrailingTagWhenThereAreSeveral()
    {
        PointTagParseResult parseResult = PointTagParser.Parse("first [POINT:1,2:a] then [POINT:30,40:b]");

        Assert.Equal("first [POINT:1,2:a] then", parseResult.SpokenText);
        Assert.Equal(new ScreenshotPixelCoordinate(30, 40), parseResult.ScreenshotCoordinate);
        Assert.Equal("b", parseResult.ElementLabel);
    }

    [Fact]
    public void TagOnlyReplyLeavesEmptySpokenText()
    {
        PointTagParseResult parseResult = PointTagParser.Parse("[POINT:640,360:center]");

        Assert.Equal(string.Empty, parseResult.SpokenText);
        Assert.Equal(new ScreenshotPixelCoordinate(640, 360), parseResult.ScreenshotCoordinate);
    }
}
