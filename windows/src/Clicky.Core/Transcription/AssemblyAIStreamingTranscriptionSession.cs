using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Clicky.Core.Audio;
using Clicky.Core.Diagnostics;

namespace Clicky.Core.Transcription;

/// <summary>Raised for websocket and protocol failures during a streaming session.</summary>
public sealed class StreamingTranscriptionException : Exception
{
    public StreamingTranscriptionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// One push-to-talk transcription session over AssemblyAI's v3 websocket.
/// Port of AssemblyAIStreamingTranscriptionSession in the Swift app:
/// <list type="bullet">
/// <item>Open waits for the "Begin" message.</item>
/// <item>Microphone audio is regrouped into 100 ms PCM16 frames and sent as binary messages.</item>
/// <item>On key-up, <see cref="RequestFinalTranscript"/> sends ForceEndpoint and waits up to 1.4 s for the
/// turn to end, then delivers the best transcript it has.</item>
/// <item>If the socket fails while a final transcript is awaited, the best partial is delivered instead of an error.</item>
/// </list>
/// Callbacks run on background threads; the consumer is responsible for marshaling to the UI thread.
/// </summary>
public sealed class AssemblyAIStreamingTranscriptionSession : IAsyncDisposable
{
    private static readonly TimeSpan SessionReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(3);
    private const string LogCategory = "AssemblyAI";

    private readonly Uri websocketUriContainingToken;
    private readonly HttpMessageInvoker sharedWebSocketMessageInvoker;
    private readonly Action<string> onTranscriptUpdate;
    private readonly Action<string> onFinalTranscriptReady;
    private readonly Action<Exception> onError;

    private readonly ClientWebSocket assemblyAIWebSocket = new();
    private readonly object sessionStateLock = new();
    private readonly AssemblyAITurnTranscriptAccumulator transcriptAccumulator = new();
    private readonly Pcm16AudioFrameChunker audioFrameChunker = new(AssemblyAIStreamingConfiguration.AudioFrameSizeInBytes);
    private readonly Channel<OutgoingWebSocketMessage> outgoingMessageChannel =
        Channel.CreateUnbounded<OutgoingWebSocketMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource sessionReadyCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource sessionLifetimeCancellation = new();

    private bool hasDeliveredFinalTranscript;
    private bool isAwaitingExplicitFinalTranscript;
    private bool isClosingIntentionally;
    private bool hasReportedFailure;
    private bool hasCompletedOutgoingChannel;
    private CancellationTokenSource? explicitFinalTranscriptDeadlineCancellation;

    internal AssemblyAIStreamingTranscriptionSession(
        Uri websocketUriContainingToken,
        HttpMessageInvoker sharedWebSocketMessageInvoker,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError)
    {
        this.websocketUriContainingToken = websocketUriContainingToken;
        this.sharedWebSocketMessageInvoker = sharedWebSocketMessageInvoker;
        this.onTranscriptUpdate = onTranscriptUpdate;
        this.onFinalTranscriptReady = onFinalTranscriptReady;
        this.onError = onError;

        // Once the session's lifetime ends (a few seconds after Cancel, or after a send failure),
        // abort and release the socket so a stuck server cannot keep it open.
        sessionLifetimeCancellation.Token.Register(() => assemblyAIWebSocket.Dispose());
    }

    /// <summary>How long the dictation manager should wait after key-up before finishing without a final transcript.</summary>
    public TimeSpan FinalTranscriptFallbackDelay => AssemblyAIStreamingConfiguration.FinalTranscriptFallbackDelay;

    /// <summary>Connects and waits for AssemblyAI's Begin message.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        await assemblyAIWebSocket
            .ConnectAsync(websocketUriContainingToken, sharedWebSocketMessageInvoker, cancellationToken)
            .ConfigureAwait(false);

        _ = Task.Run(RunReceiveLoopAsync);
        _ = Task.Run(RunSendLoopAsync);

        using CancellationTokenSource readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readyTimeout.CancelAfter(SessionReadyTimeout);
        using CancellationTokenRegistration readyTimeoutRegistration = readyTimeout.Token.Register(() =>
            sessionReadyCompletionSource.TrySetException(new StreamingTranscriptionException("AssemblyAI did not start the session in time.")));

        await sessionReadyCompletionSource.Task.ConfigureAwait(false);
    }

