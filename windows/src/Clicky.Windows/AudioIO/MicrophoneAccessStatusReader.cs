using System.Diagnostics;
using Clicky.Windows.Companion;
using Clicky.Windows.Diagnostics;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Clicky.Windows.AudioIO;

/// <summary>
/// The Windows stand-in for the Mac's AVCaptureDevice authorization check that drives the panel's
/// microphone row. Windows has no consent prompt for unpackaged desktop apps: access is either
/// allowed by the Settings toggles or silently denied, so this reads the toggles directly.
/// </summary>
public static class MicrophoneAccessStatusReader
{
    private const string LogCategory = "Audio";

    // These keys back the toggles in Settings, Privacy and security, Microphone. They are NOT a
    // documented API and Microsoft could move them in a future release, so the result here is a
    // best-effort status for the panel only. The access-denied error from
    // MicrophoneCaptureService.StartCapture stays the authoritative signal.
    //
    // Commonly reported mapping (not confirmed against Microsoft documentation):
    // HKLM ...\microphone "Value"             : the device-wide microphone access switch.
    // HKCU ...\microphone "Value"             : this user's "Microphone access" / "Let apps access your microphone".
    // HKCU ...\microphone\NonPackaged "Value" : "Let desktop apps access your microphone" (Clicky is a desktop app).
    // Any of them set to "Deny" blocks Clicky, so the exact mapping does not change the result.
    private const string MicrophoneConsentStoreKeyPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string DesktopAppsConsentKeyPath = MicrophoneConsentStoreKeyPath + @"\NonPackaged";
    private const string ConsentValueName = "Value";
    private const string DeniedConsentValue = "Deny";

    private const string MicrophonePrivacySettingsUri = "ms-settings:privacy-microphone";

    /// <summary>
    /// NoDeviceFound when no capture endpoint is active, BlockedByPrivacySettings when any of the
    /// privacy toggles reads "Deny", otherwise Available. Never throws.
    /// </summary>
    public static MicrophoneAvailability ReadCurrentAvailability()
    {
        try
        {
            if (!HasActiveCaptureEndpoint())
            {
                return MicrophoneAvailability.NoDeviceFound;
            }

            if (IsMicrophoneDeniedByPrivacySettings())
            {
                return MicrophoneAvailability.BlockedByPrivacySettings;
            }

            return MicrophoneAvailability.Available;
        }
        catch (Exception unexpectedException)
        {
            // Each check already catches its own failures; this is only a last guard so the panel
            // never crashes over a status line.
            ClickyApplicationLog.Write(LogCategory, $"Reading microphone availability threw {unexpectedException.GetType().Name}: {unexpectedException.Message}");
            return MicrophoneAvailability.Available;
        }
    }

    /// <summary>Opens Settings at Privacy and security, Microphone.</summary>
    public static void OpenMicrophonePrivacySettings()
    {
        try
        {
            // UseShellExecute hands the ms-settings: URI to the shell, which routes it to the
            // Settings app. Without it .NET would try to run the URI as an executable.
            using Process? settingsProcess = Process.Start(new ProcessStartInfo(MicrophonePrivacySettingsUri) { UseShellExecute = true });
        }
        catch (Exception settingsLaunchException)
        {
            ClickyApplicationLog.Write(LogCategory, $"Could not open microphone privacy settings: {settingsLaunchException.GetType().Name}: {settingsLaunchException.Message}");
        }
    }

    private static bool HasActiveCaptureEndpoint()
    {
        try
        {
            using MMDeviceEnumerator deviceEnumerator = new();
            MMDeviceCollection activeCaptureEndpoints = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            return activeCaptureEndpoints.Count > 0;
        }
        catch (Exception deviceEnumerationException)
        {
            // If the audio service cannot be queried, do not tell the user they have no
            // microphone. StartCapture will report the real problem when they try to talk.
            ClickyApplicationLog.Write(LogCategory, $"Could not enumerate microphones: {deviceEnumerationException.GetType().Name}: {deviceEnumerationException.Message}");
            return true;
        }
    }

    private static bool IsMicrophoneDeniedByPrivacySettings()
    {
        return IsConsentValueDenied(RegistryHive.LocalMachine, MicrophoneConsentStoreKeyPath)
            || IsConsentValueDenied(RegistryHive.CurrentUser, MicrophoneConsentStoreKeyPath)
            || IsConsentValueDenied(RegistryHive.CurrentUser, DesktopAppsConsentKeyPath);
    }

    private static bool IsConsentValueDenied(RegistryHive registryHive, string consentKeyPath)
    {
        try
        {
            // Open the 64-bit view explicitly so a 32-bit build would not be redirected to
            // WOW6432Node under HKLM. On 32-bit Windows this quietly falls back to the only view.
            using RegistryKey registryBaseKey = RegistryKey.OpenBaseKey(registryHive, RegistryView.Registry64);
            using RegistryKey? consentKey = registryBaseKey.OpenSubKey(consentKeyPath, writable: false);

            // A missing key or value means the toggle was never changed, which is "Allow".
            string? consentValue = consentKey?.GetValue(ConsentValueName) as string;
            return string.Equals(consentValue, DeniedConsentValue, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception registryReadException)
        {
            ClickyApplicationLog.Write(LogCategory, $"Could not read microphone privacy setting {registryHive}: {registryReadException.GetType().Name}: {registryReadException.Message}");
            return false;
        }
    }
}
