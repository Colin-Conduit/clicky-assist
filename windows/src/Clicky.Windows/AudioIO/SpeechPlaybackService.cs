using System.IO;
using Clicky.Windows.Diagnostics;
using NAudio.Wave;
using Windows.Media.SpeechSynthesis;

namespace Clicky.Windows.AudioIO;

/// <summary>
/// Plays Clicky's spoken replies, the Windows counterpart of the AVAudioPlayer in
/// ElevenLabsTTSClient plus the Mac's NSSpeechSynthesizer error fallback.
///
/// Lifetime rules that keep NAudio safe:
/// each playback is a session that owns one WaveOutEvent and one decoded reader. Stopping only
/// calls WaveOutEvent.Stop; both objects are disposed in that session's PlaybackStopped handler,
/// which NAudio raises as the last thing its playback thread does. Disposing earlier would free
/// the wave buffers while that thread can still be writing them to the device.
/// </summary>
public sealed class SpeechPlaybackService : IDisposable
{
    private const string LogCategory = "Audio";

    private readonly object playbackStateLock = new();

    /// <summary>Guarded by playbackStateLock. Non-null exactly while IsPlaying is true.</summary>
    private SpeechPlaybackSession? currentPlaybackSession;

    /// <summary>
    /// Guarded by playbackStateLock. Every start and every stop bumps this, so a start that is
    /// still decoding when a newer start or a stop arrives can tell it has been superseded and
    /// must not make sound.
    /// </summary>
    private long latestPlaybackRequestNumber;

    private bool isDisposed;

    /// <summary>Raised (on any thread) once per playback, when it ends naturally or is stopped.</summary>
    public event Action? PlaybackFinished;

    /// <summary>True from the moment playback starts until it ends naturally or is stopped.</summary>
    public bool IsPlaying
    {
        get
        {
            lock (playbackStateLock)
            {
                return currentPlaybackSession is not null;
            }
        }
    }

    /// <summary>
    /// Stops any current playback, decodes the MP3, starts playing and returns as soon as audio
    /// has started, like the Mac's speakText returning right after player.play().
    /// Throws on decode failure. Throws OperationCanceledException if the token is canceled, or if
    /// StopPlayback or a newer start arrives before this one begins playing.
    /// </summary>
    public async Task StartPlayingMp3Async(byte[] mp3AudioBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mp3AudioBytes);
        cancellationToken.ThrowIfCancellationRequested();

        long playbackRequestNumber = StopCurrentPlaybackAndBeginNewRequest();

