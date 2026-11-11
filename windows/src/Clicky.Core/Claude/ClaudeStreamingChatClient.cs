using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Clicky.Core.Conversation;

namespace Clicky.Core.Claude;

/// <summary>An image plus the text label sent right after it (for example the screen label with dimensions).</summary>
public sealed record LabeledImage(byte[] ImageBytes, string Label);

/// <summary>Everything one Claude turn needs, independent of the model choice.</summary>
public sealed record ClaudeChatRequest(
    IReadOnlyList<LabeledImage> LabeledImages,
    string SystemPrompt,
    IReadOnlyList<ConversationExchange> ConversationHistory,
    string UserPrompt);

/// <summary>Final result of a streamed reply.</summary>
/// <param name="StopReason">From the message_delta event, for example "end_turn", "max_tokens" or "refusal"; null if the stream never sent one.</param>
public sealed record ClaudeStreamingResponse(string FullResponseText, TimeSpan Duration, bool DidReceiveMessageStop, string? StopReason);

/// <summary>
/// Thrown for a non-2xx status from the worker's /chat route, or for an error event
/// inside the stream. <see cref="HttpStatusCode"/> is null for in-stream errors.
/// </summary>
public sealed class ClaudeApiException : Exception
{
    public ClaudeApiException(string message, int? httpStatusCode, string? errorType)
        : base(message)
    {
        HttpStatusCode = httpStatusCode;
        ErrorType = errorType;
    }

    public int? HttpStatusCode { get; }
    public string? ErrorType { get; }
}

/// <summary>
/// Claude vision client with streaming, ported from ClaudeAPI.swift. POSTs an Anthropic
/// Messages request to the worker's /chat route, which forwards it unchanged and pipes
/// the SSE stream back. The model is chosen here, on the client.
/// </summary>
public sealed class ClaudeStreamingChatClient
{
    /// <summary>
    /// The Mac sends 1024, sized for Claude Sonnet 4.6, which does not think unless asked.
    /// Claude Sonnet 5.5 and Claude Opus 5.5 always run adaptive thinking (sending
    /// thinking disabled returns HTTP 400 on both), and thinking tokens count toward
    /// max_tokens, so 1024 could cut a reply off mid-sentence. 16000 leaves room for the
    /// thinking plus a long "explain more" answer while still bounding a runaway reply that
    /// would otherwise be read aloud for minutes.
    /// </summary>
    public const int MaximumResponseTokens = 16000;

    /// <summary>
    /// Sent as output_config.effort. Voice turns are latency-sensitive chat, and "low" keeps
    /// thinking short on both picker models (the closest match to the Mac's no-thinking
    /// Sonnet 4.6 behavior). Supported on both models without a beta header.
    /// </summary>
    public const string ResponseEffortLevel = "low";

    /// <summary>Upper bound for one whole streamed reply, matching the Mac session's 300 second resource timeout.</summary>
    public static readonly TimeSpan MaximumStreamDuration = TimeSpan.FromSeconds(300);

    private const int NetworkReadBufferSizeInBytes = 8192;

    private readonly HttpClient sharedHttpClient;
    private readonly Uri chatEndpointUrl;
    private volatile string currentModelIdentifier;

    public ClaudeStreamingChatClient(HttpClient sharedHttpClient, Uri chatEndpointUrl, string initialModelIdentifier)
    {
        this.sharedHttpClient = sharedHttpClient;
        this.chatEndpointUrl = chatEndpointUrl;
        currentModelIdentifier = initialModelIdentifier;
    }

    /// <summary>The model identifier sent with every request. Changed by the panel's model picker.</summary>
    public string ModelIdentifier
    {
        get => currentModelIdentifier;
        set => currentModelIdentifier = value;
    }

