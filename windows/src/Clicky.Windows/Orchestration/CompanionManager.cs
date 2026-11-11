using System.Diagnostics;
using System.Windows.Threading;
using Clicky.Core.Claude;
using Clicky.Core.Companion;
using Clicky.Core.Conversation;
using Clicky.Core.Overlay;
using Clicky.Core.Pointing;
using Clicky.Core.Prompts;
using Clicky.Core.PushToTalk;
using Clicky.Core.Screen;
using Clicky.Core.Settings;
using Clicky.Core.TextToSpeech;
using Clicky.Windows.AudioIO;
using Clicky.Windows.Capture;
using Clicky.Windows.Companion;
using Clicky.Windows.CursorOverlay;
using Clicky.Windows.Diagnostics;
using Clicky.Windows.Display;
using Clicky.Windows.Hotkey;

namespace Clicky.Windows.Orchestration;

/// <summary>Outcome of one full voice or typed turn, used by the --ask debug mode.</summary>
public sealed record CompanionTurnResult(bool DidSucceed, string FullResponseText, string SpokenText, PointTarget? PointTarget, string? FailureDescription);

/// <summary>
/// Central state machine, ported from CompanionManager.swift. Owns the push-to-talk pipeline:
/// shortcut, dictation, screen capture, Claude, ElevenLabs, and the cursor overlay. Exposes
/// <see cref="OverlayState"/> and <see cref="PanelState"/> for the views and implements the
/// panel's button actions. Runs on the WPF UI thread.
/// </summary>
public sealed class CompanionManager : ICompanionPanelActions, IDisposable
{
    private const string LogCategory = "Companion";

    private static readonly TimeSpan PermissionPollingInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan TransientHidePollingInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan TransientHidePauseAfterInteraction = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OverlayFadeOutDuration = TimeSpan.FromSeconds(0.4);
    private static readonly TimeSpan OnboardingPromptCharacterInterval = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan OnboardingPromptAutoDismissDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OnboardingPromptFadeOutDuration = TimeSpan.FromSeconds(0.35);
    private static readonly TimeSpan ResponseBubbleLingerAfterSpeech = TimeSpan.FromSeconds(1.5);

    private readonly ClickyUserSettingsStore userSettingsStore;
    private readonly ClickyUserSettings userSettings;
    private readonly ClaudeStreamingChatClient claudeChatClient;
    private readonly ElevenLabsSpeechClient elevenLabsSpeechClient;
    private readonly BuddyDictationManager buddyDictationManager;
    private readonly LowLevelKeyboardHookService keyboardHookService;
    private readonly ScreenCaptureService screenCaptureService;
    private readonly SpeechPlaybackService speechPlaybackService;
    private readonly OverlayWindowManager overlayWindowManager;
    private readonly Dispatcher userInterfaceDispatcher;
    private readonly ConversationHistory conversationHistory = new();

    private CompanionVoiceState voiceState = CompanionVoiceState.Idle;
    private bool isOverlayVisible;
    private Task? currentResponseTask;
    private CancellationTokenSource? currentResponseCancellation;
    private CancellationTokenSource? transientHideCancellation;
    private CancellationTokenSource? responseBubbleHideCancellation;
    private DispatcherTimer? permissionPollingTimer;
    private DispatcherTimer? onboardingPromptStreamTimer;
    private CancellationTokenSource? onboardingPromptDismissCancellation;
    private bool isOnboardingSequenceRunning;
    private bool isWaitingForOnboardingDemoPointingToFinish;

