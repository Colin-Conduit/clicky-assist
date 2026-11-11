using System.Text;
using Clicky.Core.Claude;

namespace Clicky.Core.Tests;

public sealed class ClaudeServerSentEventParserTests
{
    private const string TypicalAnthropicStream =
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\"}}\n\n" +
        "event: content_block_start\n" +
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "event: ping\n" +
        "data: {\"type\": \"ping\"}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hello \"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"there [POINT:none]\"}}\n\n" +
        "event: content_block_stop\n" +
        "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    [Fact]
    public void ParsesTextDeltasAndMessageStopFromOneChunk()
    {
        ClaudeServerSentEventParser serverSentEventParser = new();

        List<ClaudeStreamEvent> parsedEvents = [.. serverSentEventParser.AppendBytes(Encoding.UTF8.GetBytes(TypicalAnthropicStream))];
        parsedEvents.AddRange(serverSentEventParser.CompleteStream());

        Assert.Collection(
            parsedEvents,
            firstEvent => Assert.Equal(new ClaudeTextDeltaEvent("hello "), firstEvent),
            secondEvent => Assert.Equal(new ClaudeTextDeltaEvent("there [POINT:none]"), secondEvent),
            thirdEvent => Assert.IsType<ClaudeMessageStopEvent>(thirdEvent));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64)]
    public void ProducesTheSameEventsWhenTheStreamIsSplitIntoArbitraryChunks(int chunkSizeInBytes)
    {
        byte[] streamBytes = Encoding.UTF8.GetBytes(TypicalAnthropicStream);
        ClaudeServerSentEventParser serverSentEventParser = new();
        List<ClaudeStreamEvent> parsedEvents = [];

        for (int chunkStartIndex = 0; chunkStartIndex < streamBytes.Length; chunkStartIndex += chunkSizeInBytes)
        {
            int chunkLength = Math.Min(chunkSizeInBytes, streamBytes.Length - chunkStartIndex);
            parsedEvents.AddRange(serverSentEventParser.AppendBytes(streamBytes.AsSpan(chunkStartIndex, chunkLength)));
        }

        parsedEvents.AddRange(serverSentEventParser.CompleteStream());

        string reassembledText = string.Concat(parsedEvents.OfType<ClaudeTextDeltaEvent>().Select(textDelta => textDelta.TextChunk));
        Assert.Equal("hello there [POINT:none]", reassembledText);
        Assert.Single(parsedEvents.OfType<ClaudeMessageStopEvent>());
    }

    [Fact]
    public void KeepsMultiByteUtf8CharactersIntactWhenSplitMidCharacter()
    {
        // An accented e (two UTF-8 bytes) and a code point above U+FFFF (four UTF-8 bytes), built from
        // code points so the source file itself stays plain ASCII.
        string textWithMultiByteCharacters = "caf" + (char)0x00E9 + " " + char.ConvertFromUtf32(0x1F600);
        string streamText =
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"" + textWithMultiByteCharacters + "\"}}\n\n";
        byte[] streamBytes = Encoding.UTF8.GetBytes(streamText);

        ClaudeServerSentEventParser serverSentEventParser = new();
        List<ClaudeStreamEvent> parsedEvents = [];
        foreach (byte singleByte in streamBytes)
        {
            parsedEvents.AddRange(serverSentEventParser.AppendBytes([singleByte]));
        }

        ClaudeTextDeltaEvent textDeltaEvent = Assert.IsType<ClaudeTextDeltaEvent>(Assert.Single(parsedEvents));
        Assert.Equal(textWithMultiByteCharacters, textDeltaEvent.TextChunk);
    }

    [Fact]
    public void HandlesCarriageReturnLineFeedLineEndings()
    {
        string crlfStream = TypicalAnthropicStream.Replace("\n", "\r\n");
        ClaudeServerSentEventParser serverSentEventParser = new();

        List<ClaudeStreamEvent> parsedEvents = [.. serverSentEventParser.AppendBytes(Encoding.UTF8.GetBytes(crlfStream))];

        Assert.Equal(2, parsedEvents.OfType<ClaudeTextDeltaEvent>().Count());
        Assert.Single(parsedEvents.OfType<ClaudeMessageStopEvent>());
    }

    [Fact]
    public void ParsesInStreamErrorEvent()
    {
        const string errorStream =
            "event: error\n" +
            "data: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n";
        ClaudeServerSentEventParser serverSentEventParser = new();

        IReadOnlyList<ClaudeStreamEvent> parsedEvents = serverSentEventParser.AppendBytes(Encoding.UTF8.GetBytes(errorStream));

        ClaudeStreamErrorEvent errorEvent = Assert.IsType<ClaudeStreamErrorEvent>(Assert.Single(parsedEvents));
        Assert.Equal("overloaded_error", errorEvent.ErrorType);
        Assert.Equal("Overloaded", errorEvent.ErrorMessage);
    }

    [Fact]
    public void ErrorEventSplitAcrossChunksIsStillReported()
    {
        byte[] errorStreamBytes = Encoding.UTF8.GetBytes(
            "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"Internal server error\"}}\n\n");
        ClaudeServerSentEventParser serverSentEventParser = new();

        List<ClaudeStreamEvent> parsedEvents = [];
        parsedEvents.AddRange(serverSentEventParser.AppendBytes(errorStreamBytes.AsSpan(0, 20)));
        Assert.Empty(parsedEvents);
        parsedEvents.AddRange(serverSentEventParser.AppendBytes(errorStreamBytes.AsSpan(20)));

        ClaudeStreamErrorEvent errorEvent = Assert.IsType<ClaudeStreamErrorEvent>(Assert.Single(parsedEvents));
        Assert.Equal("api_error", errorEvent.ErrorType);
    }

    [Fact]
    public void FlushesFinalEventWhenStreamEndsWithoutTrailingBlankLine()
    {
        ClaudeServerSentEventParser serverSentEventParser = new();

        IReadOnlyList<ClaudeStreamEvent> eventsBeforeEnd = serverSentEventParser.AppendBytes(
            Encoding.UTF8.GetBytes("data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"tail\"}}"));
        IReadOnlyList<ClaudeStreamEvent> eventsAtEnd = serverSentEventParser.CompleteStream();

        Assert.Empty(eventsBeforeEnd);
        Assert.Equal(new ClaudeTextDeltaEvent("tail"), Assert.Single(eventsAtEnd));
    }

    [Fact]
    public void IgnoresCommentsMalformedJsonAndNonTextDeltas()
    {
        const string noisyStream =
            ": keep-alive comment\n\n" +
            "data: {not json\n\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{}\"}}\n\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"kept\"}}\n\n";
        ClaudeServerSentEventParser serverSentEventParser = new();

        IReadOnlyList<ClaudeStreamEvent> parsedEvents = serverSentEventParser.AppendBytes(Encoding.UTF8.GetBytes(noisyStream));

        Assert.Equal(new ClaudeTextDeltaEvent("kept"), Assert.Single(parsedEvents));
    }

    [Fact]
    public void TreatsDoneMarkerAsEndOfStream()
    {
        ClaudeServerSentEventParser serverSentEventParser = new();

        IReadOnlyList<ClaudeStreamEvent> parsedEvents = serverSentEventParser.AppendBytes(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));

        Assert.IsType<ClaudeMessageStopEvent>(Assert.Single(parsedEvents));
    }

    [Fact]
    public void AcceptsDataFieldWithoutSpaceAfterColon()
    {
        ClaudeServerSentEventParser serverSentEventParser = new();

        IReadOnlyList<ClaudeStreamEvent> parsedEvents = serverSentEventParser.AppendBytes(
            Encoding.UTF8.GetBytes("data:{\"type\":\"message_stop\"}\n\n"));

        Assert.IsType<ClaudeMessageStopEvent>(Assert.Single(parsedEvents));
    }
}
