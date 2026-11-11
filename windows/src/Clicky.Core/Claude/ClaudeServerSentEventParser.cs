using System.Text;
using System.Text.Json;

namespace Clicky.Core.Claude;

/// <summary>Base type for the parts of Anthropic's streaming response the app reacts to.</summary>
public abstract record ClaudeStreamEvent;

/// <summary>A chunk of reply text from a content_block_delta / text_delta event.</summary>
public sealed record ClaudeTextDeltaEvent(string TextChunk) : ClaudeStreamEvent;

/// <summary>The message_stop event: Claude finished the reply normally.</summary>
public sealed record ClaudeMessageStopEvent : ClaudeStreamEvent;

/// <summary>The message_delta event, which carries why the reply ended (end_turn, max_tokens, refusal, ...).</summary>
public sealed record ClaudeMessageDeltaEvent(string? StopReason) : ClaudeStreamEvent;

/// <summary>An in-stream error event, for example overloaded_error.</summary>
public sealed record ClaudeStreamErrorEvent(string ErrorType, string ErrorMessage) : ClaudeStreamEvent;

/// <summary>
/// Incremental parser for Anthropic's Server-Sent Events stream.
///
/// Network reads can split anywhere, including inside a line or inside a multi-byte
/// UTF-8 character, so bytes are buffered until a full line ("\n" or "\r\n") arrives
/// and only then decoded. Events are dispatched on the blank line that ends them, per
/// the SSE format. Unknown event types (ping, message_start, content_block_start, ...)
/// and malformed JSON are ignored, the same as the Mac app.
/// </summary>
public sealed class ClaudeServerSentEventParser
{
    private const byte LineFeedByte = (byte)'\n';
    private const byte CarriageReturnByte = (byte)'\r';
    private const string EndOfStreamMarker = "[DONE]";

    private readonly List<byte> pendingLineBytes = [];
    private readonly StringBuilder currentEventDataText = new();
    private bool currentEventHasDataField;
    private string? currentEventName;

    /// <summary>Feeds the next network chunk and returns any events it completed.</summary>
    public IReadOnlyList<ClaudeStreamEvent> AppendBytes(ReadOnlySpan<byte> networkChunk)
    {
        List<ClaudeStreamEvent> completedEvents = [];

        foreach (byte chunkByte in networkChunk)
        {
            if (chunkByte == LineFeedByte)
            {
                ProcessCompletedLine(completedEvents);
            }
            else
            {
                pendingLineBytes.Add(chunkByte);
            }
        }

        return completedEvents;
    }

    /// <summary>
    /// Call once the response body ends. Flushes a final line or event that was not
    /// followed by the usual trailing blank line.
    /// </summary>
    public IReadOnlyList<ClaudeStreamEvent> CompleteStream()
    {
        List<ClaudeStreamEvent> completedEvents = [];

        if (pendingLineBytes.Count > 0)
        {
            ProcessCompletedLine(completedEvents);
        }

        DispatchCurrentEvent(completedEvents);
        return completedEvents;
    }