    public CompanionManager(
        ClickyUserSettingsStore userSettingsStore,
        ClickyUserSettings userSettings,
        ClaudeStreamingChatClient claudeChatClient,
        ElevenLabsSpeechClient elevenLabsSpeechClient,
        BuddyDictationManager buddyDictationManager,
        LowLevelKeyboardHookService keyboardHookService,
        ScreenCaptureService screenCaptureService,
        SpeechPlaybackService speechPlaybackService,
        OverlayWindowManager overlayWindowManager,
        CompanionOverlayState overlayState,
        Dispatcher userInterfaceDispatcher)
    {
        this.userSettingsStore = userSettingsStore;
        this.userSettings = userSettings;
        this.claudeChatClient = claudeChatClient;
        this.elevenLabsSpeechClient = elevenLabsSpeechClient;
        this.buddyDictationManager = buddyDictationManager;
        this.keyboardHookService = keyboardHookService;
        this.screenCaptureService = screenCaptureService;
        this.speechPlaybackService = speechPlaybackService;
        this.overlayWindowManager = overlayWindowManager;
        this.userInterfaceDispatcher = userInterfaceDispatcher;
        OverlayState = overlayState;

        PanelState = new CompanionPanelState
        {
            HasCompletedOnboarding = userSettings.HasCompletedOnboarding,
            SelectedModelIdentifier = userSettings.SelectedModelIdentifier,
            PushToTalkShortcutDisplayText = PushToTalkShortcutDefinitions.GetDisplayText(userSettings.PushToTalkShortcutOption),
        };

        claudeChatClient.ModelIdentifier = userSettings.SelectedModelIdentifier;
    }

    public CompanionOverlayState OverlayState { get; }

    public CompanionPanelState PanelState { get; }

    /// <summary>Raised when the panel should close (push-to-talk started, onboarding started). Mac: clickyDismissPanel.</summary>
    public event Action? PanelDismissRequested;

    /// <summary>Raised by "Quit Clicky"; the app shuts down in response.</summary>
    public event Action? QuitRequested;

    public CompanionVoiceState VoiceState
    {
        get => voiceState;
        private set
        {
            voiceState = value;
            OverlayState.VoiceState = value;
            PanelState.VoiceState = value;
        }
    }

    private bool IsOverlayVisible
    {
        get => isOverlayVisible;
        set
        {
            isOverlayVisible = value;
            PanelState.IsOverlayVisible = value;
        }
    }

    /// <summary>
    /// Wires every event and starts the keyboard hook. Shows the cursor right away when
    /// onboarding is done, the microphone is usable, and the cursor is enabled (same rule as the Mac).
    /// </summary>
    public void Start()
    {
        RefreshMicrophoneAvailability();
        ClickyApplicationLog.Write(
            LogCategory,
            $"Start: microphone {PanelState.MicrophoneAvailability}, onboarded {userSettings.HasCompletedOnboarding}, model {userSettings.SelectedModelIdentifier}, shortcut {userSettings.PushToTalkShortcut}.");

        permissionPollingTimer = new DispatcherTimer(DispatcherPriority.Background, userInterfaceDispatcher) { Interval = PermissionPollingInterval };
        permissionPollingTimer.Tick += (_, _) => RefreshMicrophoneAvailability();
        permissionPollingTimer.Start();

        buddyDictationManager.DictationStateChanged += HandleDictationStateChanged;
        buddyDictationManager.AudioPowerLevelChanged += audioPowerLevel => OverlayState.CurrentAudioPowerLevel = audioPowerLevel;
        keyboardHookService.ShortcutTransitionOccurred += HandleShortcutTransition;
        overlayWindowManager.WelcomeAnimationCompleted += HandleWelcomeAnimationCompleted;
        OverlayState.PointingAnimationFinished += HandlePointingAnimationFinished;
        speechPlaybackService.PlaybackFinished += () => userInterfaceDispatcher.BeginInvoke(HandleSpeechPlaybackFinished);

        try
        {
            keyboardHookService.Start();
        }
        catch (Exception hookStartException)
        {
            ClickyApplicationLog.Write(LogCategory, $"Keyboard hook failed to start, push-to-talk is unavailable: {hookStartException.Message}");
        }

        // Open the connection to the worker early so the first screenshot upload skips connection setup.
        _ = claudeChatClient.WarmUpConnectionAsync(CancellationToken.None);

        if (userSettings.HasCompletedOnboarding && PanelState.AreRequiredPermissionsGranted && userSettings.IsClickyCursorEnabled)
        {
            ShowOverlaysOnAllMonitors(isFirstAppearance: false);
        }
    }

