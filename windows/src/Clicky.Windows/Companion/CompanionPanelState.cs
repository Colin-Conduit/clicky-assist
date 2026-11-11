using System.ComponentModel;
using System.Runtime.CompilerServices;
using Clicky.Core.Claude;
using Clicky.Core.Companion;

namespace Clicky.Windows.Companion;

/// <summary>Which explanatory copy the panel shows under its header, mirroring the Mac panel's branches.</summary>
public enum PanelCopyMode
{
    /// <summary>Onboarded and the microphone works: "Hold Ctrl + Alt to talk."</summary>
    ReadyToTalk,

    /// <summary>Microphone works but onboarding has not run: "You're all set. Hit Start to meet Clicky."</summary>
    ReadyToStart,

    /// <summary>Onboarded but the microphone is blocked or missing.</summary>
    MicrophoneNeededAfterOnboarding,

    /// <summary>First launch and the microphone is blocked or missing.</summary>
    Introduction,
}

/// <summary>Color of the status dot next to "Clicky" in the panel header.</summary>
public enum PanelStatusDotKind
{
    /// <summary>Overlay hidden: DS.Colors.textTertiary (#6B736F).</summary>
    Inactive,

    /// <summary>Overlay visible and idle: DS.Colors.success (#34D399).</summary>
    ActiveIdle,

    /// <summary>Listening, processing or responding: DS.Colors.blue400 (#60A5FA).</summary>
    Busy,
}

public enum MicrophoneAvailability
{
    Available,
    BlockedByPrivacySettings,
    NoDeviceFound,
}

/// <summary>
/// What the panel needs to render. The companion manager sets the inputs; the derived
/// properties (copy mode, status text, dot color) are computed here so the panel view
/// only binds and never re-implements the Mac panel's branching.
/// Set and read on the WPF UI thread only.
/// </summary>
public sealed class CompanionPanelState : INotifyPropertyChanged
{
    private CompanionVoiceState voiceState = CompanionVoiceState.Idle;
    private bool isOverlayVisible;
    private bool hasCompletedOnboarding;
    private MicrophoneAvailability microphoneAvailability = MicrophoneAvailability.Available;
    private string selectedModelIdentifier = ClaudeModelIdentifiers.DefaultSonnetModelIdentifier;
    private string pushToTalkShortcutDisplayText = "Ctrl + Alt";

    public event PropertyChangedEventHandler? PropertyChanged;

    public CompanionVoiceState VoiceState
    {
        get => voiceState;
        set => SetInputField(ref voiceState, value);
    }

    public bool IsOverlayVisible
    {
        get => isOverlayVisible;
        set => SetInputField(ref isOverlayVisible, value);
    }

    public bool HasCompletedOnboarding
    {
        get => hasCompletedOnboarding;
        set => SetInputField(ref hasCompletedOnboarding, value);
    }

    public MicrophoneAvailability MicrophoneAvailability
    {
        get => microphoneAvailability;
        set => SetInputField(ref microphoneAvailability, value);
    }

    public string SelectedModelIdentifier
    {
        get => selectedModelIdentifier;
        set => SetInputField(ref selectedModelIdentifier, value);
    }

    /// <summary>For example "Ctrl + Alt".</summary>
    public string PushToTalkShortcutDisplayText
    {
        get => pushToTalkShortcutDisplayText;
        set => SetInputField(ref pushToTalkShortcutDisplayText, value);
    }

    public bool IsMicrophoneAvailable => MicrophoneAvailability == MicrophoneAvailability.Available;

    /// <summary>The Mac's allPermissionsGranted; on Windows only the microphone can be withheld.</summary>
    public bool AreRequiredPermissionsGranted => IsMicrophoneAvailable;

    public bool IsSetupComplete => HasCompletedOnboarding && AreRequiredPermissionsGranted;

    public PanelCopyMode CopyMode => (HasCompletedOnboarding, AreRequiredPermissionsGranted) switch
    {
        (true, true) => PanelCopyMode.ReadyToTalk,
        (false, true) => PanelCopyMode.ReadyToStart,
        (true, false) => PanelCopyMode.MicrophoneNeededAfterOnboarding,
        (false, false) => PanelCopyMode.Introduction,
    };

    /// <summary>"Hold Ctrl + Alt to talk." Mac: "Hold Control+Option to talk."</summary>
    public string ReadyToTalkHintText => $"Hold {PushToTalkShortcutDisplayText} to talk.";

    /// <summary>Header status: Setup, Ready, Active, Listening, Processing or Responding.</summary>
    public string StatusText => CompanionVoiceStateResolver.GetPanelStatusText(IsSetupComplete, IsOverlayVisible, VoiceState);

    public PanelStatusDotKind StatusDotKind
    {
        get
        {
            if (!IsOverlayVisible)
            {
                return PanelStatusDotKind.Inactive;
            }

            return VoiceState == CompanionVoiceState.Idle ? PanelStatusDotKind.ActiveIdle : PanelStatusDotKind.Busy;
        }
    }

    /// <summary>Model picker row is shown once setup is complete, like the Mac.</summary>
    public bool IsModelPickerVisible => IsSetupComplete;

    /// <summary>Microphone row is shown until permissions are granted, like the Mac's PERMISSIONS section.</summary>
    public bool IsPermissionsSectionVisible => !AreRequiredPermissionsGranted;

    public bool IsStartButtonVisible => !HasCompletedOnboarding && AreRequiredPermissionsGranted;

    public bool IsReplayOnboardingVisible => HasCompletedOnboarding;

    private static readonly string[] DerivedPropertyNames =
    [
        nameof(IsMicrophoneAvailable),
        nameof(AreRequiredPermissionsGranted),
        nameof(IsSetupComplete),
        nameof(CopyMode),
        nameof(ReadyToTalkHintText),
        nameof(StatusText),
        nameof(StatusDotKind),
        nameof(IsModelPickerVisible),
        nameof(IsPermissionsSectionVisible),
        nameof(IsStartButtonVisible),
        nameof(IsReplayOnboardingVisible),
    ];

    private void SetInputField<TValue>(ref TValue field, TValue newValue, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<TValue>.Default.Equals(field, newValue))
        {
            return;
        }

        field = newValue;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        foreach (string derivedPropertyName in DerivedPropertyNames)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(derivedPropertyName));
        }
    }
}

/// <summary>What the panel's buttons do. Implemented by the companion manager.</summary>
public interface ICompanionPanelActions
{
    /// <summary>The Start button: runs onboarding (welcome bubble, then the pointing demo).</summary>
    void StartOnboarding();

    /// <summary>"Watch Onboarding Again".</summary>
    void ReplayOnboarding();

    void SelectModel(string modelIdentifier);

    /// <summary>The microphone row's Grant button: opens Settings, Privacy and security, Microphone.</summary>
    void OpenMicrophonePrivacySettings();

    /// <summary>"Quit Clicky".</summary>
    void QuitApplication();
}
