using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Clicky.Core.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace Clicky.Windows.Diagnostics;

/// <summary>
/// App-wide log sink: %LOCALAPPDATA%\Clicky\logs\clicky.log, plus the launching terminal for
/// the debug flags (--ask, --capture-test, --overlay-test, --token-test).
///
/// The app is a GUI-subsystem process, so it has no console of its own. When output is
/// redirected (a pipe or file from a script), the inherited handle is used directly. When it
/// was started from an interactive terminal, there is no usable handle, so it attaches to the
/// parent's console and writes to CONOUT$.
///
/// Never log secrets: no API keys, no AssemblyAI token, no websocket URL.
/// </summary>
public static class ClickyApplicationLog
{
    private const int AttachParentProcess = -1;
    private const int StandardOutputHandleIdentifier = -11;
    private const uint FileTypeUnknown = 0x0000;
    private const uint GenericWriteAccess = 0x40000000;
    private const uint FileShareReadAndWrite = 0x00000001 | 0x00000002;
    private const uint OpenExistingFile = 3;
    private const long MaximumLogFileSizeInBytes = 5 * 1024 * 1024;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private static readonly object LogFileLock = new();
    private static string? logFilePath;
    private static TextWriter? consoleOutputWriter;

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
            consoleOutputWriter = TryCreateConsoleOutputWriter();
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

            consoleOutputWriter?.WriteLine(logLine);
        }
    }

    /// <summary>Writes straight to the console only (for streamed reply text in --ask mode).</summary>
    public static void WriteConsoleText(string consoleText)
    {
        lock (LogFileLock)
        {
            consoleOutputWriter?.Write(consoleText);
        }
    }

    /// <summary>
    /// Decides from the raw standard-output handle rather than Console.IsOutputRedirected: for a
    /// GUI process with no handle at all, that property reports "redirected" and caches the answer,
    /// which would skip attaching to the terminal.
    /// </summary>
    private static TextWriter? TryCreateConsoleOutputWriter()
    {
        UTF8Encoding utf8WithoutByteOrderMark = new(encoderShouldEmitUTF8Identifier: false);

        IntPtr standardOutputHandle = GetStdHandle(StandardOutputHandleIdentifier);
        bool hasUsableInheritedStandardOutput = standardOutputHandle != IntPtr.Zero
            && standardOutputHandle != InvalidHandleValue
            && GetFileType(standardOutputHandle) != FileTypeUnknown;

        if (hasUsableInheritedStandardOutput)
        {
            return new StreamWriter(Console.OpenStandardOutput(), utf8WithoutByteOrderMark) { AutoFlush = true };
        }

        if (!AttachConsole(AttachParentProcess))
        {
            // Started from Explorer or a shortcut: no terminal to write to, the log file still works.
            return null;
        }

        SafeFileHandle consoleOutputHandle = CreateFile("CONOUT$", GenericWriteAccess, FileShareReadAndWrite, IntPtr.Zero, OpenExistingFile, 0, IntPtr.Zero);
        if (consoleOutputHandle.IsInvalid)
        {
            consoleOutputHandle.Dispose();
            return null;
        }

        // A blank line first, because the terminal already printed its next prompt when the GUI process detached from it.
        StreamWriter attachedConsoleWriter = new(new FileStream(consoleOutputHandle, FileAccess.Write), utf8WithoutByteOrderMark) { AutoFlush = true };
        attachedConsoleWriter.WriteLine();
        return attachedConsoleWriter;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandleIdentifier);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr fileHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFileHandle);
}
