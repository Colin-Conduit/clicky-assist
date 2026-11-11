namespace Clicky.Windows.Capture;

/// <summary>Which screenshot backend <see cref="ScreenCaptureService"/> uses for each monitor.</summary>
public enum ScreenCaptureBackendPreference
{
    /// <summary>Windows.Graphics.Capture first; GDI BitBlt for any monitor where it fails. Used by the app.</summary>
    Automatic,

    /// <summary>Windows.Graphics.Capture only, so the capture test can exercise it in isolation.</summary>
    WindowsGraphicsCaptureOnly,

    /// <summary>GDI BitBlt only, so the capture test can exercise the fallback in isolation.</summary>
    GdiOnly,
}
