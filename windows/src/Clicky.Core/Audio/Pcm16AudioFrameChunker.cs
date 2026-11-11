namespace Clicky.Core.Audio;

/// <summary>
/// Collects PCM16 bytes from the microphone (which arrive in whatever size WASAPI
/// delivers) into fixed-size frames for the websocket. The last partial frame can be
/// flushed with silence padding so it still meets a minimum duration.
/// Not thread-safe; the caller serializes access.
/// </summary>
public sealed class Pcm16AudioFrameChunker
{
    private readonly int frameSizeInBytes;
    private readonly byte[] pendingFrameBuffer;
    private int pendingFrameByteCount;

    public Pcm16AudioFrameChunker(int frameSizeInBytes)
    {
        if (frameSizeInBytes <= 0 || frameSizeInBytes % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameSizeInBytes), "Frame size must be a positive, even number of bytes.");
        }

        this.frameSizeInBytes = frameSizeInBytes;
        pendingFrameBuffer = new byte[frameSizeInBytes];
    }

    public int PendingByteCount => pendingFrameByteCount;

    /// <summary>Adds audio and returns every frame it completed, in order.</summary>
    public IReadOnlyList<byte[]> AppendAudioBytes(ReadOnlySpan<byte> pcm16AudioBytes)
    {
        List<byte[]> completedFrames = [];
        ReadOnlySpan<byte> remainingAudioBytes = pcm16AudioBytes;

        while (remainingAudioBytes.Length > 0)
        {
            int bytesUntilFrameIsFull = frameSizeInBytes - pendingFrameByteCount;
            int bytesToCopy = Math.Min(bytesUntilFrameIsFull, remainingAudioBytes.Length);

            remainingAudioBytes[..bytesToCopy].CopyTo(pendingFrameBuffer.AsSpan(pendingFrameByteCount));
            pendingFrameByteCount += bytesToCopy;
            remainingAudioBytes = remainingAudioBytes[bytesToCopy..];

            if (pendingFrameByteCount == frameSizeInBytes)
            {
                completedFrames.Add(pendingFrameBuffer.ToArray());
                pendingFrameByteCount = 0;
            }
        }

        return completedFrames;
    }

    /// <summary>
    /// Returns the partial frame, padded with silence to at least <paramref name="minimumFrameSizeInBytes"/>,
    /// or null when nothing is pending.
    /// </summary>
    public byte[]? FlushPendingAudioWithSilencePadding(int minimumFrameSizeInBytes)
    {
        if (pendingFrameByteCount == 0)
        {
            return null;
        }

        int flushedFrameSizeInBytes = Math.Max(pendingFrameByteCount, minimumFrameSizeInBytes);
        byte[] flushedFrame = new byte[flushedFrameSizeInBytes];
        pendingFrameBuffer.AsSpan(0, pendingFrameByteCount).CopyTo(flushedFrame);
        pendingFrameByteCount = 0;
        return flushedFrame;
    }

    public void Reset()
    {
        pendingFrameByteCount = 0;
    }
}
