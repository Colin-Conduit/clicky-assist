using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Clicky.Core.Pointing;
using Clicky.Core.Screen;
using Clicky.Windows.Diagnostics;
using Clicky.Windows.Display;

namespace Clicky.Windows.Capture;

/// <summary>
/// Windows port of CompanionScreenCaptureUtility.captureAllScreensAsJPEG: screenshots every
/// monitor, cursor monitor first, each scaled so its longest edge is 1280 pixels and
/// encoded as JPEG at quality 80, ready to send to Claude.
///
/// Each monitor is captured with Windows.Graphics.Capture when possible and with GDI
/// BitBlt when not (see <see cref="ScreenCaptureBackendPreference"/>). A monitor is only
/// left out when every allowed backend fails for it.
/// </summary>
public sealed class ScreenCaptureService
{
    private const string LogCategory = "Capture";

    /// <summary>Win32 device names look like \\.\DISPLAY1; the prefix adds nothing to a log line.</summary>
    private const string Win32DeviceNamePrefix = @"\\.\";

    private const int MaximumExceptionDescriptionLength = 300;

    private readonly ScreenCaptureBackendPreference backendPreference;
    private readonly WindowsGraphicsCaptureMonitorScreenshotCapturer windowsGraphicsCaptureCapturer = new();
    private readonly GdiMonitorScreenshotCapturer gdiCapturer = new();

    /// <summary>
    /// Captures run one at a time: they share one Direct3D device, and the backend
    /// descriptions must describe a single capture rather than a mix of two.
    /// </summary>
    private readonly SemaphoreSlim oneCaptureAtATimeLock = new(1, 1);

    private IReadOnlyList<string> lastCaptureBackendDescriptions = [];

    public ScreenCaptureService(ScreenCaptureBackendPreference backendPreference = ScreenCaptureBackendPreference.Automatic)
    {
        this.backendPreference = backendPreference;
    }

    /// <summary>
    /// One line per monitor from the most recent capture, for example
    /// "DISPLAY1 (cursor): Windows.Graphics.Capture 2560x1440 -> 1280x720" or
    /// "DISPLAY2: GDI BitBlt 1920x1080 -> 1280x720 (fallback: reason)".
    /// Replaced as a whole after each capture, so readers never see a half-written list.
    /// </summary>
    public IReadOnlyList<string> LastCaptureBackendDescriptions => Volatile.Read(ref lastCaptureBackendDescriptions);

    /// <summary>
    /// Captures every monitor, cursor monitor first. Throws <see cref="InvalidOperationException"/>
    /// when no monitor could be captured, and <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public Task<IReadOnlyList<CapturedScreen>> CaptureAllScreensAsJpegAsync(CancellationToken cancellationToken)
    {
        // Callers are usually on the WPF UI thread. Pixel copies and JPEG encoding of several
        // monitors take long enough to stall the overlay animation, so all of it runs on the
        // thread pool.
        return Task.Run(() => CaptureAllScreensOneAtATimeAsync(cancellationToken), cancellationToken);
    }

    /// <summary>Single-line "ExceptionType: message" for logs and backend descriptions.</summary>
    internal static string DescribeExceptionOnOneLine(Exception exception)
    {
        string singleLineMessage = Regex.Replace(exception.Message, @"\s+", " ").Trim();
        string exceptionDescription = $"{exception.GetType().Name}: {singleLineMessage}";

        // COM and Win32 failures are easiest to look up by their HRESULT.
        if (exception is ExternalException && !singleLineMessage.Contains("0x", StringComparison.OrdinalIgnoreCase))
        {
            exceptionDescription += $" (0x{exception.HResult:X8})";
        }

        return exceptionDescription.Length <= MaximumExceptionDescriptionLength
            ? exceptionDescription
            : exceptionDescription[..MaximumExceptionDescriptionLength] + "...";
    }

