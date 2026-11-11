using System.ComponentModel;
using System.Runtime.CompilerServices;
using Clicky.Core.Companion;
using Clicky.Core.Pointing;

namespace Clicky.Windows.Companion;

/// <summary>
/// Where the blue cursor should fly and what it should say when it lands.
/// </summary>
/// <param name="TargetLocationInPhysicalPixels">Element location in virtual-screen physical pixels.</param>
/// <param name="TargetMonitorBoundsInPhysicalPixels">Bounds of the monitor the element is on; only that monitor's overlay animates.</param>
/// <param name="CustomBubbleText">Text for the pointing bubble (the onboarding demo comment), or null to use a random pointer phrase.</param>
public sealed record OverlayPointingTarget(
    PhysicalPixelPoint TargetLocationInPhysicalPixels,
    PhysicalPixelRectangle TargetMonitorBoundsInPhysicalPixels,
    string? CustomBubbleText);

/// <summary>
/// Everything the cursor overlays render, shared by every monitor's overlay window.
/// The Windows counterpart of the @Published properties BlueCursorView observes on
/// CompanionManager. Set and read on the WPF UI thread only.
/// </summary>
public sealed class CompanionOverlayState : INotifyPropertyChanged
{
    private CompanionVoiceState voiceState = CompanionVoiceState.Idle;
    private double currentAudioPowerLevel;
    private OverlayPointingTarget? detectedElementPointingTarget;
    private string responseBubbleText = string.Empty;
    private bool isResponseBubbleVisible;
    private string onboardingPromptText = string.Empty;
    private bool isOnboardingPromptVisible;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised by an overlay once it has flown back to the cursor after pointing.</summary>
    public event Action? PointingAnimationFinished;

    /// <summary>Idle and Responding show the triangle, Listening the waveform, Processing the spinner.</summary>
    public CompanionVoiceState VoiceState
    {
        get => voiceState;
        set => SetField(ref voiceState, value);
    }

    /// <summary>0..1 microphone level for the waveform bars.</summary>
    public double CurrentAudioPowerLevel
    {
        get => currentAudioPowerLevel;
        set => SetField(ref currentAudioPowerLevel, value);
    }

    /// <summary>
    /// Non-null while the buddy should fly to, point at, and return from an element.
    /// The overlay on the matching monitor clears it through <see cref="NotifyPointingAnimationFinished"/>.
    /// While it is set, overlays on other monitors hide their buddy so only one is visible.
    /// </summary>
    public OverlayPointingTarget? DetectedElementPointingTarget
    {
        get => detectedElementPointingTarget;
        set => SetField(ref detectedElementPointingTarget, value);
    }

    /// <summary>Spoken reply text (POINT tag removed), shown in a bubble beside the cursor while it is visible.</summary>
    public string ResponseBubbleText
    {
        get => responseBubbleText;
        set => SetField(ref responseBubbleText, value);
    }

    public bool IsResponseBubbleVisible
    {
        get => isResponseBubbleVisible;
        set => SetField(ref isResponseBubbleVisible, value);
    }

    /// <summary>Onboarding hint ("press ctrl + alt and introduce yourself"), streamed one character at a time by the companion.</summary>
    public string OnboardingPromptText
    {
        get => onboardingPromptText;
        set => SetField(ref onboardingPromptText, value);
    }

    /// <summary>Overlays fade the onboarding prompt bubble in when true and out when false.</summary>
    public bool IsOnboardingPromptVisible
    {
        get => isOnboardingPromptVisible;
        set => SetField(ref isOnboardingPromptVisible, value);
    }

    /// <summary>Called by the overlay that handled the pointing target, after the return flight (or its cancellation).</summary>
    public void NotifyPointingAnimationFinished()
    {
        DetectedElementPointingTarget = null;
        PointingAnimationFinished?.Invoke();
    }

    private void SetField<TValue>(ref TValue field, TValue newValue, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<TValue>.Default.Equals(field, newValue))
        {
            return;
        }

        field = newValue;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