    private void ProcessCompletedLine(List<ClaudeStreamEvent> completedEvents)
    {
        int lineByteCount = pendingLineBytes.Count;
        if (lineByteCount > 0 && pendingLineBytes[lineByteCount - 1] == CarriageReturnByte)
        {
            lineByteCount--;
        }

        string lineText = Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pendingLineBytes)[..lineByteCount]);
        pendingLineBytes.Clear();

        if (lineText.Length == 0)
        {
            DispatchCurrentEvent(completedEvents);
            return;
        }

        // Lines starting with ':' are SSE comments (keep-alives).
        if (lineText[0] == ':')
        {
            return;
        }

        int fieldSeparatorIndex = lineText.IndexOf(':');
        string fieldName = fieldSeparatorIndex >= 0 ? lineText[..fieldSeparatorIndex] : lineText;
        string fieldValue = fieldSeparatorIndex >= 0 ? lineText[(fieldSeparatorIndex + 1)..] : string.Empty;
        if (fieldValue.StartsWith(' '))
        {
            fieldValue = fieldValue[1..];
        }

        switch (fieldName)
        {
            case "event":
                currentEventName = fieldValue;
                break;
            case "data":
                if (currentEventHasDataField)
                {
                    currentEventDataText.Append('\n');
                }

                currentEventDataText.Append(fieldValue);
                currentEventHasDataField = true;
                break;
        }
    }

    private void DispatchCurrentEvent(List<ClaudeStreamEvent> completedEvents)
    {
        if (!currentEventHasDataField)
        {
            currentEventName = null;
            return;
        }

        string eventDataText = currentEventDataText.ToString();
        string? eventNameFromEventField = currentEventName;

        currentEventDataText.Clear();
        currentEventHasDataField = false;
        currentEventName = null;

        if (eventDataText == EndOfStreamMarker)
        {
            completedEvents.Add(new ClaudeMessageStopEvent());
            return;
        }

        ClaudeStreamEvent? parsedEvent = ParseEventData(eventDataText, eventNameFromEventField);
        if (parsedEvent is not null)
        {
            completedEvents.Add(parsedEvent);
        }
    }

    private static ClaudeStreamEvent? ParseEventData(string eventDataText, string? eventNameFromEventField)
    {
        try
        {
            using JsonDocument eventDocument = JsonDocument.Parse(eventDataText);
            JsonElement eventRoot = eventDocument.RootElement;
            if (eventRoot.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? eventType = eventRoot.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : eventNameFromEventField;

            switch (eventType)
            {
                case "content_block_delta":
                    return ParseContentBlockDelta(eventRoot);
                case "message_delta":
                    return ParseMessageDelta(eventRoot);
                case "message_stop":
                    return new ClaudeMessageStopEvent();
                case "error":
                    return ParseErrorEvent(eventRoot);
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ClaudeStreamEvent? ParseContentBlockDelta(JsonElement eventRoot)
    {
        if (eventRoot.TryGetProperty("delta", out JsonElement deltaElement)
            && deltaElement.ValueKind == JsonValueKind.Object
            && deltaElement.TryGetProperty("type", out JsonElement deltaTypeElement)
            && deltaTypeElement.ValueKind == JsonValueKind.String
            && deltaTypeElement.GetString() == "text_delta"
            && deltaElement.TryGetProperty("text", out JsonElement deltaTextElement)
            && deltaTextElement.ValueKind == JsonValueKind.String)
        {
            return new ClaudeTextDeltaEvent(deltaTextElement.GetString() ?? string.Empty);
        }

        return null;
    }

    private static ClaudeStreamEvent ParseMessageDelta(JsonElement eventRoot)
    {
        string? stopReason = eventRoot.TryGetProperty("delta", out JsonElement deltaElement)
            && deltaElement.ValueKind == JsonValueKind.Object
            && deltaElement.TryGetProperty("stop_reason", out JsonElement stopReasonElement)
            && stopReasonElement.ValueKind == JsonValueKind.String
                ? stopReasonElement.GetString()
                : null;

        return new ClaudeMessageDeltaEvent(stopReason);
    }

    private static ClaudeStreamEvent ParseErrorEvent(JsonElement eventRoot)
    {
        string errorType = "unknown_error";
        string errorMessage = "Claude returned an error.";

        if (eventRoot.TryGetProperty("error", out JsonElement errorElement) && errorElement.ValueKind == JsonValueKind.Object)
        {
            if (errorElement.TryGetProperty("type", out JsonElement errorTypeElement) && errorTypeElement.ValueKind == JsonValueKind.String)
            {
                errorType = errorTypeElement.GetString() ?? errorType;
            }

            if (errorElement.TryGetProperty("message", out JsonElement errorMessageElement) && errorMessageElement.ValueKind == JsonValueKind.String)
            {
                errorMessage = errorMessageElement.GetString() ?? errorMessage;
            }
        }

        return new ClaudeStreamErrorEvent(errorType, errorMessage);
    }
}