    private async Task<IReadOnlyList<CapturedScreen>> CaptureAllScreensOneAtATimeAsync(CancellationToken cancellationToken)
    {
        await oneCaptureAtATimeLock.WaitAsync(cancellationToken);
        try
        {
            return await CaptureAllScreensAsync(cancellationToken);
        }
        finally
        {
            oneCaptureAtATimeLock.Release();
        }
    }

    private async Task<IReadOnlyList<CapturedScreen>> CaptureAllScreensAsync(CancellationToken cancellationToken)
    {
        Stopwatch captureStopwatch = Stopwatch.StartNew();

        IReadOnlyList<DisplayMonitorInfo> monitorsInEnumerationOrder = DisplayMonitorEnumerator.EnumerateMonitors();
        if (monitorsInEnumerationOrder.Count == 0)
        {
            const string noMonitorsDescription = "No monitors were found to capture.";
            PublishBackendDescriptions([noMonitorsDescription]);
            ClickyApplicationLog.Write(LogCategory, noMonitorsDescription);
            throw new InvalidOperationException(noMonitorsDescription);
        }

        PhysicalPixelPoint cursorPositionInPhysicalPixels = CursorPositionReader.GetCursorPositionInPhysicalPixels();
        DisplayMonitorInfo? cursorMonitor = DisplayMonitorEnumerator.FindMonitorContainingPoint(monitorsInEnumerationOrder, cursorPositionInPhysicalPixels);
        IReadOnlyList<DisplayMonitorInfo> monitorsInPromptOrder = ScreenCaptureLabeling.OrderCursorScreenFirst(
            monitorsInEnumerationOrder,
            monitor => monitor == cursorMonitor);

        List<EncodedMonitorScreenshot> encodedScreenshotsInPromptOrder = [];
        List<string> backendDescriptions = [];

        foreach (DisplayMonitorInfo monitor in monitorsInPromptOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool isCursorScreen = monitor == cursorMonitor;
            string monitorDescriptionPrefix = BuildMonitorDescriptionPrefix(monitor, isCursorScreen);
            ScreenshotDimensions screenshotDimensions = ScreenCoordinateMapper.ComputeScreenshotDimensions(
                monitor.BoundsInPhysicalPixels.Width,
                monitor.BoundsInPhysicalPixels.Height);

            MonitorPixelCaptureOutcome captureOutcome = await CaptureMonitorPixelsWithPreferredBackendAsync(monitor, cancellationToken);
            if (captureOutcome.CapturedPixels is not CapturedMonitorPixels capturedPixels)
            {
                RecordBackendDescription(backendDescriptions, $"{monitorDescriptionPrefix}: skipped ({captureOutcome.FailureDescription})");
                continue;
            }

            byte[] jpegImageBytes;
            try
            {
                jpegImageBytes = ScreenshotJpegEncoder.EncodeScaledJpeg(capturedPixels, screenshotDimensions);
            }
            catch (Exception exception)
            {
                // The encoder is shared by both backends, so trying the other backend would fail the same way.
                RecordBackendDescription(backendDescriptions, $"{monitorDescriptionPrefix}: skipped (JPEG encoding failed: {DescribeExceptionOnOneLine(exception)})");
                continue;
            }

            RecordBackendDescription(
                backendDescriptions,
                BuildSuccessfulCaptureDescription(monitorDescriptionPrefix, captureOutcome, capturedPixels, screenshotDimensions));
            encodedScreenshotsInPromptOrder.Add(new EncodedMonitorScreenshot(monitor, isCursorScreen, jpegImageBytes, screenshotDimensions));
        }

        PublishBackendDescriptions(backendDescriptions);

        if (encodedScreenshotsInPromptOrder.Count == 0)
        {
            throw new InvalidOperationException($"No screen could be captured. {string.Join(" | ", backendDescriptions)}");
        }

        List<CapturedScreen> capturedScreens = BuildLabeledCapturedScreens(encodedScreenshotsInPromptOrder);
        ClickyApplicationLog.Write(
            LogCategory,
            $"Captured {capturedScreens.Count} of {monitorsInPromptOrder.Count} screens in {captureStopwatch.ElapsedMilliseconds} ms.");
        return capturedScreens;
    }

