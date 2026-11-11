using Clicky.Windows.Capture;

namespace Clicky.Windows.Debugging;

/// <summary>
/// Command-line switches. With none, Clicky runs normally in the notification area.
/// Each debug mode attaches to the parent terminal's console so its output is visible.
/// </summary>
/// <param name="IsCaptureTestMode">--capture-test: write one JPEG per monitor to %TEMP%\clicky-capture-test and exit.</param>
/// <param name="IsOverlayTestMode">--overlay-test: run the overlay through listening, processing, pointing on every monitor, and responding, then exit.</param>
/// <param name="AskTranscript">--ask "text": run one full turn with typed text instead of speech, then exit.</param>
/// <param name="IsTokenTestMode">--token-test: fetch an AssemblyAI token from the worker, print its length only, and exit.</param>
/// <param name="ShouldShowOverlaysInCaptures">--show-in-captures: skip WDA_EXCLUDEFROMCAPTURE so documentation screenshots can include the overlay.</param>
/// <param name="ShouldOpenPanelAtStartup">--open-panel: open the companion panel right after launch (for screenshots).</param>
/// <param name="CaptureBackendPreference">--capture-backend=auto|wgc|gdi (default auto).</param>
public sealed record ClickyCommandLineOptions(
    bool IsCaptureTestMode,
    bool IsOverlayTestMode,
    string? AskTranscript,
    bool IsTokenTestMode,
    bool ShouldShowOverlaysInCaptures,
    bool ShouldOpenPanelAtStartup,
    ScreenCaptureBackendPreference CaptureBackendPreference)
{
    public bool IsAnyDebugMode => IsCaptureTestMode || IsOverlayTestMode || AskTranscript is not null || IsTokenTestMode;

    public static ClickyCommandLineOptions Parse(IReadOnlyList<string> commandLineArguments)
    {
        bool isCaptureTestMode = false;
        bool isOverlayTestMode = false;
        string? askTranscript = null;
        bool isTokenTestMode = false;
        bool shouldShowOverlaysInCaptures = false;
        bool shouldOpenPanelAtStartup = false;
        ScreenCaptureBackendPreference captureBackendPreference = ScreenCaptureBackendPreference.Automatic;

        for (int argumentIndex = 0; argumentIndex < commandLineArguments.Count; argumentIndex++)
        {
            string commandLineArgument = commandLineArguments[argumentIndex];

            if (commandLineArgument.Equals("--capture-test", StringComparison.OrdinalIgnoreCase))
            {
                isCaptureTestMode = true;
            }
            else if (commandLineArgument.Equals("--overlay-test", StringComparison.OrdinalIgnoreCase))
            {
                isOverlayTestMode = true;
            }
            else if (commandLineArgument.Equals("--token-test", StringComparison.OrdinalIgnoreCase))
            {
                isTokenTestMode = true;
            }
            else if (commandLineArgument.Equals("--show-in-captures", StringComparison.OrdinalIgnoreCase))
            {
                shouldShowOverlaysInCaptures = true;
            }
            else if (commandLineArgument.Equals("--open-panel", StringComparison.OrdinalIgnoreCase))
            {
                shouldOpenPanelAtStartup = true;
            }
            else if (commandLineArgument.StartsWith("--ask=", StringComparison.OrdinalIgnoreCase))
            {
                askTranscript = commandLineArgument["--ask=".Length..];
            }
            else if (commandLineArgument.Equals("--ask", StringComparison.OrdinalIgnoreCase) && argumentIndex + 1 < commandLineArguments.Count)
            {
                argumentIndex++;
                askTranscript = commandLineArguments[argumentIndex];
            }
            else if (commandLineArgument.StartsWith("--capture-backend=", StringComparison.OrdinalIgnoreCase))
            {
                string backendName = commandLineArgument["--capture-backend=".Length..].Trim().ToLowerInvariant();
                captureBackendPreference = backendName switch
                {
                    "wgc" => ScreenCaptureBackendPreference.WindowsGraphicsCaptureOnly,
                    "gdi" => ScreenCaptureBackendPreference.GdiOnly,
                    _ => ScreenCaptureBackendPreference.Automatic,
                };
            }
        }

        if (askTranscript is not null && askTranscript.Trim().Length == 0)
        {
            askTranscript = null;
        }

        return new ClickyCommandLineOptions(
            isCaptureTestMode,
            isOverlayTestMode,
            askTranscript,
            isTokenTestMode,
            shouldShowOverlaysInCaptures,
            shouldOpenPanelAtStartup,
            captureBackendPreference);
    }
}
