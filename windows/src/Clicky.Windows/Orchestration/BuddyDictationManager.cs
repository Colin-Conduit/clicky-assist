using System.Windows.Threading;
using Clicky.Core.Transcription;
using Clicky.Windows.AudioIO;
using Clicky.Windows.Companion;
using Clicky.Windows.Diagnostics;

namespace Clicky.Windows.Orchestration;

/// <summary>
/// Push-to-talk dictation for the keyboard shortcut, ported from BuddyDictationManager.swift
/// (keyboard path only; the Mac's microphone-button path belongs to a help chat that does not
/// exist on Windows). Owns the microphone and one AssemblyAI streaming session per press.
///
/// Flow: key down starts the microphone and opens a transcription session; key up stops the
/// microphone, asks AssemblyAI for the final transcript, and arms a fallback timer; whichever
/// arrives first (final transcript, error with a usable partial, or the timer) finishes the
/// session and hands the transcript to the companion.
///
/// Everything here runs on the WPF UI thread. Microphone and websocket callbacks arrive on
/// background threads and are marshaled back with the dispatcher.
/// </summary>
public sealed class BuddyDictationManager : IDisposable
{
    private const string LogCategory = "Dictation";

    /// <summary>Fallback when the session does not specify its own delay (the Mac default).</summary>
    private static readonly TimeSpan DefaultFinalTranscriptFallbackDelay = TimeSpan.FromSeconds(2.4);

    private readonly MicrophoneCaptureService microphoneCaptureService;
    private readonly AssemblyAIStreamingTranscriptionProvider transcriptionProvider;
    private readonly Dispatcher userInterfaceDispatcher;

    private AssemblyAIStreamingTranscriptionSession? activeTranscriptionSession;
    private CancellationTokenSource? pendingStartCancellation;
    private DispatcherTimer? finalTranscriptFallbackTimer;
    private Action<string>? submitFinalTranscript;
    private string latestRecognizedText = string.Empty;
    private bool hasFinishedCurrentDictationSession;
    private bool isKeyboardShortcutSessionActive;

    // Audio captured while the websocket is still connecting. The Mac starts its audio engine
    // only after the session is ready, which drops the first words if the user starts talking
    // right away; buffering here keeps them. Guarded by pendingAudioLock because audio arrives
    // on the capture thread.
    private readonly object pendingAudioLock = new();
    private readonly List<byte[]> audioCapturedBeforeSessionReady = [];
    private AssemblyAIStreamingTranscriptionSession? sessionReceivingLiveAudio;

    public BuddyDictationManager(
        MicrophoneCaptureService microphoneCaptureService,
        AssemblyAIStreamingTranscriptionProvider transcriptionProvider,
        Dispatcher userInterfaceDispatcher)
    {
        this.microphoneCaptureService = microphoneCaptureService;
        this.transcriptionProvider = transcriptionProvider;
        this.userInterfaceDispatcher = userInterfaceDispatcher;

        microphoneCaptureService.Pcm16AudioAvailable += HandleMicrophonePcm16AudioAvailable;
        microphoneCaptureService.AudioPowerLevelChanged += HandleMicrophoneAudioPowerLevelChanged;
        microphoneCaptureService.CaptureFailed += HandleMicrophoneCaptureFailed;
    }

    /// <summary>Raised on the UI thread whenever one of the flags below changes.</summary>
    public event Action? DictationStateChanged;

    /// <summary>Raised on the UI thread with the latest microphone level (0..1).</summary>
    public event Action<double>? AudioPowerLevelChanged;

    public bool IsRecordingFromKeyboardShortcut { get; private set; }
    public bool IsFinalizingTranscript { get; private set; }
    public bool IsPreparingToRecord { get; private set; }
    public double CurrentAudioPowerLevel { get; private set; }
    public string? LastErrorMessage { get; private set; }
    public MicrophoneAvailability? LastMicrophoneProblem { get; private set; }

    public bool IsDictationInProgress => IsPreparingToRecord || IsRecordingFromKeyboardShortcut || IsFinalizingTranscript;

    public string TranscriptionProviderDisplayName => transcriptionProvider.DisplayName;