    /// <summary>
    /// Opens a connection to the worker host ahead of the first real request so the large
    /// screenshot payload does not also pay for connection and TLS setup. The worker answers
    /// a HEAD with 405, which is fine; only the connection matters. Failures are ignored.
    /// </summary>
    public async Task WarmUpConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            Uri workerRootUrl = new(chatEndpointUrl, "/");
            using HttpRequestMessage warmupRequest = new(HttpMethod.Head, workerRootUrl);
            using CancellationTokenSource warmupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            warmupTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            using HttpResponseMessage warmupResponse = await sharedHttpClient.SendAsync(warmupRequest, warmupTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception warmupException) when (warmupException is HttpRequestException or OperationCanceledException)
        {
            // Purely an optimization.
        }
    }

    /// <summary>
    /// Streams one reply. <paramref name="onAccumulatedTextUpdated"/> receives the full text
    /// so far after every text delta (called on a thread-pool thread).
    /// </summary>
    public async Task<ClaudeStreamingResponse> StreamResponseAsync(
        ClaudeChatRequest chatRequest,
        Action<string>? onAccumulatedTextUpdated,
        CancellationToken cancellationToken)
    {
        Stopwatch responseStopwatch = Stopwatch.StartNew();
        string requestBodyJson = BuildRequestBodyJson(ModelIdentifier, chatRequest);

        using HttpRequestMessage chatHttpRequest = new(HttpMethod.Post, chatEndpointUrl)
        {
            Content = new StringContent(requestBodyJson, Encoding.UTF8, "application/json"),
        };
        chatHttpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using CancellationTokenSource streamTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        streamTimeout.CancelAfter(MaximumStreamDuration);

        using HttpResponseMessage chatHttpResponse = await sharedHttpClient
            .SendAsync(chatHttpRequest, HttpCompletionOption.ResponseHeadersRead, streamTimeout.Token)
            .ConfigureAwait(false);

        if (!chatHttpResponse.IsSuccessStatusCode)
        {
            string errorResponseBody = await chatHttpResponse.Content.ReadAsStringAsync(streamTimeout.Token).ConfigureAwait(false);
            int httpStatusCode = (int)chatHttpResponse.StatusCode;
            throw new ClaudeApiException(
                $"Claude request failed (HTTP {httpStatusCode}): {errorResponseBody}",
                httpStatusCode,
                ExtractErrorTypeFromErrorBody(errorResponseBody));
        }

        await using Stream responseBodyStream = await chatHttpResponse.Content.ReadAsStreamAsync(streamTimeout.Token).ConfigureAwait(false);

        ClaudeServerSentEventParser serverSentEventParser = new();
        StreamAccumulationState streamAccumulationState = new();
        byte[] networkReadBuffer = new byte[NetworkReadBufferSizeInBytes];

        while (!streamAccumulationState.DidReceiveMessageStop)
        {
            int bytesRead = await responseBodyStream.ReadAsync(networkReadBuffer, streamTimeout.Token).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                HandleStreamEvents(serverSentEventParser.CompleteStream(), streamAccumulationState, onAccumulatedTextUpdated);
                break;
            }

            HandleStreamEvents(
                serverSentEventParser.AppendBytes(networkReadBuffer.AsSpan(0, bytesRead)),
                streamAccumulationState,
                onAccumulatedTextUpdated);
        }

        return new ClaudeStreamingResponse(
            streamAccumulationState.AccumulatedResponseText.ToString(),
            responseStopwatch.Elapsed,
            streamAccumulationState.DidReceiveMessageStop,
            streamAccumulationState.StopReason);
    }

    private static void HandleStreamEvents(
        IReadOnlyList<ClaudeStreamEvent> streamEvents,
        StreamAccumulationState streamAccumulationState,
        Action<string>? onAccumulatedTextUpdated)
    {
        foreach (ClaudeStreamEvent streamEvent in streamEvents)
        {
            switch (streamEvent)
            {
                case ClaudeTextDeltaEvent textDeltaEvent:
                    streamAccumulationState.AccumulatedResponseText.Append(textDeltaEvent.TextChunk);
                    onAccumulatedTextUpdated?.Invoke(streamAccumulationState.AccumulatedResponseText.ToString());
                    break;
                case ClaudeMessageDeltaEvent messageDeltaEvent:
                    streamAccumulationState.StopReason = messageDeltaEvent.StopReason ?? streamAccumulationState.StopReason;
                    break;
                case ClaudeStreamErrorEvent streamErrorEvent:
                    throw new ClaudeApiException(
                        $"Claude stream error ({streamErrorEvent.ErrorType}): {streamErrorEvent.ErrorMessage}",
                        null,
                        streamErrorEvent.ErrorType);
                case ClaudeMessageStopEvent:
                    streamAccumulationState.DidReceiveMessageStop = true;
                    break;
            }
        }
    }

    private sealed class StreamAccumulationState
    {
        public StringBuilder AccumulatedResponseText { get; } = new();
        public bool DidReceiveMessageStop { get; set; }
        public string? StopReason { get; set; }
    }

    /// <summary>
    /// Builds the Anthropic Messages body with the Mac app's layout: prior exchanges as plain
    /// text user/assistant turns, then one user turn with every image followed by its label,
    /// then the user's prompt. Two differences for the 5.5 models: output_config.effort is set
    /// (see <see cref="ResponseEffortLevel"/>), and exchanges with an empty side are skipped.
    /// </summary>
    public static string BuildRequestBodyJson(string modelIdentifier, ClaudeChatRequest chatRequest)
    {
        using MemoryStream requestBodyStream = new();
        using (Utf8JsonWriter requestBodyWriter = new(requestBodyStream))
        {
            requestBodyWriter.WriteStartObject();
            requestBodyWriter.WriteString("model", modelIdentifier);
            requestBodyWriter.WriteNumber("max_tokens", MaximumResponseTokens);
            requestBodyWriter.WriteBoolean("stream", true);
            requestBodyWriter.WriteString("system", chatRequest.SystemPrompt);

            requestBodyWriter.WriteStartObject("output_config");
            requestBodyWriter.WriteString("effort", ResponseEffortLevel);
            requestBodyWriter.WriteEndObject();

            requestBodyWriter.WriteStartArray("messages");

            foreach (ConversationExchange previousExchange in chatRequest.ConversationHistory)
            {
                // The Messages API rejects empty message content, so an exchange with an empty
                // side (for example a reply that was only a POINT tag) would fail every later turn.
                bool isExchangeSendable = previousExchange.UserTranscript.Trim().Length > 0
                    && previousExchange.AssistantResponse.Trim().Length > 0;
                if (!isExchangeSendable)
                {
                    continue;
                }

                WriteTextMessage(requestBodyWriter, "user", previousExchange.UserTranscript);
                WriteTextMessage(requestBodyWriter, "assistant", previousExchange.AssistantResponse);
            }

            requestBodyWriter.WriteStartObject();
            requestBodyWriter.WriteString("role", "user");
            requestBodyWriter.WriteStartArray("content");

            foreach (LabeledImage labeledImage in chatRequest.LabeledImages)
            {
                requestBodyWriter.WriteStartObject();
                requestBodyWriter.WriteString("type", "image");
                requestBodyWriter.WriteStartObject("source");
                requestBodyWriter.WriteString("type", "base64");
                requestBodyWriter.WriteString("media_type", DetectImageMediaType(labeledImage.ImageBytes));
                requestBodyWriter.WriteBase64String("data", labeledImage.ImageBytes);
                requestBodyWriter.WriteEndObject();
                requestBodyWriter.WriteEndObject();

                WriteTextContentBlock(requestBodyWriter, labeledImage.Label);
            }

            WriteTextContentBlock(requestBodyWriter, chatRequest.UserPrompt);

            requestBodyWriter.WriteEndArray();
            requestBodyWriter.WriteEndObject();

            requestBodyWriter.WriteEndArray();
            requestBodyWriter.WriteEndObject();
        }

        return Encoding.UTF8.GetString(requestBodyStream.ToArray());
    }

    /// <summary>
    /// PNG starts with 89 50 4E 47; everything else is treated as JPEG, which is what
    /// screen capture produces. The API rejects a declared type that does not match.
    /// </summary>
    public static string DetectImageMediaType(byte[] imageBytes)
    {
        bool hasPngSignature = imageBytes.Length >= 4
            && imageBytes[0] == 0x89
            && imageBytes[1] == 0x50
            && imageBytes[2] == 0x4E
            && imageBytes[3] == 0x47;

        return hasPngSignature ? "image/png" : "image/jpeg";
    }

    private static void WriteTextMessage(Utf8JsonWriter requestBodyWriter, string role, string messageText)
    {
        requestBodyWriter.WriteStartObject();
        requestBodyWriter.WriteString("role", role);
        requestBodyWriter.WriteString("content", messageText);
        requestBodyWriter.WriteEndObject();
    }

    private static void WriteTextContentBlock(Utf8JsonWriter requestBodyWriter, string blockText)
    {
        requestBodyWriter.WriteStartObject();
        requestBodyWriter.WriteString("type", "text");
        requestBodyWriter.WriteString("text", blockText);
        requestBodyWriter.WriteEndObject();
    }

    /// <summary>Anthropic error bodies look like {"type":"error","error":{"type":"...","message":"..."}}.</summary>
    internal static string? ExtractErrorTypeFromErrorBody(string errorResponseBody)
    {
        try
        {
            using JsonDocument errorDocument = JsonDocument.Parse(errorResponseBody);
            if (errorDocument.RootElement.ValueKind == JsonValueKind.Object
                && errorDocument.RootElement.TryGetProperty("error", out JsonElement errorElement)
                && errorElement.ValueKind == JsonValueKind.Object
                && errorElement.TryGetProperty("type", out JsonElement errorTypeElement)
                && errorTypeElement.ValueKind == JsonValueKind.String)
            {
                return errorTypeElement.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON (for example a plain-text 404 from the worker).
        }

        return null;
    }
}
