using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Clicky.Core.Diagnostics;

namespace Clicky.Windows.Diagnostics;

/// <summary>
/// App-wide log sink: %LOCALAPPDATA%\Clicky\logs\clicky.log, plus the parent console
/// when one exists (the debug flags --ask, --capture-test and --overlay-test are run
/// from a terminal). The app is a GUI process, so it attaches to the parent's console
/// instead of opening its own window.
///
/// Never log secrets: no API keys, no AssemblyAI token, no websocket URL.
/// </summary>
public static class ClickyApplicationLog
{
    private const int AttachParentProcess = -1;
    private const long MaximumLogFileSizeInBytes = 5 * 1024 * 1024;

    private static readonly object LogFileLock = new();
    private static string? logFilePath;
    private static bool isConsoleOutputAvailable;

    public static string? LogFilePath => logFilePath;

    /// <summary>Installs the sink for both the Windows app and Clicky.Core.</summary>
    public static void Initialize(bool attachToParentConsole)
    {
        string logFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clicky", "logs");
        Directory.CreateDirectory(logFolderPath);
        logFilePath = Path.Combine(logFolderPath, "clicky.log");
        RotateLogFileIfTooLarge(logFilePath);

        if (attachToParentConsole)
        {
            // Output redirected to a file or pipe already has a valid handle; otherwise borrow the terminal's console.
            isConsoleOutputAvailable = Console.IsOutputRedirected || AttachConsole(AttachParentProcess);
            if (isConsoleOutputAvailable && !Console.IsOutputRedirected)
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            }
        }

        ClickyDiagnosticLog.InstallLogSink(Write);
    }

    public static void Write(string logCategory, string logMessage)
    {
        string logLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logCategory}] {logMessage}";

        lock (LogFileLock)
        {
            if (logFilePath is not null)
            {
                try
                {
                    File.AppendAllText(logFilePath, logLine + Environment.NewLine);
                }
                catch (IOException)
                {
                    // Another Clicky instance may hold the file briefly; skip this line.
                }
            }

            if (isConsoleOutputAvailable)
            {
                Console.WriteLine(logLine);
            }
        }
    }

    /// <summary>Writes straight to the console only (for streamed reply text in --ask mode).</summary>
    public static void WriteConsoleText(string consoleText)
    {
        if (isConsoleOutputAvailable)
        {
            lock (LogFileLock)
            {
                Console.Write(consoleText);
            }
        }
    }

    private static void RotateLogFileIfTooLarge(string currentLogFilePath)
    {
        try
        {
            FileInfo currentLogFileInfo = new(currentLogFilePath);
            if (currentLogFileInfo.Exists && currentLogFileInfo.Length > MaximumLogFileSizeInBytes)
            {
                File.Move(currentLogFilePath, currentLogFilePath + ".old", overwrite: true);
            }
        }
        catch (IOException)
        {
            // Rotation is best effort.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