    /// <summary>
    /// Key down. Starts the microphone at once (so the waveform reacts immediately) and opens the
    /// AssemblyAI session in the background. <paramref name="submitFinalTranscript"/> receives the
    /// final, non-empty transcript after key up.
    /// </summary>
    public async Task StartPushToTalkFromKeyboardShortcutAsync(Action<string> submitFinalTranscript)
    {
        if (IsDictationInProgress)
        {
            return;
        }

        ClickyApplicationLog.Write(LogCategory, "Start requested (keyboard shortcut).");

        pendingStartCancellation?.Cancel();
        CancellationTokenSource startCancellation = new();
        pendingStartCancellation = startCancellation;

        LastErrorMessage = null;
        LastMicrophoneProblem = null;
        this.submitFinalTranscript = submitFinalTranscript;
        latestRecognizedText = string.Empty;
        hasFinishedCurrentDictationSession = false;
        isKeyboardShortcutSessionActive = true;
        IsPreparingToRecord = true;
        IsFinalizingTranscript = false;
        IsRecordingFromKeyboardShortcut = true;
        CurrentAudioPowerLevel = 0;
        lock (pendingAudioLock)
        {
            audioCapturedBeforeSessionReady.Clear();
            sessionReceivingLiveAudio = null;
        }

        RaiseDictationStateChanged();

        try
        {
            microphoneCaptureService.StartCapture();
        }
        catch (MicrophoneUnavailableException microphoneUnavailableException)
        {
            ClickyApplicationLog.Write(LogCategory, $"Microphone unavailable: {microphoneUnavailableException.Message}");
            LastMicrophoneProblem = microphoneUnavailableException.MicrophoneAvailability;
            LastErrorMessage = microphoneUnavailableException.MicrophoneAvailability == MicrophoneAvailability.NoDeviceFound
                ? "no microphone found. plug one in and try again."
                : "microphone access is turned off in windows settings.";
            ResetSessionState();
            return;
        }
        catch (Exception microphoneStartException)
        {
            // Any other start failure (for example another app holding the device in exclusive
            // mode) must also end the press cleanly instead of leaving the waveform up with no audio.
            ClickyApplicationLog.Write(LogCategory, $"Microphone failed to start: {microphoneStartException.GetType().Name}: {microphoneStartException.Message}");
            LastErrorMessage = "couldn't start voice input. try again.";
            ResetSessionState();
            return;
        }

        // The session callbacks are created before the session object exists. They capture this
        // per-press holder so they can ignore events from a session that has already been
        // replaced by a newer press (the Mac uses request identifiers for the same purpose).
        SessionReferenceHolder openedSessionReference = new();

        try
        {
            AssemblyAIStreamingTranscriptionSession openedSession = await transcriptionProvider.StartStreamingSessionAsync(
                TranscriptionKeyterms.BuildKeyterms([]),
                onTranscriptUpdate: transcriptText => userInterfaceDispatcher.BeginInvoke(() => HandleTranscriptUpdate(openedSessionReference, transcriptText)),
                onFinalTranscriptReady: transcriptText => userInterfaceDispatcher.BeginInvoke(() => HandleFinalTranscriptReady(openedSessionReference, transcriptText)),
                onError: sessionError => userInterfaceDispatcher.BeginInvoke(() => HandleRecognitionError(openedSessionReference, sessionError)),
                startCancellation.Token);

            openedSessionReference.Session = openedSession;

            bool wasStartSuperseded = startCancellation.IsCancellationRequested || !ReferenceEquals(pendingStartCancellation, startCancellation);
            if (wasStartSuperseded)
            {
                ClickyApplicationLog.Write(LogCategory, "Start cancelled (shortcut released while the session was opening).");
                openedSession.Cancel();
                return;
            }

            activeTranscriptionSession = openedSession;
            FlushAudioCapturedBeforeSessionReady(openedSession);
            IsPreparingToRecord = false;
            RaiseDictationStateChanged();
            ClickyApplicationLog.Write(LogCategory, $"Recognition session started ({transcriptionProvider.DisplayName}).");
        }
        catch (OperationCanceledException) when (startCancellation.IsCancellationRequested)
        {
            ClickyApplicationLog.Write(LogCategory, "Start cancelled before the session opened.");
        }
        catch (Exception sessionStartException)
        {
            ClickyApplicationLog.Write(LogCategory, $"Failed to start recognition session ({transcriptionProvider.DisplayName}): {sessionStartException.GetType().Name}: {sessionStartException.Message}");
            if (ReferenceEquals(pendingStartCancellation, startCancellation))
            {
                LastErrorMessage = "couldn't start voice input. try again.";
                microphoneCaptureService.StopCapture();
                ResetSessionState();
            }
        }
    }