        try
        {
            // Decoding and opening the wave device run on the thread pool: it keeps the UI thread
            // free, and NAudio's WaveOutEvent captures SynchronizationContext.Current when it is
            // constructed, so building it there (where there is none) makes PlaybackStopped fire
            // on the playback thread instead of being queued behind the WPF dispatcher.
            PreparedSpeechPlayback preparedSpeechPlayback = await Task.Run(
                () => PrepareSpeechPlayback(() => OpenMp3AudioReader(mp3AudioBytes)),
                cancellationToken).ConfigureAwait(false);

            StartPreparedPlayback(preparedSpeechPlayback, playbackRequestNumber, cancellationToken);
        }
        catch (Exception playbackStartException) when (playbackStartException is not OperationCanceledException)
        {
            ClickyApplicationLog.Write(LogCategory, $"Could not play speech audio: {playbackStartException.GetType().Name}: {playbackStartException.Message}");
            throw;
        }
    }

    /// <summary>
    /// Same as StartPlayingMp3Async, but speaks textToSpeak with the voice built into Windows
    /// (Windows.Media.SpeechSynthesis). Used for the error fallback line, like the Mac's
    /// NSSpeechSynthesizer fallback.
    /// </summary>
    public async Task StartSpeakingWithSystemVoiceAsync(string textToSpeak, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(textToSpeak);
        cancellationToken.ThrowIfCancellationRequested();

        long playbackRequestNumber = StopCurrentPlaybackAndBeginNewRequest();

        try
        {
            PreparedSpeechPlayback preparedSpeechPlayback = await Task.Run(
                async () =>
                {
                    byte[] synthesizedWaveFileBytes = await SynthesizeTextToWaveFileBytesAsync(textToSpeak, cancellationToken).ConfigureAwait(false);
                    return PrepareSpeechPlayback(() => new WaveFileReader(new MemoryStream(synthesizedWaveFileBytes, writable: false)));
                },
                cancellationToken).ConfigureAwait(false);

            StartPreparedPlayback(preparedSpeechPlayback, playbackRequestNumber, cancellationToken);
        }
        catch (Exception playbackStartException) when (playbackStartException is not OperationCanceledException)
        {
            ClickyApplicationLog.Write(LogCategory, $"Could not speak with the system voice: {playbackStartException.GetType().Name}: {playbackStartException.Message}");
            throw;
        }
    }

    /// <summary>Stops playback immediately. Safe from any thread and safe to call repeatedly.</summary>
    public void StopPlayback()
    {
        SpeechPlaybackSession? stoppedPlaybackSession;

        lock (playbackStateLock)
        {
            // Also cancels any start that is still decoding.
            latestPlaybackRequestNumber++;
            stoppedPlaybackSession = DetachAndStopCurrentSessionWhileLocked();
        }

        // Raised outside the lock so a handler can call back into this service.
        if (stoppedPlaybackSession is not null)
        {
            RaisePlaybackFinished();
        }
    }

    public void Dispose()
    {
        lock (playbackStateLock)
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
        }

        StopPlayback();
    }

    private long StopCurrentPlaybackAndBeginNewRequest()
    {
        SpeechPlaybackSession? stoppedPlaybackSession;
        long playbackRequestNumber;

        lock (playbackStateLock)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);

            latestPlaybackRequestNumber++;
            playbackRequestNumber = latestPlaybackRequestNumber;
            stoppedPlaybackSession = DetachAndStopCurrentSessionWhileLocked();
        }

        if (stoppedPlaybackSession is not null)
        {
            RaisePlaybackFinished();
        }

        return playbackRequestNumber;
    }

    /// <summary>
    /// Stops the current session and marks it finished. Returns it when this call is the one that
    /// finished it, so the caller raises PlaybackFinished exactly once. Its device and reader are
    /// released later, when its playback thread exits.
    /// </summary>
    private SpeechPlaybackSession? DetachAndStopCurrentSessionWhileLocked()
    {
        SpeechPlaybackSession? playbackSessionToStop = currentPlaybackSession;
        if (playbackSessionToStop is null)
        {
            return null;
        }

        currentPlaybackSession = null;

        if (playbackSessionToStop.HasFinished)
        {
            return null;
        }

        playbackSessionToStop.HasFinished = true;

        try
        {
            // waveOutReset returns queued buffers and kicks NAudio's playback thread, which then
            // exits and raises PlaybackStopped. It does not wait for that thread, so holding the
            // lock here cannot deadlock with HandlePlaybackStopped.
            playbackSessionToStop.WaveOutputDevice.Stop();
        }
        catch (Exception stopException)
        {
            ClickyApplicationLog.Write(LogCategory, $"Stopping speech playback threw {stopException.GetType().Name}: {stopException.Message}");
        }

        return playbackSessionToStop;
    }

    /// <summary>
    /// Makes the prepared audio the current session and starts it, unless it was superseded or
    /// canceled while it was being prepared.
    /// </summary>
    private void StartPreparedPlayback(PreparedSpeechPlayback preparedSpeechPlayback, long playbackRequestNumber, CancellationToken cancellationToken)
    {
        SpeechPlaybackSession newPlaybackSession = new(preparedSpeechPlayback.WaveOutputDevice, preparedSpeechPlayback.DecodedAudioReader);

        lock (playbackStateLock)
        {
            bool isStillTheLatestRequest = playbackRequestNumber == latestPlaybackRequestNumber;

            if (isDisposed || !isStillTheLatestRequest || cancellationToken.IsCancellationRequested)
            {
                // Play never ran, so PlaybackStopped will never fire to clean up. Release now.
                ReleaseSessionResourcesWhileLocked(newPlaybackSession);

                ObjectDisposedException.ThrowIf(isDisposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException("Speech playback was stopped or replaced before it started.");
            }

            newPlaybackSession.WaveOutputDevice.PlaybackStopped += (outputDeviceSender, playbackStoppedArguments) =>
                HandlePlaybackStopped(newPlaybackSession, playbackStoppedArguments);

            try
            {
                newPlaybackSession.WaveOutputDevice.Play();
            }
            catch
            {
                ReleaseSessionResourcesWhileLocked(newPlaybackSession);
                throw;
            }

            // Set before the lock is released, so even an instantly finished clip finds itself as
            // the current session when its PlaybackStopped handler takes the lock.
            currentPlaybackSession = newPlaybackSession;
        }
    }

    /// <summary>Runs on NAudio's playback thread after it has left its loop.</summary>
    private void HandlePlaybackStopped(SpeechPlaybackSession stoppedPlaybackSession, StoppedEventArgs playbackStoppedArguments)
    {
        bool shouldRaisePlaybackFinished = false;

        lock (playbackStateLock)
        {
            // Not finished yet means the audio ran out on its own (or the device failed). If
            // StopPlayback got here first it already raised PlaybackFinished, so only clean up.
            if (!stoppedPlaybackSession.HasFinished)
            {
                stoppedPlaybackSession.HasFinished = true;
                shouldRaisePlaybackFinished = true;
            }

            if (ReferenceEquals(currentPlaybackSession, stoppedPlaybackSession))
            {
                currentPlaybackSession = null;
            }

            ReleaseSessionResourcesWhileLocked(stoppedPlaybackSession);
        }

        if (playbackStoppedArguments.Exception is not null)
        {
            ClickyApplicationLog.Write(LogCategory, $"Speech playback ended with an error: {playbackStoppedArguments.Exception.GetType().Name}: {playbackStoppedArguments.Exception.Message}");
        }

        if (shouldRaisePlaybackFinished)
        {
            RaisePlaybackFinished();
        }
    }

    private void RaisePlaybackFinished()
    {
        try
        {
            PlaybackFinished?.Invoke();
        }
        catch (Exception subscriberException)
        {
            // This can run on a thread-pool thread, where an unhandled exception ends the process.
            ClickyApplicationLog.Write(LogCategory, $"A playback finished subscriber threw {subscriberException.GetType().Name}: {subscriberException.Message}");
        }
    }

    /// <summary>
    /// Opens the decoded audio and the default output device. Runs on the thread pool.
    /// The reader is disposed here if the device cannot be opened, so nothing leaks on failure.
    /// </summary>
    private static PreparedSpeechPlayback PrepareSpeechPlayback(Func<WaveStream> openDecodedAudioReader)
    {
        WaveStream decodedAudioReader = openDecodedAudioReader();
        WaveOutEvent? waveOutputDevice = null;

        try
        {
            // WaveOutEvent with the default device number (-1, WAVE_MAPPER) follows the Windows
            // default output device. It uses event callbacks, so it needs no window or message loop.
            waveOutputDevice = new WaveOutEvent();
            waveOutputDevice.Init(decodedAudioReader);
            return new PreparedSpeechPlayback(waveOutputDevice, decodedAudioReader);
        }
        catch
        {
            waveOutputDevice?.Dispose();
            decodedAudioReader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// ElevenLabs returns MP3. NAudio's Mp3FileReader uses the ACM MP3 decoder that ships with
    /// Windows and needs no setup, so it is tried first. Media Foundation is the fallback in case
    /// the ACM decoder is unavailable or rejects the file. Mp3FileReader scans every frame when it
    /// is constructed, so bytes that are not MP3 at all throw here rather than during playback.
    /// </summary>
    private static WaveStream OpenMp3AudioReader(byte[] mp3AudioBytes)
    {
        try
        {
            return new Mp3FileReader(new MemoryStream(mp3AudioBytes, writable: false));
        }
        catch (Exception acmDecoderException)
        {
            ClickyApplicationLog.Write(LogCategory, $"ACM MP3 decoder could not open the speech audio ({acmDecoderException.GetType().Name}: {acmDecoderException.Message}). Trying Media Foundation.");
        }

        // A fresh stream, because the failed reader may have moved or kept the first one.
        return new StreamMediaFoundationReader(new MemoryStream(mp3AudioBytes, writable: false));
    }

    /// <summary>
    /// SpeechSynthesizer produces a complete WAV file in memory. It is copied into a byte array so
    /// the WinRT stream can be released right away and NAudio reads a plain MemoryStream.
    /// </summary>
    private static async Task<byte[]> SynthesizeTextToWaveFileBytesAsync(string textToSpeak, CancellationToken cancellationToken)
    {
        using SpeechSynthesizer systemSpeechSynthesizer = new();
        using SpeechSynthesisStream synthesizedSpeechStream = await systemSpeechSynthesizer
            .SynthesizeTextToStreamAsync(textToSpeak)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        using Stream synthesizedSpeechReadStream = synthesizedSpeechStream.AsStreamForRead();
        using MemoryStream collectedWaveFileBytes = new();
        await synthesizedSpeechReadStream.CopyToAsync(collectedWaveFileBytes, cancellationToken).ConfigureAwait(false);
        return collectedWaveFileBytes.ToArray();
    }

    /// <summary>Guarded by playbackStateLock. Disposing under the lock keeps it from racing a Stop call.</summary>
    private static void ReleaseSessionResourcesWhileLocked(SpeechPlaybackSession playbackSession)
    {
        if (playbackSession.HasReleasedResources)
        {
            return;
        }

        playbackSession.HasReleasedResources = true;

        try
        {
            playbackSession.WaveOutputDevice.Dispose();
        }
        catch (Exception disposeException)
        {
            ClickyApplicationLog.Write(LogCategory, $"Closing the speech output device threw {disposeException.GetType().Name}: {disposeException.Message}");
        }

        try
        {
            playbackSession.DecodedAudioReader.Dispose();
        }
        catch (Exception disposeException)
        {
            ClickyApplicationLog.Write(LogCategory, $"Closing the speech audio reader threw {disposeException.GetType().Name}: {disposeException.Message}");
        }
    }

    private sealed record PreparedSpeechPlayback(WaveOutEvent WaveOutputDevice, WaveStream DecodedAudioReader);

    /// <summary>One playback: its output device, its decoded audio and whether it has finished.</summary>
    private sealed class SpeechPlaybackSession
    {
        public SpeechPlaybackSession(WaveOutEvent waveOutputDevice, WaveStream decodedAudioReader)
        {
            WaveOutputDevice = waveOutputDevice;
            DecodedAudioReader = decodedAudioReader;
        }

        public WaveOutEvent WaveOutputDevice { get; }

        public WaveStream DecodedAudioReader { get; }

        /// <summary>Guarded by playbackStateLock. Set by whichever of stop or natural end comes first.</summary>
        public bool HasFinished { get; set; }

        /// <summary>Guarded by playbackStateLock.</summary>
        public bool HasReleasedResources { get; set; }
    }
}