    /// <summary>Shows the cursor overlay without a welcome animation (used by the --ask and --overlay-test modes).</summary>
    public void ShowCursorOverlayForDebugging()
    {
        ShowOverlaysOnAllMonitors(isFirstAppearance: false);
    }

    public void Dispose()
    {
        permissionPollingTimer?.Stop();
        onboardingPromptStreamTimer?.Stop();
        currentResponseCancellation?.Cancel();
        transientHideCancellation?.Cancel();
        responseBubbleHideCancellation?.Cancel();
        onboardingPromptDismissCancellation?.Cancel();
        buddyDictationManager.CancelCurrentDictation();
        speechPlaybackService.StopPlayback();
        overlayWindowManager.HideOverlays();
        IsOverlayVisible = false;
    }

    // MARK: - Panel actions

    public void StartOnboarding()
    {
        ClickyApplicationLog.Write(LogCategory, "Onboarding started.");
        PanelDismissRequested?.Invoke();

        // Mark onboarding as done now so the Start button never shows again, like the Mac.
        userSettings.HasCompletedOnboarding = true;
        SaveUserSettings();
        PanelState.HasCompletedOnboarding = true;

        BeginOnboardingSequence();
    }

    public void ReplayOnboarding()
    {
        ClickyApplicationLog.Write(LogCategory, "Onboarding replay requested.");
        PanelDismissRequested?.Invoke();
        BeginOnboardingSequence();
    }

    public void SelectModel(string modelIdentifier)
    {
        string normalizedModelIdentifier = ClaudeModelIdentifiers.NormalizeModelIdentifier(modelIdentifier);
        userSettings.SelectedModelIdentifier = normalizedModelIdentifier;
        SaveUserSettings();
        claudeChatClient.ModelIdentifier = normalizedModelIdentifier;
        PanelState.SelectedModelIdentifier = normalizedModelIdentifier;
        ClickyApplicationLog.Write(LogCategory, $"Model set to {normalizedModelIdentifier}.");
    }

    public void OpenMicrophonePrivacySettings()
    {
        MicrophoneAccessStatusReader.OpenMicrophonePrivacySettings();
    }

    public void QuitApplication()
    {
        QuitRequested?.Invoke();
    }

    // MARK: - Push-to-talk

    private void HandleShortcutTransition(PushToTalkShortcutTransition shortcutTransition)
    {
        switch (shortcutTransition)
        {
            case PushToTalkShortcutTransition.Pressed:
                HandleShortcutPressed();
                break;
            case PushToTalkShortcutTransition.Released:
                buddyDictationManager.StopPushToTalkFromKeyboardShortcut();
                break;
        }
    }

    private void HandleShortcutPressed()
    {
        if (buddyDictationManager.IsDictationInProgress)
        {
            return;
        }

        // The Mac ignores push-to-talk while the onboarding video plays; Windows ignores it while
        // the welcome bubble and pointing demo run.
        if (isOnboardingSequenceRunning)
        {
            return;
        }

        transientHideCancellation?.Cancel();
        transientHideCancellation = null;

        // Transient mode: bring the cursor back just for this interaction.
        if (!userSettings.IsClickyCursorEnabled && !IsOverlayVisible)
        {
            ShowOverlaysOnAllMonitors(isFirstAppearance: false);
        }

        PanelDismissRequested?.Invoke();

        // A new utterance interrupts the previous reply and its audio.
        currentResponseCancellation?.Cancel();
        speechPlaybackService.StopPlayback();
        HideResponseBubble();
        OverlayState.DetectedElementPointingTarget = null;

        if (OverlayState.IsOnboardingPromptVisible)
        {
            DismissOnboardingPrompt();
        }

        _ = buddyDictationManager.StartPushToTalkFromKeyboardShortcutAsync(finalTranscript =>
        {
            ClickyApplicationLog.Write(LogCategory, $"Transcript received ({finalTranscript.Length} characters).");
            _ = RunResponsePipelineAsync(finalTranscript, onAccumulatedResponseText: null);
        });
    }

