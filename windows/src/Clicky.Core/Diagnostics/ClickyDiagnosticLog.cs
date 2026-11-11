namespace Clicky.Core.Diagnostics;

/// <summary>
/// Minimal logging hook so Core classes can report what they are doing without
/// depending on any logging framework. The Windows app installs a sink that writes
/// to a log file and, for the debug command-line modes, to the console.
///
/// Never pass secrets here: no API keys, no AssemblyAI tokens, and no websocket
/// URL (it carries the token). Log lengths instead of values.
/// </summary>
public static class ClickyDiagnosticLog
{
    private static Action<string, string>? installedLogSink;

    /// <summary>Installs the sink that receives (category, message) pairs. Pass null to silence logging.</summary>
    public static void InstallLogSink(Action<string, string>? logSink)
    {
        installedLogSink = logSink;
    }

    public static void Write(string logCategory, string logMessage)
    {
        try
        {
            installedLogSink?.Invoke(logCategory, logMessage);
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
