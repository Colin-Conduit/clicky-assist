namespace Clicky.Core.Conversation;

/// <summary>
/// One completed turn: what the user said and what Claude answered (with the
/// [POINT:...] tag already removed so it does not confuse later turns).
/// </summary>
public sealed record ConversationExchange(string UserTranscript, string AssistantResponse);

/// <summary>
/// Rolling in-memory history so Claude remembers earlier turns within one app session.
/// Mirrors CompanionManager.conversationHistory on the Mac: only the last ten exchanges
/// are kept so context does not grow without bound. Screenshots from earlier turns are
/// not stored; only the text is replayed.
/// </summary>
public sealed class ConversationHistory
{
    public const int DefaultMaximumExchangeCount = 10;

    private readonly List<ConversationExchange> exchangesOldestFirst = [];
    private readonly object exchangesLock = new();

    public ConversationHistory(int maximumExchangeCount = DefaultMaximumExchangeCount)
    {
        if (maximumExchangeCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumExchangeCount), "History must keep at least one exchange.");
        }

        MaximumExchangeCount = maximumExchangeCount;
    }

    public int MaximumExchangeCount { get; }

    public int ExchangeCount
    {
        get
        {
            lock (exchangesLock)
            {
                return exchangesOldestFirst.Count;
            }
        }
    }

    /// <summary>Snapshot of the stored exchanges, oldest first, safe to enumerate while new turns arrive.</summary>
    public IReadOnlyList<ConversationExchange> GetExchangesOldestFirst()
    {
        lock (exchangesLock)
        {
            return exchangesOldestFirst.ToArray();
        }
    }

    public void AppendExchange(string userTranscript, string assistantResponse)
    {
        lock (exchangesLock)
        {
            exchangesOldestFirst.Add(new ConversationExchange(userTranscript, assistantResponse));

            int exchangesOverLimit = exchangesOldestFirst.Count - MaximumExchangeCount;
            if (exchangesOverLimit > 0)
            {
                exchangesOldestFirst.RemoveRange(0, exchangesOverLimit);
            }
        }
    }

    public void Clear()
    {
        lock (exchangesLock)
        {
            exchangesOldestFirst.Clear();
        }
    }
}
