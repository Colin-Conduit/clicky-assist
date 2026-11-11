namespace Clicky.Core.Companion;

/// <summary>
/// What the companion is doing right now. Drives the panel status text and which
/// cursor shape the overlay shows (triangle, waveform, or spinner).
/// </summary>
public enum CompanionVoiceState
{
    /// <summary>Waiting for push-to-talk. Overlay shows the blue triangle.</summary>
    Idle,

    /// <summary>Push-to-talk held, microphone streaming. Overlay shows the waveform.</summary>
    Listening,

    /// <summary>Finalizing the transcript, capturing screens, waiting for Claude and TTS. Overlay shows the spinner.</summary>
    Processing,

    /// <summary>Reply audio is playing. Overlay shows the triangle again.</summary>
    Responding,
}

/// <summary>
/// Pure port of CompanionManager.bindVoiceStateObservation: derives the voice state
/// from the dictation manager's flags. The AI response pipeline owns Responding, so
/// dictation flag changes never override it.
/// </summary>
public static class CompanionVoiceStateResolver
{
    public static CompanionVoiceState ResolveFromDictationFlags(
        CompanionVoiceState currentVoiceState,
        bool isRecordingFromKeyboardShortcut,
        bool isFinalizingTranscript,
        bool isPreparingToRecord)
    {
        if (currentVoiceState == CompanionVoiceState.Responding)
        {
            return CompanionVoiceState.Responding;
        }

        if (isFinalizingTranscript)
        {
            return CompanionVoiceState.Processing;
        }

        if (isRecordingFromKeyboardShortcut)
        {
            return CompanionVoiceState.Listening;
        }

        if (isPreparingToRecord)
        {
            return CompanionVoiceState.Processing;
        }

        return CompanionVoiceState.Idle;
    }

    /// <summary>Panel header status text, same words as the Mac panel.</summary>
    public static string GetPanelStatusText(bool isSetupComplete, bool isOverlayVisible, CompanionVoiceState voiceState)
    {
        if (!isSetupComplete)
        {
            return "Setup";
        }

        if (!isOverlayVisible)
        {
            return "Ready";
        }

        return voiceState switch
        {
            CompanionVoiceState.Idle => "Active",
            CompanionVoiceState.Listening => "Listening",
            CompanionVoiceState.Processing => "Processing",
            CompanionVoiceState.Responding => "Responding",
            _ => "Active",
        };
    }
}