    /// <summary>Queues PCM16 mono 16 kHz audio. Safe to call from the audio capture thread.</summary>
    public void AppendPcm16Audio(ReadOnlySpan<byte> pcm16AudioBytes)
    {
        IReadOnlyList<byte[]> completedAudioFrames;
        lock (sessionStateLock)
        {
            if (isClosingIntentionally || hasCompletedOutgoingChannel)
            {
                return;
            }

            completedAudioFrames = audioFrameChunker.AppendAudioBytes(pcm16AudioBytes);
        }

        foreach (byte[] completedAudioFrame in completedAudioFrames)
        {
            EnqueueOutgoingMessage(OutgoingWebSocketMessage.ForBinaryAudio(completedAudioFrame));
        }
    }

    /// <summary>
    /// Called on key-up. Flushes buffered audio, asks AssemblyAI to end the turn now, and
    /// arms a 1.4 s deadline after which the best available transcript is delivered.
    /// </summary>
    public void RequestFinalTranscript()
    {
        byte[]? flushedAudioFrame;
        CancellationToken deadlineCancellationToken;

        lock (sessionStateLock)
        {
            if (hasDeliveredFinalTranscript)
            {
                return;
            }

            isAwaitingExplicitFinalTranscript = true;
            flushedAudioFrame = audioFrameChunker.FlushPendingAudioWithSilencePadding(AssemblyAIStreamingConfiguration.MinimumAudioFrameSizeInBytes);

            explicitFinalTranscriptDeadlineCancellation?.Cancel();
            explicitFinalTranscriptDeadlineCancellation = new CancellationTokenSource();
            deadlineCancellationToken = explicitFinalTranscriptDeadlineCancellation.Token;
        }

        if (flushedAudioFrame is not null)
        {
            EnqueueOutgoingMessage(OutgoingWebSocketMessage.ForBinaryAudio(flushedAudioFrame));
        }

        EnqueueOutgoingMessage(OutgoingWebSocketMessage.ForJsonControlMessage("ForceEndpoint"));

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AssemblyAIStreamingConfiguration.ExplicitFinalTranscriptGracePeriod, deadlineCancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            DeliverFinalTranscriptIfNeeded(GetBestAvailableTranscriptText());
        });
    }

    /// <summary>Ends the session: sends Terminate, then closes the socket. No error callbacks fire afterwards.</summary>
    public void Cancel()
    {
        lock (sessionStateLock)
        {
            explicitFinalTranscriptDeadlineCancellation?.Cancel();
            explicitFinalTranscriptDeadlineCancellation = null;
            isClosingIntentionally = true;
        }

        EnqueueOutgoingMessage(OutgoingWebSocketMessage.ForJsonControlMessage("Terminate"));
        CompleteOutgoingChannel();
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        }
        finally
        {
            sessionLifetimeCancellation.CancelAfter(GracefulCloseTimeout);
        }
    }

    private string GetBestAvailableTranscriptText()
    {
        lock (sessionStateLock)
        {
            return transcriptAccumulator.GetBestAvailableTranscriptText();
        }
    }

    private async Task RunReceiveLoopAsync()
    {
        byte[] receiveBuffer = new byte[16 * 1024];
        using MemoryStream currentMessageBytes = new();

        try
        {
            while (!sessionLifetimeCancellation.IsCancellationRequested)
            {
                WebSocketReceiveResult receiveResult = await assemblyAIWebSocket
                    .ReceiveAsync(receiveBuffer, sessionLifetimeCancellation.Token)
                    .ConfigureAwait(false);

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    string closeDescription = assemblyAIWebSocket.CloseStatusDescription ?? string.Empty;
                    int closeStatusCode = (int)(assemblyAIWebSocket.CloseStatus ?? WebSocketCloseStatus.Empty);
                    FailSession(new StreamingTranscriptionException($"AssemblyAI closed the connection ({closeStatusCode}): {closeDescription}"));
                    return;
                }

                currentMessageBytes.Write(receiveBuffer, 0, receiveResult.Count);
                if (!receiveResult.EndOfMessage)
                {
                    continue;
                }

                string incomingMessageText = Encoding.UTF8.GetString(currentMessageBytes.GetBuffer(), 0, (int)currentMessageBytes.Length);
                currentMessageBytes.SetLength(0);
                HandleIncomingTextMessage(incomingMessageText);
            }
        }
        catch (Exception receiveException)
        {
            FailSession(new StreamingTranscriptionException("AssemblyAI connection failed.", receiveException));
        }
    }

    private async Task RunSendLoopAsync()
    {
        try
        {
            await foreach (OutgoingWebSocketMessage outgoingMessage in outgoingMessageChannel.Reader.ReadAllAsync(sessionLifetimeCancellation.Token).ConfigureAwait(false))
            {
                if (assemblyAIWebSocket.State != WebSocketState.Open)
                {
                    continue;
                }

                await assemblyAIWebSocket
                    .SendAsync(outgoingMessage.Payload, outgoingMessage.MessageType, endOfMessage: true, sessionLifetimeCancellation.Token)
                    .ConfigureAwait(false);
            }

            await CloseSocketGracefullyAsync().ConfigureAwait(false);
        }
        catch (Exception sendException)
        {
            FailSession(new StreamingTranscriptionException("Sending audio to AssemblyAI failed.", sendException));
        }
        finally
        {
            sessionLifetimeCancellation.CancelAfter(GracefulCloseTimeout);
        }
    }

    private async Task CloseSocketGracefullyAsync()
    {
        if (assemblyAIWebSocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using CancellationTokenSource closeTimeout = new(GracefulCloseTimeout);
        try
        {
            await assemblyAIWebSocket
                .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, closeTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception closeException) when (closeException is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The server may already have closed after Terminate.
        }
    }

    private void HandleIncomingTextMessage(string incomingMessageText)
    {
        try
        {
            using JsonDocument messageDocument = JsonDocument.Parse(incomingMessageText);
            JsonElement messageRoot = messageDocument.RootElement;
            string messageType = messageRoot.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? (typeElement.GetString() ?? string.Empty).ToLowerInvariant()
                : string.Empty;

            switch (messageType)
            {
                case "begin":
                    sessionReadyCompletionSource.TrySetResult();
                    break;
                case "turn":
                    HandleTurnMessage(messageRoot);
                    break;
                case "termination":
                    sessionReadyCompletionSource.TrySetResult();
                    bool shouldDeliverOnTermination;
                    lock (sessionStateLock)
                    {
                        shouldDeliverOnTermination = isAwaitingExplicitFinalTranscript && !hasDeliveredFinalTranscript;
                    }

                    if (shouldDeliverOnTermination)
                    {
                        DeliverFinalTranscriptIfNeeded(GetBestAvailableTranscriptText());
                    }

                    break;
                case "error":
                    string errorMessageText = ReadOptionalString(messageRoot, "error")
                        ?? ReadOptionalString(messageRoot, "message")
                        ?? "AssemblyAI returned an error.";
                    FailSession(new StreamingTranscriptionException(errorMessageText));
                    break;
            }
        }
        catch (JsonException jsonException)
        {
            FailSession(new StreamingTranscriptionException("AssemblyAI sent a message that could not be read.", jsonException));
        }
    }

    private void HandleTurnMessage(JsonElement turnMessageRoot)
    {
        string? turnTranscriptText = ReadOptionalString(turnMessageRoot, "transcript");
        int? turnOrder = turnMessageRoot.TryGetProperty("turn_order", out JsonElement turnOrderElement) && turnOrderElement.ValueKind == JsonValueKind.Number
            ? turnOrderElement.GetInt32()
            : null;
        bool? isEndOfTurn = ReadOptionalBoolean(turnMessageRoot, "end_of_turn");
        bool? isTurnFormatted = ReadOptionalBoolean(turnMessageRoot, "turn_is_formatted");

        bool didTurnEnd;
        bool shouldDeliverFinalTranscript;
        string fullTranscriptText;
        string bestAvailableTranscriptText;

        lock (sessionStateLock)
        {
            didTurnEnd = transcriptAccumulator.ApplyTurnMessage(turnTranscriptText, turnOrder, isEndOfTurn, isTurnFormatted);
            fullTranscriptText = transcriptAccumulator.LatestFullTranscriptText;
            bestAvailableTranscriptText = transcriptAccumulator.GetBestAvailableTranscriptText();
            shouldDeliverFinalTranscript = isAwaitingExplicitFinalTranscript && didTurnEnd;

            if (shouldDeliverFinalTranscript)
            {
                explicitFinalTranscriptDeadlineCancellation?.Cancel();
                explicitFinalTranscriptDeadlineCancellation = null;
            }
        }

        if (fullTranscriptText.Length > 0)
        {
            onTranscriptUpdate(fullTranscriptText);
        }

        if (shouldDeliverFinalTranscript)
        {
            DeliverFinalTranscriptIfNeeded(bestAvailableTranscriptText);
        }
    }

    private void DeliverFinalTranscriptIfNeeded(string finalTranscriptText)
    {
        lock (sessionStateLock)
        {
            if (hasDeliveredFinalTranscript)
            {
                return;
            }

            hasDeliveredFinalTranscript = true;
            explicitFinalTranscriptDeadlineCancellation?.Cancel();
            explicitFinalTranscriptDeadlineCancellation = null;
        }

        onFinalTranscriptReady(finalTranscriptText);
        EnqueueOutgoingMessage(OutgoingWebSocketMessage.ForJsonControlMessage("Terminate"));
    }

    private void FailSession(Exception sessionException)
    {
        sessionReadyCompletionSource.TrySetException(sessionException);

        string bestAvailableTranscriptText;
        bool shouldDeliverPartialTranscriptInstead;

        lock (sessionStateLock)
        {
            // After an intentional close, a delivered final transcript, or an earlier failure,
            // socket errors are expected noise and must not reach the dictation manager.
            if (isClosingIntentionally || hasDeliveredFinalTranscript || hasReportedFailure)
            {
                return;
            }

            bestAvailableTranscriptText = transcriptAccumulator.GetBestAvailableTranscriptText();
            shouldDeliverPartialTranscriptInstead = isAwaitingExplicitFinalTranscript && bestAvailableTranscriptText.Length > 0;

            if (!shouldDeliverPartialTranscriptInstead)
            {
                hasReportedFailure = true;
            }
        }

        if (shouldDeliverPartialTranscriptInstead)
        {
            ClickyDiagnosticLog.Write(LogCategory, $"Websocket error during active session, delivering partial transcript as fallback: {sessionException.Message}");
            DeliverFinalTranscriptIfNeeded(bestAvailableTranscriptText);
            return;
        }

        ClickyDiagnosticLog.Write(LogCategory, $"Session failed: {DescribeExceptionChain(sessionException)}");
        onError(sessionException);
    }

    private void EnqueueOutgoingMessage(OutgoingWebSocketMessage outgoingMessage)
    {
        outgoingMessageChannel.Writer.TryWrite(outgoingMessage);
    }

    private void CompleteOutgoingChannel()
    {
        lock (sessionStateLock)
        {
            if (hasCompletedOutgoingChannel)
            {
                return;
            }

            hasCompletedOutgoingChannel = true;
        }

        outgoingMessageChannel.Writer.TryComplete();
    }

    private static string? ReadOptionalString(JsonElement messageRoot, string propertyName)
    {
        return messageRoot.TryGetProperty(propertyName, out JsonElement propertyElement) && propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString()
            : null;
    }

    private static bool? ReadOptionalBoolean(JsonElement messageRoot, string propertyName)
    {
        if (!messageRoot.TryGetProperty(propertyName, out JsonElement propertyElement))
        {
            return null;
        }

        return propertyElement.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>Exception messages only; never the websocket URL, which carries the token.</summary>
    private static string DescribeExceptionChain(Exception exceptionToDescribe)
    {
        List<string> exceptionMessages = [];
        for (Exception? currentException = exceptionToDescribe; currentException is not null; currentException = currentException.InnerException)
        {
            exceptionMessages.Add($"{currentException.GetType().Name}: {currentException.Message}");
        }

        return string.Join(" -> ", exceptionMessages);
    }

    private sealed record OutgoingWebSocketMessage(ReadOnlyMemory<byte> Payload, WebSocketMessageType MessageType)
    {
        public static OutgoingWebSocketMessage ForBinaryAudio(byte[] pcm16AudioFrame)
        {
            return new OutgoingWebSocketMessage(pcm16AudioFrame, WebSocketMessageType.Binary);
        }

        public static OutgoingWebSocketMessage ForJsonControlMessage(string controlMessageType)
        {
            byte[] controlMessageJsonBytes = JsonSerializer.SerializeToUtf8Bytes(new { type = controlMessageType });
            return new OutgoingWebSocketMessage(controlMessageJsonBytes, WebSocketMessageType.Text);
        }
    }
}
