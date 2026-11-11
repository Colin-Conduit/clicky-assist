using System.Net;
using System.Text;
using System.Text.Json;
using Clicky.Core.Claude;
using Clicky.Core.Conversation;

namespace Clicky.Core.Tests;

public sealed class ClaudeStreamingChatClientTests
{
    private static readonly Uri TestChatEndpointUrl = new("http://worker.test/chat");

    [Fact]
    public void RequestBodyMatchesMacLayout()
    {
        byte[] jpegBytes = [0xFF, 0xD8, 0xFF, 0xE0];
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D];
        ClaudeChatRequest chatRequest = new(
            [new LabeledImage(jpegBytes, "screen 1 label"), new LabeledImage(pngBytes, "screen 2 label")],
            "system prompt text",
            [new ConversationExchange("earlier question", "earlier answer")],
            "what is on my screen");

        string requestBodyJson = ClaudeStreamingChatClient.BuildRequestBodyJson(ClaudeModelIdentifiers.DefaultSonnetModelIdentifier, chatRequest);

        using JsonDocument requestBodyDocument = JsonDocument.Parse(requestBodyJson);
        JsonElement requestBodyRoot = requestBodyDocument.RootElement;
        Assert.Equal(ClaudeModelIdentifiers.DefaultSonnetModelIdentifier, requestBodyRoot.GetProperty("model").GetString());
        Assert.Equal(ClaudeStreamingChatClient.MaximumResponseTokens, requestBodyRoot.GetProperty("max_tokens").GetInt32());
        Assert.True(requestBodyRoot.GetProperty("stream").GetBoolean());
        Assert.Equal("system prompt text", requestBodyRoot.GetProperty("system").GetString());
        Assert.Equal("low", requestBodyRoot.GetProperty("output_config").GetProperty("effort").GetString());

        // Thinking cannot be disabled on the 5.5 models (HTTP 400), so the body must not try.
        Assert.False(requestBodyRoot.TryGetProperty("thinking", out _));

