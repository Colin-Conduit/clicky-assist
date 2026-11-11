using Clicky.Windows.Companion;

namespace Clicky.Windows.AudioIO;

/// <summary>
/// Thrown by <see cref="MicrophoneCaptureService.StartCapture"/> when Windows cannot give the app a
/// microphone. <see cref="MicrophoneAvailability"/> tells the panel which fix to offer: open the
/// privacy settings page, or ask the user to connect a microphone.
/// </summary>
public sealed class MicrophoneUnavailableException : Exception
{
    public MicrophoneUnavailableException(string message, MicrophoneAvailability microphoneAvailability, Exception? innerException = null)
        : base(message, innerException)
    {
        MicrophoneAvailability = microphoneAvailability;
    }

    public MicrophoneAvailability MicrophoneAvailability { get; }
}
