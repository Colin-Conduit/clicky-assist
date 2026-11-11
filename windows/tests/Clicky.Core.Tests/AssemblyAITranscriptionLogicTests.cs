using System.Text.Json;
using Clicky.Core.Audio;
using Clicky.Core.Transcription;

namespace Clicky.Core.Tests;

public sealed class AssemblyAITranscriptionLogicTests
{
    [Fact]
    public void PartialTurnsUpdateAndFinishedTurnsAccumulateInOrder()
    {
        AssemblyAITurnTranscriptAccumulator transcriptAccumulator = new();

        Assert.False(transcriptAccumulator.ApplyTurnMessage("how do i", 0, false, false));
        Assert.Equal("how do i", transcriptAccumulator.LatestFullTranscriptText);

        Assert.True(transcriptAccumulator.ApplyTurnMessage("How do I commit?", 0, true, true));
        Assert.Equal("How do I commit?", transcriptAccumulator.LatestFullTranscriptText);

        Assert.False(transcriptAccumulator.ApplyTurnMessage("in git", 1, false, false));
        Assert.Equal("How do I commit? in git", transcriptAccumulator.GetBestAvailableTranscriptText());
    }

    [Fact]
    public void UnformattedEndOfTurnNeverOverwritesFormattedTurn()
    {
        AssemblyAITurnTranscriptAccumulator transcriptAccumulator = new();

        transcriptAccumulator.ApplyTurnMessage("Hello there.", 0, true, true);
        transcriptAccumulator.ApplyTurnMessage("hello there", 0, true, false);

        Assert.Equal("Hello there.", transcriptAccumulator.ComposeFullTranscript());
    }

    [Fact]
    public void MissingTurnOrderContinuesAfterTheHighestStoredTurn()
    {
        AssemblyAITurnTranscriptAccumulator transcriptAccumulator = new();

        transcriptAccumulator.ApplyTurnMessage("first", null, true, false);
        transcriptAccumulator.ApplyTurnMessage("second", null, true, false);

        Assert.Equal("first second", transcriptAccumulator.ComposeFullTranscript());
    }

    [Fact]
    public void EmptyFinalTurnClearsTheActivePartialAndStoresNothing()
    {
        AssemblyAITurnTranscriptAccumulator transcriptAccumulator = new();

        transcriptAccumulator.ApplyTurnMessage("partial words", 0, false, false);
        transcriptAccumulator.ApplyTurnMessage("   ", 0, true, false);

        Assert.Equal(string.Empty, transcriptAccumulator.ComposeFullTranscript());
        Assert.Equal(string.Empty, transcriptAccumulator.GetBestAvailableTranscriptText());
    }

    [Fact]
    public void ChunkerGroupsAudioIntoHundredMillisecondFrames()
    {
        Pcm16AudioFrameChunker audioFrameChunker = new(AssemblyAIStreamingConfiguration.AudioFrameSizeInBytes);

        IReadOnlyList<byte[]> framesFromFirstWrite = audioFrameChunker.AppendAudioBytes(new byte[1000]);
        IReadOnlyList<byte[]> framesFromSecondWrite = audioFrameChunker.AppendAudioBytes(new byte[6000]);

        Assert.Equal(3200, AssemblyAIStreamingConfiguration.AudioFrameSizeInBytes);
        Assert.Empty(framesFromFirstWrite);
        Assert.Equal(2, framesFromSecondWrite.Count);
        Assert.All(framesFromSecondWrite, completedFrame => Assert.Equal(3200, completedFrame.Length));
        Assert.Equal(600, audioFrameChunker.PendingByteCount);
    }

    [Fact]
    public void ChunkerPadsTheFinalFrameToTheFiftyMillisecondMinimum()
    {
        Pcm16AudioFrameChunker audioFrameChunker = new(AssemblyAIStreamingConfiguration.AudioFrameSizeInBytes);
        byte[] shortAudio = Enumerable.Repeat((byte)7, 400).ToArray();
        audioFrameChunker.AppendAudioBytes(shortAudio);

        byte[]? flushedFrame = audioFrameChunker.FlushPendingAudioWithSilencePadding(AssemblyAIStreamingConfiguration.MinimumAudioFrameSizeInBytes);

        Assert.NotNull(flushedFrame);
        Assert.Equal(1600, flushedFrame.Length);
        Assert.All(flushedFrame.Take(400), audioByte => Assert.Equal(7, audioByte));
        Assert.All(flushedFrame.Skip(400), audioByte => Assert.Equal(0, audioByte));
        Assert.Null(audioFrameChunker.FlushPendingAudioWithSilencePadding(1600));
    }

    [Fact]
    public void WebsocketUrlCarriesTheSameParametersAsTheMacApp()
    {
        Uri websocketUri = AssemblyAIStreamingConfiguration.BuildWebsocketUri("example-token", ["Claude", "Next.js"]);
        Dictionary<string, string> queryParameters = websocketUri.Query.TrimStart('?')
            .Split('&')
            .Select(queryPair => queryPair.Split('=', 2))
            .ToDictionary(queryPair => Uri.UnescapeDataString(queryPair[0]), queryPair => Uri.UnescapeDataString(queryPair[1]));

        Assert.Equal("wss", websocketUri.Scheme);
        Assert.Equal("streaming.assemblyai.com", websocketUri.Host);
        Assert.Equal("/v3/ws", websocketUri.AbsolutePath);
        Assert.Equal("16000", queryParameters["sample_rate"]);
        Assert.Equal("pcm_s16le", queryParameters["encoding"]);
        Assert.Equal("true", queryParameters["format_turns"]);
        Assert.Equal("u3-rt-pro", queryParameters["speech_model"]);
        Assert.Equal(["Claude", "Next.js"], JsonSerializer.Deserialize<string[]>(queryParameters["keyterms_prompt"])!);
        Assert.Equal("example-token", queryParameters["token"]);
    }

    [Fact]
    public void KeytermsAreDeduplicatedCaseInsensitivelyKeepingOrder()
    {
        IReadOnlyList<string> keyterms = TranscriptionKeyterms.BuildKeyterms(["claude", "  Windows  ", "", "windows"]);

        Assert.Equal(TranscriptionKeyterms.BaseKeyterms.Count + 1, keyterms.Count);
        Assert.Equal("Windows", keyterms[^1]);
        Assert.Single(keyterms, keyterm => keyterm.Equals("claude", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TokenResponseParsing()
    {
        Assert.Equal("abc", AssemblyAITemporaryTokenClient.ParseTokenFromResponseBody("{\"token\":\"abc\",\"expires_in_seconds\":480}"));
        Assert.Throws<TranscriptionTokenException>(() => AssemblyAITemporaryTokenClient.ParseTokenFromResponseBody("{\"error\":\"bad key\"}"));
        Assert.Throws<TranscriptionTokenException>(() => AssemblyAITemporaryTokenClient.ParseTokenFromResponseBody("not json"));
    }
}