    private void HandleDictationStateChanged()
    {
        CompanionVoiceState resolvedVoiceState = CompanionVoiceStateResolver.ResolveFromDictationFlags(
            VoiceState,
            buddyDictationManager.IsRecordingFromKeyboardShortcut,
            buddyDictationManager.IsFinalizingTranscript,
            buddyDictationManager.IsPreparingToRecord);

        if (resolvedVoiceState == VoiceState)
        {
            return;
        }

        VoiceState = resolvedVoiceState;

        if (resolvedVoiceState == CompanionVoiceState.Idle)
        {
            if (buddyDictationManager.LastMicrophoneProblem is not null)
            {
                RefreshMicrophoneAvailability();
            }

            // A press with nothing said starts no response, so schedule the hide here. While a
            // response is running, its own completion schedules the hide instead.
            if (currentResponseTask is null)
            {
                ScheduleTransientHideIfNeeded();
            }
        }
    }

    // MARK: - Response pipeline

    /// <summary>
    /// Typed turn for the --ask debug mode: runs the same pipeline as a spoken turn, then waits
    /// for the reply audio and any pointing flight to finish.
    /// </summary>
    public async Task<CompanionTurnResult> RunTypedDebugTurnAsync(string typedTranscript, Action<string> onResponseTextDelta)
    {
        string previouslyPrintedText = string.Empty;
        CompanionTurnResult turnResult = await RunResponsePipelineAsync(typedTranscript, accumulatedResponseText =>
        {
            string newTextDelta = accumulatedResponseText[previouslyPrintedText.Length..];
            previouslyPrintedText = accumulatedResponseText;
            onResponseTextDelta(newTextDelta);
        });

        while (speechPlaybackService.IsPlaying || OverlayState.DetectedElementPointingTarget is not null)
        {
            await Task.Delay(TransientHidePollingInterval);
        }

        return turnResult;
    }

    /// <summary>
    /// Captures every screen, sends them with the transcript to Claude, parses the POINT tag,
    /// starts the pointing flight, records history, and speaks the reply. The spinner stays up
    /// until the reply audio starts, same as the Mac.
    /// </summary>
    private Task<CompanionTurnResult> RunResponsePipelineAsync(string transcript, Action<string>? onAccumulatedResponseText)
    {
        currentResponseCancellation?.Cancel();
        speechPlaybackService.StopPlayback();

        CancellationTokenSource responseCancellation = new();
        currentResponseCancellation = responseCancellation;

        Task<CompanionTurnResult> responseTask = RunResponsePipelineCoreAsync(transcript, onAccumulatedResponseText, responseCancellation.Token);
        currentResponseTask = responseTask;
        return responseTask;
    }