    private async Task<MonitorPixelCaptureOutcome> CaptureMonitorPixelsWithPreferredBackendAsync(DisplayMonitorInfo monitor, CancellationToken cancellationToken)
    {
        if (backendPreference == ScreenCaptureBackendPreference.GdiOnly)
        {
            return CaptureMonitorPixelsWithGdi(monitor, windowsGraphicsCaptureFailureReason: null);
        }

        try
        {
            CapturedMonitorPixels capturedPixels = await windowsGraphicsCaptureCapturer.CaptureMonitorAsync(monitor, cancellationToken);
            return MonitorPixelCaptureOutcome.Succeeded(
                capturedPixels,
                WindowsGraphicsCaptureMonitorScreenshotCapturer.BackendDisplayName,
                windowsGraphicsCaptureFailureReason: null);
        }
        catch (Exception exception) when (!WasCancelledByCaller(exception, cancellationToken))
        {
            string windowsGraphicsCaptureFailureReason = DescribeExceptionOnOneLine(exception);

            if (backendPreference == ScreenCaptureBackendPreference.WindowsGraphicsCaptureOnly)
            {
                return MonitorPixelCaptureOutcome.Failed(
                    $"{WindowsGraphicsCaptureMonitorScreenshotCapturer.BackendDisplayName} failed: {windowsGraphicsCaptureFailureReason}");
            }

            return CaptureMonitorPixelsWithGdi(monitor, windowsGraphicsCaptureFailureReason);
        }
    }

    /// <summary>
    /// <paramref name="windowsGraphicsCaptureFailureReason"/> is null when GDI was chosen
    /// directly, and the Windows.Graphics.Capture failure when GDI is the fallback.
    /// </summary>
    private MonitorPixelCaptureOutcome CaptureMonitorPixelsWithGdi(DisplayMonitorInfo monitor, string? windowsGraphicsCaptureFailureReason)
    {
        try
        {
            CapturedMonitorPixels capturedPixels = gdiCapturer.CaptureMonitor(monitor);
            return MonitorPixelCaptureOutcome.Succeeded(capturedPixels, GdiMonitorScreenshotCapturer.BackendDisplayName, windowsGraphicsCaptureFailureReason);
        }
        catch (Exception exception)
        {
            string gdiFailureReason = DescribeExceptionOnOneLine(exception);
            string failureDescription = windowsGraphicsCaptureFailureReason is null
                ? $"{GdiMonitorScreenshotCapturer.BackendDisplayName} failed: {gdiFailureReason}"
                : $"{WindowsGraphicsCaptureMonitorScreenshotCapturer.BackendDisplayName} failed: {windowsGraphicsCaptureFailureReason}; "
                    + $"{GdiMonitorScreenshotCapturer.BackendDisplayName} failed: {gdiFailureReason}";
            return MonitorPixelCaptureOutcome.Failed(failureDescription);
        }
    }

    /// <summary>
    /// Labels are numbered by position in the returned list, not by position among all
    /// monitors. PointTargetResolver maps Claude's ":screenN" to list position N, so if a
    /// monitor had been skipped, numbering from the full monitor list would send the cursor
    /// to the wrong screen. When nothing is skipped the two numberings are identical.
    /// </summary>
    private static List<CapturedScreen> BuildLabeledCapturedScreens(IReadOnlyList<EncodedMonitorScreenshot> encodedScreenshotsInPromptOrder)
    {
        List<CapturedScreen> capturedScreens = [];
        for (int screenIndex = 0; screenIndex < encodedScreenshotsInPromptOrder.Count; screenIndex++)
        {
            EncodedMonitorScreenshot encodedScreenshot = encodedScreenshotsInPromptOrder[screenIndex];
            string screenLabel = ScreenCaptureLabeling.BuildScreenLabel(screenIndex, encodedScreenshotsInPromptOrder.Count, encodedScreenshot.IsCursorScreen);

            capturedScreens.Add(new CapturedScreen(
                encodedScreenshot.JpegImageBytes,
                screenLabel,
                encodedScreenshot.IsCursorScreen,
                encodedScreenshot.Monitor.DeviceName,
                encodedScreenshot.Monitor.BoundsInPhysicalPixels,
                encodedScreenshot.Monitor.DpiScale,
                encodedScreenshot.ScreenshotDimensions));
        }

        return capturedScreens;
    }

