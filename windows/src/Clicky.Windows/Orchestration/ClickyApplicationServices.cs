using System.Net.Http;
using System.Windows.Threading;
using Clicky.Core.Claude;
using Clicky.Core.Configuration;
using Clicky.Core.Settings;
using Clicky.Core.TextToSpeech;
using Clicky.Core.Transcription;
using Clicky.Windows.AudioIO;
using Clicky.Windows.Capture;
using Clicky.Windows.Companion;
using Clicky.Windows.CursorOverlay;
using Clicky.Windows.Hotkey;

namespace Clicky.Windows.Orchestration;

/// <summary>
/// Composition root: builds every long-lived service once, with one shared HttpClient for
/// all worker requests, and disposes them in reverse order on exit.
/// </summary>
public sealed class ClickyApplicationServices : IDisposable
{
    private readonly HttpClient sharedWorkerHttpClient;

    public ClickyApplicationServices(
        ClickyWorkerConfiguration workerConfiguration,
        ScreenCaptureBackendPreference screenCaptureBackendPreference,
        Dispatcher userInterfaceDispatcher)
    {
        WorkerConfiguration = workerConfiguration;

        // Request timeouts are enforced per call by each client (Claude 300 s, TTS 60 s,
        // token 15 s), so the HttpClient itself never times out a long streamed reply.
        sharedWorkerHttpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

        UserSettingsStore = new ClickyUserSettingsStore(ClickyUserSettingsStore.GetDefaultSettingsFilePath());
        UserSettings = UserSettingsStore.Load();

        ClaudeChatClient = new ClaudeStreamingChatClient(sharedWorkerHttpClient, workerConfiguration.ChatEndpointUrl, UserSettings.SelectedModelIdentifier);
        ElevenLabsSpeechClient = new ElevenLabsSpeechClient(sharedWorkerHttpClient, workerConfiguration.TextToSpeechEndpointUrl);
        TranscriptionTokenClient = new AssemblyAITemporaryTokenClient(sharedWorkerHttpClient, workerConfiguration.TranscribeTokenEndpointUrl);
        TranscriptionProvider = new AssemblyAIStreamingTranscriptionProvider(TranscriptionTokenClient);

        MicrophoneCaptureService = new MicrophoneCaptureService();
        SpeechPlaybackService = new SpeechPlaybackService();
        ScreenCaptureService = new ScreenCaptureService(screenCaptureBackendPreference);
        KeyboardHookService = new LowLevelKeyboardHookService(UserSettings.PushToTalkShortcutOption, userInterfaceDispatcher);
        DictationManager = new BuddyDictationManager(MicrophoneCaptureService, TranscriptionProvider, userInterfaceDispatcher);

        OverlayState = new CompanionOverlayState();
        OverlayWindowManager = new OverlayWindowManager(OverlayState);

        CompanionManager = new CompanionManager(
            UserSettingsStore,
            UserSettings,
            ClaudeChatClient,
            ElevenLabsSpeechClient,
            DictationManager,
            KeyboardHookService,
            ScreenCaptureService,
            SpeechPlaybackService,
            OverlayWindowManager,
            OverlayState,
            userInterfaceDispatcher);
    }

    public ClickyWorkerConfiguration WorkerConfiguration { get; }
    public ClickyUserSettingsStore UserSettingsStore { get; }
    public ClickyUserSettings UserSettings { get; }
    public ClaudeStreamingChatClient ClaudeChatClient { get; }
    public ElevenLabsSpeechClient ElevenLabsSpeechClient { get; }
    public AssemblyAITemporaryTokenClient TranscriptionTokenClient { get; }
    public AssemblyAIStreamingTranscriptionProvider TranscriptionProvider { get; }
    public MicrophoneCaptureService MicrophoneCaptureService { get; }
    public SpeechPlaybackService SpeechPlaybackService { get; }
    public ScreenCaptureService ScreenCaptureService { get; }
    public LowLevelKeyboardHookService KeyboardHookService { get; }
    public BuddyDictationManager DictationManager { get; }
    public CompanionOverlayState OverlayState { get; }
    public OverlayWindowManager OverlayWindowManager { get; }
    public CompanionManager CompanionManager { get; }

    public void Dispose()
    {
        CompanionManager.Dispose();
        DictationManager.Dispose();
        KeyboardHookService.Dispose();
        SpeechPlaybackService.Dispose();
        MicrophoneCaptureService.Dispose();
        TranscriptionProvider.Dispose();
        sharedWorkerHttpClient.Dispose();
    }
}