    /// <summary>
    /// Key up. A quick tap released before the session opened is dropped, like the Mac.
    /// Otherwise the microphone stops and the final transcript is requested.
    /// </summary>
    public void StopPushToTalkFromKeyboardShortcut()
    {
        if (!isKeyboardShortcutSessionActive)
        {
            IsPreparingToRecord = false;
            RaiseDictationStateChanged();
            return;
        }

        isKeyboardShortcutSessionActive = false;

        if (activeTranscriptionSession is null)
        {
            // Released while the websocket was still connecting. The Mac cancels the start task
            // here and records nothing, so a quick tap does not send an empty turn.
            ClickyApplicationLog.Write(LogCategory, "Released before the session opened; cancelling.");
            pendingStartCancellation?.Cancel();
            microphoneCaptureService.StopCapture();
            ResetSessionState();
            return;
        }

        BeginFinalizingTranscript();
    }

    /// <summary>Abandons the current dictation without submitting anything.</summary>
    public void CancelCurrentDictation()
    {
        pendingStartCancellation?.Cancel();

        if (!IsDictationInProgress)
        {
            return;
        }

        StopFinalTranscriptFallbackTimer();
        microphoneCaptureService.StopCapture();
        activeTranscriptionSession?.Cancel();
        ResetSessionState();
    }

    public void Dispose()
    {
        CancelCurrentDictation();
        microphoneCaptureService.Pcm16AudioAvailable -= HandleMicrophonePcm16AudioAvailable;
        microphoneCaptureService.AudioPowerLevelChanged -= HandleMicrophoneAudioPowerLevelChanged;
        microphoneCaptureService.CaptureFailed -= HandleMicrophoneCaptureFailed;
    }

    private void BeginFinalizingTranscript()
    {
        if (IsFinalizingTranscript || activeTranscriptionSession is null)
        {
            return;
        }

        ClickyApplicationLog.Write(LogCategory, "Stop requested (keyboard shortcut); finalizing transcript.");

        IsRecordingFromKeyboardShortcut = false;
        IsFinalizingTranscript = true;
        RaiseDictationStateChanged();

        microphoneCaptureService.StopCapture();
        activeTranscriptionSession.RequestFinalTranscript();

        TimeSpan finalTranscriptFallbackDelay = activeTranscriptionSession.FinalTranscriptFallbackDelay;
        if (finalTranscriptFallbackDelay <= TimeSpan.Zero)
        {
            finalTranscriptFallbackDelay = DefaultFinalTranscriptFallbackDelay;
        }

        StopFinalTranscriptFallbackTimer();
        finalTranscriptFallbackTimer = new DispatcherTimer(DispatcherPriority.Normal, userInterfaceDispatcher)
        {
            Interval = finalTranscriptFallbackDelay,
        };
        finalTranscriptFallbackTimer.Tick += (_, _) =>
        {
            ClickyApplicationLog.Write(LogCategory, "Final transcript fallback timer fired.");
            FinishCurrentDictationSessionIfNeeded(shouldSubmitFinalTranscript: true);
        };
        finalTranscriptFallbackTimer.Start();
    }

    private void HandleTranscriptUpdate(SessionReferenceHolder sessionReference, string transcriptText)
    {
        if (!ReferenceEquals(sessionReference.Session, activeTranscriptionSession))
        {
            return;
        }

        latestRecognizedText = transcriptText;
    }

    private void HandleFinalTranscriptReady(SessionReferenceHolder sessionReference, string transcriptText)
    {
        if (!ReferenceEquals(sessionReference.Session, activeTranscriptionSession))
        {
            return;
        }

        latestRecognizedText = transcriptText;
        if (IsFinalizingTranscript)
        {
            FinishCurrentDictationSessionIfNeeded(shouldSubmitFinalTranscript: true);
        }
    }

    private void HandleRecognitionError(SessionReferenceHolder sessionReference, Exception sessionError)
    {
        if (!ReferenceEquals(sessionReference.Session, activeTranscriptionSession) || hasFinishedCurrentDictationSession)
        {
            return;
        }

        if (IsFinalizingTranscript && latestRecognizedText.Trim().Length > 0)
        {
            FinishCurrentDictationSessionIfNeeded(shouldSubmitFinalTranscript: true);
            return;
        }

        ClickyApplicationLog.Write(LogCategory, $"Dictation error ({transcriptionProvider.DisplayName}): {sessionError.Message}");
        LastErrorMessage = "couldn't transcribe that. try again.";
        CancelCurrentDictation();
    }

