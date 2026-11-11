namespace Clicky.Core.Transcription;

/// <summary>
/// Pure transcript bookkeeping for AssemblyAI v3 "Turn" messages, ported from the
/// Swift streaming session. AssemblyAI sends a growing partial transcript for the
/// active turn, then a final (and optionally formatted) transcript when the turn ends.
/// Finished turns are kept in turn order; the full transcript is all finished turns
/// followed by the active partial, joined by spaces.
/// Not thread-safe; the owning session serializes access.
/// </summary>
public sealed class AssemblyAITurnTranscriptAccumulator
{
    private readonly SortedDictionary<int, StoredTurnTranscript> storedTurnTranscriptsByTurnOrder = [];
    private int? activeTurnOrder;
    private string activeTurnTranscriptText = string.Empty;

    /// <summary>The last full transcript computed, used as a fallback when composing yields nothing.</summary>
    public string LatestFullTranscriptText { get; private set; } = string.Empty;

    /// <summary>
    /// Applies one Turn message and returns true when it ended a turn
    /// (end_of_turn or turn_is_formatted), which is the signal the final transcript can be delivered.
    /// </summary>
    public bool ApplyTurnMessage(string? transcriptText, int? turnOrder, bool? isEndOfTurn, bool? isTurnFormatted)
    {
        string trimmedTranscriptText = transcriptText?.Trim() ?? string.Empty;
        bool didTurnEnd = isEndOfTurn == true || isTurnFormatted == true;

        int resolvedTurnOrder = turnOrder
            ?? activeTurnOrder
            ?? (storedTurnTranscriptsByTurnOrder.Count == 0 ? 0 : storedTurnTranscriptsByTurnOrder.Keys.Max() + 1);

        if (didTurnEnd)
        {
            activeTurnOrder = null;
            activeTurnTranscriptText = string.Empty;
            StoreTurnTranscript(trimmedTranscriptText, resolvedTurnOrder, isTurnFormatted == true);
        }
        else
        {
            activeTurnOrder = resolvedTurnOrder;
            activeTurnTranscriptText = trimmedTranscriptText;
        }

        LatestFullTranscriptText = ComposeFullTranscript();
        return didTurnEnd;
    }

    public string ComposeFullTranscript()
    {
        List<string> transcriptSegments = storedTurnTranscriptsByTurnOrder.Values
            .Select(storedTurnTranscript => storedTurnTranscript.TranscriptText)
            .Where(transcriptSegment => transcriptSegment.Length > 0)
            .ToList();

        string trimmedActiveTurnTranscriptText = activeTurnTranscriptText.Trim();
        if (trimmedActiveTurnTranscriptText.Length > 0)
        {
            transcriptSegments.Add(trimmedActiveTurnTranscriptText);
        }

        return string.Join(' ', transcriptSegments);
    }

    /// <summary>Composed transcript when there is one, otherwise the last one seen.</summary>
    public string GetBestAvailableTranscriptText()
    {
        string composedTranscriptText = ComposeFullTranscript().Trim();
        return composedTranscriptText.Length > 0 ? composedTranscriptText : LatestFullTranscriptText.Trim();
    }

    private void StoreTurnTranscript(string transcriptText, int turnOrder, bool isFormatted)
    {
        if (transcriptText.Length == 0)
        {
            return;
        }

        // A formatted transcript is the better version of a turn; never overwrite it
        // with the unformatted one if AssemblyAI delivers them out of order.
        if (storedTurnTranscriptsByTurnOrder.TryGetValue(turnOrder, out StoredTurnTranscript? existingTurnTranscript)
            && existingTurnTranscript.IsFormatted
            && !isFormatted)
        {
            return;
        }

        storedTurnTranscriptsByTurnOrder[turnOrder] = new StoredTurnTranscript(transcriptText, isFormatted);
    }

    private sealed record StoredTurnTranscript(string TranscriptText, bool IsFormatted);
}