        JsonElement[] messages = [.. requestBodyRoot.GetProperty("messages").EnumerateArray()];
        Assert.Equal(3, messages.Length);
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("earlier question", messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("earlier answer", messages[1].GetProperty("content").GetString());

        JsonElement[] currentContentBlocks = [.. messages[2].GetProperty("content").EnumerateArray()];
        Assert.Equal(5, currentContentBlocks.Length);
        Assert.Equal("image", currentContentBlocks[0].GetProperty("type").GetString());
        Assert.Equal("image/jpeg", currentContentBlocks[0].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String(jpegBytes), currentContentBlocks[0].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("screen 1 label", currentContentBlocks[1].GetProperty("text").GetString());
        Assert.Equal("image/png", currentContentBlocks[2].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal("screen 2 label", currentContentBlocks[3].GetProperty("text").GetString());
        Assert.Equal("what is on my screen", currentContentBlocks[4].GetProperty("text").GetString());
    }

    [Fact]
    public void ExchangesWithAnEmptySideAreLeftOutOfTheRequest()
    {
        ClaudeChatRequest chatRequest = new(
            [],
            "system",
            [
                new ConversationExchange("point at the save button", string.Empty),
                new ConversationExchange("   ", "an answer to nothing"),
                new ConversationExchange("what is this", "it is a settings page"),
            ],
            "next question");

        using JsonDocument requestBodyDocument = JsonDocument.Parse(
            ClaudeStreamingChatClient.BuildRequestBodyJson(ClaudeModelIdentifiers.DefaultSonnetModelIdentifier, chatRequest));
        JsonElement[] messages = [.. requestBodyDocument.RootElement.GetProperty("messages").EnumerateArray()];

        Assert.Equal(3, messages.Length);
        Assert.Equal("what is this", messages[0].GetProperty("content").GetString());
        Assert.Equal("it is a settings page", messages[1].GetProperty("content").GetString());
        Assert.All(messages.Take(2), message => Assert.NotEqual(string.Empty, message.GetProperty("content").GetString()));
    }

    [Fact]
    public async Task ReportsTheStopReasonFromMessageDelta()
    {
        string streamText =
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
        using HttpClient fakeHttpClient = new(new FakeWorkerHandler(HttpStatusCode.OK, streamText, readChunkSizeInBytes: 7));
        ClaudeStreamingChatClient chatClient = new(fakeHttpClient, TestChatEndpointUrl, ClaudeModelIdentifiers.DefaultSonnetModelIdentifier);

        ClaudeStreamingResponse streamingResponse = await chatClient.StreamResponseAsync(BuildMinimalChatRequest(), null, CancellationToken.None);

        Assert.Equal("max_tokens", streamingResponse.StopReason);
        Assert.Equal("partial", streamingResponse.FullResponseText);
    }

    [Fact]
    public async Task StreamsAccumulatedTextAcrossSplitNetworkReads()
    {
        string streamText =
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"see the \"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"save button [POINT:10,20:save]\"}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
        using HttpClient fakeHttpClient = new(new FakeWorkerHandler(HttpStatusCode.OK, streamText, readChunkSizeInBytes: 5));
        ClaudeStreamingChatClient chatClient = new(fakeHttpClient, TestChatEndpointUrl, ClaudeModelIdentifiers.DefaultSonnetModelIdentifier);
        List<string> accumulatedTextUpdates = [];

        ClaudeStreamingResponse streamingResponse = await chatClient.StreamResponseAsync(
            BuildMinimalChatRequest(), accumulatedTextUpdates.Add, CancellationToken.None);

        Assert.Equal("see the save button [POINT:10,20:save]", streamingResponse.FullResponseText);
        Assert.True(streamingResponse.DidReceiveMessageStop);
        Assert.Equal(["see the ", "see the save button [POINT:10,20:save]"], accumulatedTextUpdates);
    }

    [Fact]
    public async Task InStreamErrorEventThrowsWithErrorType()
    {
        string streamText =
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\n" +
            "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n";
        using HttpClient fakeHttpClient = new(new FakeWorkerHandler(HttpStatusCode.OK, streamText, readChunkSizeInBytes: 9));
        ClaudeStreamingChatClient chatClient = new(fakeHttpClient, TestChatEndpointUrl, ClaudeModelIdentifiers.DefaultSonnetModelIdentifier);

        ClaudeApiException apiException = await Assert.ThrowsAsync<ClaudeApiException>(() =>
            chatClient.StreamResponseAsync(BuildMinimalChatRequest(), null, CancellationToken.None));

        Assert.Equal("overloaded_error", apiException.ErrorType);
        Assert.Null(apiException.HttpStatusCode);
    }

    [Fact]
    public async Task NonSuccessStatusThrowsWithBodyAndErrorType()
    {
        const string errorBody = "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"model: not found\"}}";
        using HttpClient fakeHttpClient = new(new FakeWorkerHandler(HttpStatusCode.BadRequest, errorBody, readChunkSizeInBytes: 1024));
        ClaudeStreamingChatClient chatClient = new(fakeHttpClient, TestChatEndpointUrl, "claude-unknown");

        ClaudeApiException apiException = await Assert.ThrowsAsync<ClaudeApiException>(() =>
            chatClient.StreamResponseAsync(BuildMinimalChatRequest(), null, CancellationToken.None));

        Assert.Equal(400, apiException.HttpStatusCode);
        Assert.Equal("invalid_request_error", apiException.ErrorType);
        Assert.Contains("model: not found", apiException.Message);
    }

    [Fact]
    public async Task SendsTheCurrentlySelectedModel()
    {
        FakeWorkerHandler fakeWorkerHandler = new(HttpStatusCode.OK, "data: {\"type\":\"message_stop\"}\n\n", readChunkSizeInBytes: 64);
        using HttpClient fakeHttpClient = new(fakeWorkerHandler);
        ClaudeStreamingChatClient chatClient = new(fakeHttpClient, TestChatEndpointUrl, ClaudeModelIdentifiers.DefaultSonnetModelIdentifier)
        {
            ModelIdentifier = ClaudeModelIdentifiers.OpusModelIdentifier,
        };

        await chatClient.StreamResponseAsync(BuildMinimalChatRequest(), null, CancellationToken.None);

        using JsonDocument sentBodyDocument = JsonDocument.Parse(fakeWorkerHandler.LastRequestBody!);
        Assert.Equal(ClaudeModelIdentifiers.OpusModelIdentifier, sentBodyDocument.RootElement.GetProperty("model").GetString());
        Assert.Equal(TestChatEndpointUrl, fakeWorkerHandler.LastRequestUri);
        Assert.Equal(HttpMethod.Post, fakeWorkerHandler.LastRequestMethod);
    }

    [Fact]
    public void ModelConstantsAndNormalization()
    {
        // The one place tests spell out the model ids: it pins the values the task specifies, so a
        // change to ClaudeModelIdentifiers.cs is a deliberate, visible test update.
        Assert.Equal("claude-sonnet-5-5", ClaudeModelIdentifiers.DefaultSonnetModelIdentifier);
        Assert.Equal("claude-opus-5-5", ClaudeModelIdentifiers.OpusModelIdentifier);
        Assert.Equal(ClaudeModelIdentifiers.DefaultSonnetModelIdentifier, ClaudeModelIdentifiers.NormalizeModelIdentifier("claude-sonnet-4-6"));
        Assert.Equal(ClaudeModelIdentifiers.OpusModelIdentifier, ClaudeModelIdentifiers.NormalizeModelIdentifier(ClaudeModelIdentifiers.OpusModelIdentifier));
    }

    private static ClaudeChatRequest BuildMinimalChatRequest()
    {
        return new ClaudeChatRequest([new LabeledImage([0xFF, 0xD8], "label")], "system", [], "prompt");
    }

    /// <summary>Returns a canned body through a stream that hands out at most N bytes per read.</summary>
    private sealed class FakeWorkerHandler(HttpStatusCode responseStatusCode, string responseBodyText, int readChunkSizeInBytes) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public HttpMethod? LastRequestMethod { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            LastRequestUri = request.RequestUri;
            LastRequestMethod = request.Method;

            return new HttpResponseMessage(responseStatusCode)
            {
                Content = new StreamContent(new SmallReadStream(Encoding.UTF8.GetBytes(responseBodyText), readChunkSizeInBytes)),
            };
        }
    }

    private sealed class SmallReadStream(byte[] streamBytes, int maximumBytesPerRead) : MemoryStream(streamBytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, maximumBytesPerRead));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, maximumBytesPerRead)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumBytesPerRead)], cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            base.ReadAsync(buffer, offset, Math.Min(count, maximumBytesPerRead), cancellationToken);
    }
}