    private void FinishCurrentDictationSessionIfNeeded(bool shouldSubmitFinalTranscript)
    {
        if (hasFinishedCurrentDictationSession)
        {
            return;
        }

        hasFinishedCurrentDictationSession = true;
        StopFinalTranscriptFallbackTimer();

        string finalTranscriptText = latestRecognizedText.Trim();
        Action<string>? submitFinalTranscriptCallback = submitFinalTranscript;

        microphoneCaptureService.StopCapture();
        activeTranscriptionSession?.Cancel();
        ResetSessionState();

        if (!shouldSubmitFinalTranscript || finalTranscriptText.Length == 0)
        {
            ClickyApplicationLog.Write(LogCategory, "Session finished with no transcript to submit.");
            return;
        }

        submitFinalTranscriptCallback?.Invoke(finalTranscriptText);
    }

    private void ResetSessionState()
    {
        pendingStartCancellation = null;
        activeTranscriptionSession = null;
        submitFinalTranscript = null;
        latestRecognizedText = string.Empty;
        hasFinishedCurrentDictationSession = false;
        isKeyboardShortcutSessionActive = false;
        IsPreparingToRecord = false;
        IsRecordingFromKeyboardShortcut = false;
        IsFinalizingTranscript = false;
        CurrentAudioPowerLevel = 0;
        lock (pendingAudioLock)
        {
            audioCapturedBeforeSessionReady.Clear();
            sessionReceivingLiveAudio = null;
        }

        AudioPowerLevelChanged?.Invoke(0);
        RaiseDictationStateChanged();
    }

    private void StopFinalTranscriptFallbackTimer()
    {
        finalTranscriptFallbackTimer?.Stop();
        finalTranscriptFallbackTimer = null;
    }

    private void FlushAudioCapturedBeforeSessionReady(AssemblyAIStreamingTranscriptionSession readySession)
    {
        lock (pendingAudioLock)
        {
            foreach (byte[] bufferedPcm16Audio in audioCapturedBeforeSessionReady)
            {
                readySession.AppendPcm16Audio(bufferedPcm16Audio);
            }

            audioCapturedBeforeSessionReady.Clear();
            sessionReceivingLiveAudio = readySession;
        }
    }

    /// <summary>Capture thread. Sends audio straight to the session once it is ready, buffers it before that.</summary>
    private void HandleMicrophonePcm16AudioAvailable(byte[] pcm16AudioBytes)
    {
        lock (pendingAudioLock)
        {
            if (sessionReceivingLiveAudio is not null)
            {
                sessionReceivingLiveAudio.AppendPcm16Audio(pcm16AudioBytes);
            }
            else
            {
                audioCapturedBeforeSessionReady.Add(pcm16AudioBytes);
            }
        }
    }

    /// <summary>Capture thread.</summary>
    private void HandleMicrophoneAudioPowerLevelChanged(double audioPowerLevel)
    {
        userInterfaceDispatcher.BeginInvoke(() =>
        {
            if (!IsRecordingFromKeyboardShortcut)
            {
                return;
            }

            CurrentAudioPowerLevel = audioPowerLevel;
            AudioPowerLevelChanged?.Invoke(audioPowerLevel);
        });
    }

    /// <summary>Capture thread.</summary>
    private void HandleMicrophoneCaptureFailed(Exception captureException)
    {
        userInterfaceDispatcher.BeginInvoke(() =>
        {
            ClickyApplicationLog.Write(LogCategory, $"Microphone capture failed: {captureException.Message}");
            if (IsRecordingFromKeyboardShortcut && activeTranscriptionSession is not null)
            {
                // Treat it like a key release so whatever was said so far still gets answered.
                isKeyboardShortcutSessionActive = false;
                BeginFinalizingTranscript();
                return;
            }

            LastErrorMessage = "the microphone stopped unexpectedly.";
            CancelCurrentDictation();
        });
    }

    private void RaiseDictationStateChanged()
    {
        DictationStateChanged?.Invoke();
    }

    private sealed class SessionReferenceHolder
    {
        public AssemblyAIStreamingTranscriptionSession? Session { get; set; }
    }
}