    private static string BuildMonitorDescriptionPrefix(DisplayMonitorInfo monitor, bool isCursorScreen)
    {
        string shortDeviceName = monitor.DeviceName.StartsWith(Win32DeviceNamePrefix, StringComparison.Ordinal)
            ? monitor.DeviceName[Win32DeviceNamePrefix.Length..]
            : monitor.DeviceName;

        return isCursorScreen ? $"{shortDeviceName} (cursor)" : shortDeviceName;
    }

    private static string BuildSuccessfulCaptureDescription(
        string monitorDescriptionPrefix,
        MonitorPixelCaptureOutcome captureOutcome,
        CapturedMonitorPixels capturedPixels,
        ScreenshotDimensions screenshotDimensions)
    {
        string captureDescription =
            $"{monitorDescriptionPrefix}: {captureOutcome.BackendDisplayName} "
            + $"{capturedPixels.WidthInPixels}x{capturedPixels.HeightInPixels} -> "
            + $"{screenshotDimensions.WidthInPixels}x{screenshotDimensions.HeightInPixels}";

        return captureOutcome.WindowsGraphicsCaptureFailureReason is null
            ? captureDescription
            : $"{captureDescription} (fallback: {captureOutcome.WindowsGraphicsCaptureFailureReason})";
    }

    /// <summary>Adds the line and logs it right away, so a fallback reason shows up in the log when it happens.</summary>
    private static void RecordBackendDescription(List<string> backendDescriptions, string backendDescription)
    {
        backendDescriptions.Add(backendDescription);
        ClickyApplicationLog.Write(LogCategory, backendDescription);
    }

    private void PublishBackendDescriptions(IReadOnlyList<string> backendDescriptions)
    {
        Volatile.Write(ref lastCaptureBackendDescriptions, backendDescriptions.ToArray());
    }

    /// <summary>True only for cancellation the caller asked for, which must propagate instead of triggering the GDI fallback.</summary>
    private static bool WasCancelledByCaller(Exception exception, CancellationToken cancellationToken)
    {
        return exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
    }

    /// <summary>
    /// Result of trying the allowed backends on one monitor. A non-null
    /// <see cref="WindowsGraphicsCaptureFailureReason"/> on a success means GDI was the fallback.
    /// </summary>
    private sealed record MonitorPixelCaptureOutcome(
        CapturedMonitorPixels? CapturedPixels,
        string? BackendDisplayName,
        string? WindowsGraphicsCaptureFailureReason,
        string? FailureDescription)
    {
        public static MonitorPixelCaptureOutcome Succeeded(
            CapturedMonitorPixels capturedPixels,
            string backendDisplayName,
            string? windowsGraphicsCaptureFailureReason)
        {
            return new MonitorPixelCaptureOutcome(capturedPixels, backendDisplayName, windowsGraphicsCaptureFailureReason, FailureDescription: null);
        }

        public static MonitorPixelCaptureOutcome Failed(string failureDescription)
        {
            return new MonitorPixelCaptureOutcome(
                CapturedPixels: null,
                BackendDisplayName: null,
                WindowsGraphicsCaptureFailureReason: null,
                failureDescription);
        }
    }

    /// <summary>One monitor's finished JPEG, waiting for its label once the final screen count is known.</summary>
    private sealed record EncodedMonitorScreenshot(
        DisplayMonitorInfo Monitor,
        bool IsCursorScreen,
        byte[] JpegImageBytes,
        ScreenshotDimensions ScreenshotDimensions);
}