    private async Task<CompanionTurnResult> RunResponsePipelineCoreAsync(
        string transcript,
        Action<string>? onAccumulatedResponseText,
        CancellationToken cancellationToken)
    {
        VoiceState = CompanionVoiceState.Processing;
        HideResponseBubble();

        string fullResponseText = string.Empty;
        string spokenText = string.Empty;
        PointTarget? pointTarget = null;
        string? failureDescription = null;

        try
        {
            Stopwatch turnStopwatch = Stopwatch.StartNew();
            IReadOnlyList<CapturedScreen> capturedScreens = await screenCaptureService.CaptureAllScreensAsJpegAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ClickyApplicationLog.Write(LogCategory, $"Captured {capturedScreens.Count} screen(s) in {turnStopwatch.ElapsedMilliseconds} ms: {string.Join("; ", screenCaptureService.LastCaptureBackendDescriptions)}");

            List<LabeledImage> labeledImages = capturedScreens
                .Select(capturedScreen => new LabeledImage(capturedScreen.JpegImageBytes, capturedScreen.LabelWithImageDimensions))
                .ToList();

            ClaudeChatRequest chatRequest = new(
                labeledImages,
                CompanionSystemPrompts.CompanionVoiceResponseSystemPrompt,
                conversationHistory.GetExchangesOldestFirst(),
                transcript);

            ClaudeStreamingResponse streamingResponse = await claudeChatClient.StreamResponseAsync(
                chatRequest,
                accumulatedResponseText => userInterfaceDispatcher.BeginInvoke(() => onAccumulatedResponseText?.Invoke(accumulatedResponseText)),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            fullResponseText = streamingResponse.FullResponseText;
            ClickyApplicationLog.Write(LogCategory, $"Claude replied in {streamingResponse.Duration.TotalMilliseconds:F0} ms ({fullResponseText.Length} characters, model {claudeChatClient.ModelIdentifier}).");

            PointTagParseResult pointTagParseResult = PointTagParser.Parse(fullResponseText);
            spokenText = pointTagParseResult.SpokenText;

            // Switch to idle before setting the target so the triangle (not the spinner) is the
            // shape that flies. Same ordering as the Mac.
            if (pointTagParseResult.HasPointCoordinate)
            {
                VoiceState = CompanionVoiceState.Idle;
            }

            pointTarget = PointTargetResolver.ResolvePointTarget(pointTagParseResult, capturedScreens);
            if (pointTarget is not null)
            {
                OverlayState.DetectedElementPointingTarget = new OverlayPointingTarget(
                    pointTarget.TargetLocationInPhysicalPixels,
                    pointTarget.TargetScreen.MonitorBoundsInPhysicalPixels,
                    CustomBubbleText: null);
                ClickyApplicationLog.Write(
                    LogCategory,
                    $"Element pointing: {pointTagParseResult.ScreenshotCoordinate} on {pointTarget.TargetScreen.MonitorDeviceName} -> physical {pointTarget.TargetLocationInPhysicalPixels} \"{pointTagParseResult.ElementLabel ?? "element"}\"");
            }
            else
            {
                ClickyApplicationLog.Write(LogCategory, $"Element pointing: {pointTagParseResult.ElementLabel ?? "no element"}");
            }

            // History stores the reply without the tag so it does not confuse later turns.
            conversationHistory.AppendExchange(transcript, spokenText);
            ClickyApplicationLog.Write(LogCategory, $"Conversation history: {conversationHistory.ExchangeCount} exchanges.");

            if (spokenText.Trim().Length > 0)
            {
                try
                {
                    byte[] spokenAudioBytes = await elevenLabsSpeechClient.FetchSpeechAudioAsync(spokenText, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    await speechPlaybackService.StartPlayingMp3Async(spokenAudioBytes, cancellationToken);
                    VoiceState = CompanionVoiceState.Responding;
                    ShowResponseBubble(spokenText);
                    ClickyApplicationLog.Write(LogCategory, $"Speaking reply ({spokenAudioBytes.Length / 1024} KB of audio).");
                }
                catch (Exception textToSpeechException) when (textToSpeechException is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    failureDescription = $"TTS failed: {textToSpeechException.Message}";
                    ClickyApplicationLog.Write(LogCategory, failureDescription);
                    await SpeakErrorFallbackAsync();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user spoke again; this response was interrupted on purpose.
            return new CompanionTurnResult(false, fullResponseText, spokenText, pointTarget, "Cancelled by a newer request.");
        }
        catch (Exception responseException)
        {
            failureDescription = $"Response failed: {responseException.GetType().Name}: {responseException.Message}";
            ClickyApplicationLog.Write(LogCategory, failureDescription);
            await SpeakErrorFallbackAsync();
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                currentResponseTask = null;
            }
        }

        // The Mac returns to idle as soon as the audio has started (the triangle shows for both
        // idle and responding) and then schedules the transient hide, which waits for the audio.
        if (!cancellationToken.IsCancellationRequested)
        {
            VoiceState = CompanionVoiceState.Idle;
            ScheduleTransientHideIfNeeded();
        }

        return new CompanionTurnResult(failureDescription is null, fullResponseText, spokenText, pointTarget, failureDescription);
    }

    /// <summary>Speaks the TODO fallback line with the Windows system voice (the Mac uses NSSpeechSynthesizer).</summary>
    private async Task SpeakErrorFallbackAsync()
    {
        try
        {
            await speechPlaybackService.StartSpeakingWithSystemVoiceAsync(WindowsPortTodoConstants.ResponseErrorFallbackSpokenLine, CancellationToken.None);
            VoiceState = CompanionVoiceState.Responding;
        }
        catch (Exception systemVoiceException)
        {
            ClickyApplicationLog.Write(LogCategory, $"System voice fallback failed: {systemVoiceException.Message}");
        }
    }

    // MARK: - Response bubble

    private void ShowResponseBubble(string spokenText)
    {
        responseBubbleHideCancellation?.Cancel();
        responseBubbleHideCancellation = null;
        OverlayState.ResponseBubbleText = spokenText;
        OverlayState.IsResponseBubbleVisible = true;
    }

    private void HideResponseBubble()
    {
        responseBubbleHideCancellation?.Cancel();
        responseBubbleHideCancellation = null;
        OverlayState.IsResponseBubbleVisible = false;
    }

    private async void HandleSpeechPlaybackFinished()
    {
        if (!OverlayState.IsResponseBubbleVisible)
        {
            return;
        }

        responseBubbleHideCancellation?.Cancel();
        CancellationTokenSource bubbleHideCancellation = new();
        responseBubbleHideCancellation = bubbleHideCancellation;

        try
        {
            await Task.Delay(ResponseBubbleLingerAfterSpeech, bubbleHideCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!speechPlaybackService.IsPlaying)
        {
            OverlayState.IsResponseBubbleVisible = false;
        }
    }

    // MARK: - Transient cursor mode

    /// <summary>
    /// When "Show Clicky" is off, waits for the reply audio and any pointing flight to finish,
    /// pauses one second, then fades the overlay out. Cancelled by the next push-to-talk press.
    /// </summary>
    private async void ScheduleTransientHideIfNeeded()
    {
        if (userSettings.IsClickyCursorEnabled || !IsOverlayVisible)
        {
            return;
        }

        transientHideCancellation?.Cancel();
        CancellationTokenSource hideCancellation = new();
        transientHideCancellation = hideCancellation;

        try
        {
            while (speechPlaybackService.IsPlaying)
            {
                await Task.Delay(TransientHidePollingInterval, hideCancellation.Token);
            }

            while (OverlayState.DetectedElementPointingTarget is not null)
            {
                await Task.Delay(TransientHidePollingInterval, hideCancellation.Token);
            }

            await Task.Delay(TransientHidePauseAfterInteraction, hideCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        overlayWindowManager.FadeOutAndHideOverlays(OverlayFadeOutDuration);
        IsOverlayVisible = false;
    }

    // MARK: - Onboarding

    private void BeginOnboardingSequence()
    {
        // TODO(windows-port): the Mac also starts background music here; see WindowsPortTodoConstants.OnboardingMusicAssetFileName.
        isOnboardingSequenceRunning = true;
        isWaitingForOnboardingDemoPointingToFinish = false;
        DismissOnboardingPrompt();
        ShowOverlaysOnAllMonitors(isFirstAppearance: true);
    }

    /// <summary>
    /// The Mac plays an intro video after the welcome bubble and runs the pointing demo 40 seconds
    /// in, then shows the "press control + option" prompt when the video ends. Windows has no
    /// video (see WindowsPortTodoConstants.OnboardingIntroVideoUrl), so the demo runs right after
    /// the welcome bubble and the prompt follows once the demo's pointing flight is done.
    /// </summary>
    private async void HandleWelcomeAnimationCompleted()
    {
        bool didStartPointing = await PerformOnboardingDemoInteractionAsync();
        if (didStartPointing)
        {
            isWaitingForOnboardingDemoPointingToFinish = true;
            return;
        }

        FinishOnboardingSequence();
    }

    private void HandlePointingAnimationFinished()
    {
        if (!isWaitingForOnboardingDemoPointingToFinish)
        {
            return;
        }

        isWaitingForOnboardingDemoPointingToFinish = false;
        FinishOnboardingSequence();
    }

    private void FinishOnboardingSequence()
    {
        isOnboardingSequenceRunning = false;
        StartOnboardingPromptStream();
    }

    /// <summary>
    /// Captures the cursor screen and asks Claude for one specific thing to point at with a short
    /// comment, then flies there with that comment as the bubble text. Returns true when a pointing
    /// flight started. Port of performOnboardingDemoInteraction.
    /// </summary>
    private async Task<bool> PerformOnboardingDemoInteractionAsync()
    {
        if (VoiceState is not (CompanionVoiceState.Idle or CompanionVoiceState.Responding))
        {
            return false;
        }

        try
        {
            IReadOnlyList<CapturedScreen> capturedScreens = await screenCaptureService.CaptureAllScreensAsJpegAsync(CancellationToken.None);

            // Only the cursor screen is sent, so Claude cannot pick something on another monitor.
            CapturedScreen? cursorScreenCapture = capturedScreens.FirstOrDefault(capturedScreen => capturedScreen.IsCursorScreen);
            if (cursorScreenCapture is null)
            {
                ClickyApplicationLog.Write(LogCategory, "Onboarding demo: no cursor screen found.");
                return false;
            }

            ClaudeChatRequest demoChatRequest = new(
                [new LabeledImage(cursorScreenCapture.JpegImageBytes, cursorScreenCapture.LabelWithImageDimensions)],
                CompanionSystemPrompts.OnboardingDemoSystemPrompt,
                [],
                CompanionSystemPrompts.OnboardingDemoUserPrompt);

            ClaudeStreamingResponse demoResponse = await claudeChatClient.StreamResponseAsync(demoChatRequest, null, CancellationToken.None);
            PointTagParseResult demoParseResult = PointTagParser.Parse(demoResponse.FullResponseText);
            PointTarget? demoPointTarget = PointTargetResolver.ResolvePointTarget(demoParseResult, [cursorScreenCapture]);

            if (demoPointTarget is null)
            {
                ClickyApplicationLog.Write(LogCategory, "Onboarding demo: no element to point at.");
                return false;
            }

            OverlayState.DetectedElementPointingTarget = new OverlayPointingTarget(
                demoPointTarget.TargetLocationInPhysicalPixels,
                cursorScreenCapture.MonitorBoundsInPhysicalPixels,
                CustomBubbleText: demoParseResult.SpokenText);
            ClickyApplicationLog.Write(LogCategory, $"Onboarding demo: pointing at \"{demoParseResult.ElementLabel ?? "element"}\".");
            return true;
        }
        catch (Exception demoException)
        {
            ClickyApplicationLog.Write(LogCategory, $"Onboarding demo error: {demoException.GetType().Name}: {demoException.Message}");
            return false;
        }
    }

    /// <summary>Streams "press ctrl + alt and introduce yourself" next to the cursor, then auto-dismisses after 10 s.</summary>
    private void StartOnboardingPromptStream()
    {
        string onboardingPromptMessage = PointingBubblePhrases.BuildOnboardingPromptText(PanelState.PushToTalkShortcutDisplayText);

        onboardingPromptStreamTimer?.Stop();
        onboardingPromptDismissCancellation?.Cancel();
        OverlayState.OnboardingPromptText = string.Empty;
        OverlayState.IsOnboardingPromptVisible = true;

        int nextCharacterIndex = 0;
        onboardingPromptStreamTimer = new DispatcherTimer(DispatcherPriority.Normal, userInterfaceDispatcher) { Interval = OnboardingPromptCharacterInterval };
        onboardingPromptStreamTimer.Tick += async (_, _) =>
        {
            if (nextCharacterIndex < onboardingPromptMessage.Length)
            {
                OverlayState.OnboardingPromptText += onboardingPromptMessage[nextCharacterIndex];
                nextCharacterIndex++;
                return;
            }

            onboardingPromptStreamTimer?.Stop();

            CancellationTokenSource promptDismissCancellation = new();
            onboardingPromptDismissCancellation = promptDismissCancellation;
            try
            {
                await Task.Delay(OnboardingPromptAutoDismissDelay, promptDismissCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            DismissOnboardingPrompt();
        };
        onboardingPromptStreamTimer.Start();
    }

    private async void DismissOnboardingPrompt()
    {
        onboardingPromptStreamTimer?.Stop();
        onboardingPromptDismissCancellation?.Cancel();
        onboardingPromptDismissCancellation = null;

        if (!OverlayState.IsOnboardingPromptVisible)
        {
            return;
        }

        OverlayState.IsOnboardingPromptVisible = false;
        await Task.Delay(OnboardingPromptFadeOutDuration);
        if (!OverlayState.IsOnboardingPromptVisible)
        {
            OverlayState.OnboardingPromptText = string.Empty;
        }
    }

    // MARK: - Permissions and overlay

    private void RefreshMicrophoneAvailability()
    {
        MicrophoneAvailability previousMicrophoneAvailability = PanelState.MicrophoneAvailability;
        MicrophoneAvailability currentMicrophoneAvailability = MicrophoneAccessStatusReader.ReadCurrentAvailability();
        PanelState.MicrophoneAvailability = currentMicrophoneAvailability;

        if (previousMicrophoneAvailability != currentMicrophoneAvailability)
        {
            ClickyApplicationLog.Write(LogCategory, $"Microphone availability: {currentMicrophoneAvailability}.");
        }

        // Mac: once permissions come back after onboarding, show the cursor again.
        if (currentMicrophoneAvailability == MicrophoneAvailability.Available
            && userSettings.HasCompletedOnboarding
            && userSettings.IsClickyCursorEnabled
            && !IsOverlayVisible)
        {
            ShowOverlaysOnAllMonitors(isFirstAppearance: false);
        }
    }

    private void ShowOverlaysOnAllMonitors(bool isFirstAppearance)
    {
        IReadOnlyList<DisplayMonitorInfo> connectedMonitors = DisplayMonitorEnumerator.EnumerateMonitors();
        overlayWindowManager.ShowOverlays(connectedMonitors, isFirstAppearance);
        IsOverlayVisible = true;
    }

    /// <summary>Monitors were added, removed or rescaled: rebuild the overlays at the new geometry.</summary>
    public void HandleDisplayConfigurationChanged()
    {
        if (!IsOverlayVisible)
        {
            return;
        }

        ClickyApplicationLog.Write(LogCategory, "Display configuration changed; rebuilding overlays.");
        ShowOverlaysOnAllMonitors(isFirstAppearance: false);
    }

    private void SaveUserSettings()
    {
        try
        {
            userSettingsStore.Save(userSettings);
        }
        catch (Exception settingsSaveException)
        {
            ClickyApplicationLog.Write(LogCategory, $"Could not save settings: {settingsSaveException.Message}");
        }
    }
}
