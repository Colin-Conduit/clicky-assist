using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clicky.Core.Pointing;
using Clicky.Core.Screen;
using Clicky.Windows.Capture;
using Clicky.Windows.Companion;
using Clicky.Windows.CursorOverlay;
using Clicky.Windows.Diagnostics;
using Clicky.Windows.Display;

namespace Clicky.Windows.Debugging;

/// <summary>
/// --capture-test: proves screen capture works on every monitor and that the overlay never
/// shows up in what Claude receives.
///
/// It opens the real overlay windows with a solid magenta test square on each monitor, then
/// captures every screen with the chosen backend (and, in automatic mode, with GDI and
/// Windows.Graphics.Capture separately), writes the JPEGs to %TEMP%\clicky-capture-test,
/// and checks the pixels where the square sits. With capture exclusion on, the square must be
/// absent from every image. With --show-in-captures, exclusion is off and the square must be
/// present, which proves the check itself can see it.
/// </summary>
public static class CaptureTestRunner
{
    private const string LogCategory = "CaptureTest";
    private const string OutputFolderName = "clicky-capture-test";

    // Must match the overlay contract: a 200x200 DIP square at 100,100 DIPs from the overlay's top-left.
    private const double MarkerLeftInDips = 100;
    private const double MarkerTopInDips = 100;
    private const double MarkerSizeInDips = 200;
    private static readonly Color CaptureTestMarkerColor = Color.FromRgb(255, 0, 255);

    private static readonly TimeSpan OverlaySettleDelay = TimeSpan.FromMilliseconds(900);

    public static async Task<int> RunAsync(ClickyCommandLineOptions commandLineOptions)
    {
        string outputFolderPath = Path.Combine(Path.GetTempPath(), OutputFolderName);
        Directory.CreateDirectory(outputFolderPath);
        foreach (string previousOutputFilePath in Directory.GetFiles(outputFolderPath, "*.jpg", SearchOption.AllDirectories))
        {
            File.Delete(previousOutputFilePath);
        }

        bool isCaptureExclusionEnabled = !commandLineOptions.ShouldShowOverlaysInCaptures;
        IReadOnlyList<DisplayMonitorInfo> connectedMonitors = DisplayMonitorEnumerator.EnumerateMonitors();
        StringBuilder reportBuilder = new();
        reportBuilder.AppendLine($"Clicky capture test, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        reportBuilder.AppendLine($"Capture exclusion enabled: {isCaptureExclusionEnabled}");
        foreach (DisplayMonitorInfo connectedMonitor in connectedMonitors)
        {
            reportBuilder.AppendLine($"Monitor {connectedMonitor.DeviceName}: bounds {connectedMonitor.BoundsInPhysicalPixels}, scale {connectedMonitor.DpiScale:P0}, primary {connectedMonitor.IsPrimary}");
        }

        CompanionOverlayState overlayState = new();
        OverlayWindowManager overlayWindowManager = new(overlayState)
        {
            ExcludeOverlaysFromScreenCapture = isCaptureExclusionEnabled,
            CaptureTestMarkerColor = CaptureTestMarkerColor,
        };
        overlayWindowManager.ShowOverlays(connectedMonitors, isFirstAppearance: false);
        await Task.Delay(OverlaySettleDelay);

        foreach (string exclusionStatusLine in overlayWindowManager.DescribeCaptureExclusionStatus())
        {
            reportBuilder.AppendLine($"Overlay window: {exclusionStatusLine}");
        }

        List<(string FolderLabel, ScreenCaptureBackendPreference BackendPreference)> captureRuns =
        [
            (string.Empty, commandLineOptions.CaptureBackendPreference),
        ];
        if (commandLineOptions.CaptureBackendPreference == ScreenCaptureBackendPreference.Automatic)
        {
            captureRuns.Add(("gdi", ScreenCaptureBackendPreference.GdiOnly));
            captureRuns.Add(("wgc", ScreenCaptureBackendPreference.WindowsGraphicsCaptureOnly));
        }

        bool didEveryCheckPass = true;
        int primaryRunScreenCount = 0;

        foreach ((string folderLabel, ScreenCaptureBackendPreference backendPreference) in captureRuns)
        {
            string runOutputFolderPath = folderLabel.Length == 0 ? outputFolderPath : Path.Combine(outputFolderPath, folderLabel);
            Directory.CreateDirectory(runOutputFolderPath);
            reportBuilder.AppendLine();
            reportBuilder.AppendLine($"Run: backend {backendPreference}, folder {runOutputFolderPath}");

            ScreenCaptureService screenCaptureService = new(backendPreference);
            IReadOnlyList<CapturedScreen> capturedScreens;
            try
            {
                capturedScreens = await screenCaptureService.CaptureAllScreensAsJpegAsync(CancellationToken.None);
            }
            catch (Exception captureException)
            {
                reportBuilder.AppendLine($"  FAILED: {captureException.GetType().Name}: {captureException.Message}");
                // A forced single backend failing is informative but only the main run must succeed.
                if (folderLabel.Length == 0)
                {
                    didEveryCheckPass = false;
                }

                continue;
            }

            foreach (string backendDescription in screenCaptureService.LastCaptureBackendDescriptions)
            {
                reportBuilder.AppendLine($"  {backendDescription}");
            }

            if (folderLabel.Length == 0)
            {
                primaryRunScreenCount = capturedScreens.Count;
                if (capturedScreens.Count != connectedMonitors.Count)
                {
                    reportBuilder.AppendLine($"  FAILED: captured {capturedScreens.Count} screens for {connectedMonitors.Count} monitors.");
                    didEveryCheckPass = false;
                }
            }

            for (int screenIndex = 0; screenIndex < capturedScreens.Count; screenIndex++)
            {
                CapturedScreen capturedScreen = capturedScreens[screenIndex];
                string screenFileName = $"screen{screenIndex + 1}.jpg";
                string screenFilePath = Path.Combine(runOutputFolderPath, screenFileName);
                await File.WriteAllBytesAsync(screenFilePath, capturedScreen.JpegImageBytes);

                bool isMarkerVisible = IsCaptureTestMarkerVisible(capturedScreen);
                bool didMarkerCheckPass = isCaptureExclusionEnabled ? !isMarkerVisible : isMarkerVisible;
                didEveryCheckPass &= didMarkerCheckPass;

                reportBuilder.AppendLine(
                    $"  {screenFileName}: {capturedScreen.MonitorDeviceName}, label \"{capturedScreen.LabelWithImageDimensions}\", " +
                    $"{capturedScreen.JpegImageBytes.Length / 1024} KB, overlay marker {(isMarkerVisible ? "VISIBLE" : "not visible")} -> {(didMarkerCheckPass ? "PASS" : "FAIL")}");
            }
        }

        overlayWindowManager.HideOverlays();

        reportBuilder.AppendLine();
        reportBuilder.AppendLine($"Result: {(didEveryCheckPass ? "PASS" : "FAIL")} ({primaryRunScreenCount} screen file(s) in {outputFolderPath})");

        string reportText = reportBuilder.ToString();
        await File.WriteAllTextAsync(Path.Combine(outputFolderPath, "report.txt"), reportText);
        foreach (string reportLine in reportText.Split(Environment.NewLine))
        {
            ClickyApplicationLog.Write(LogCategory, reportLine);
        }

        return didEveryCheckPass ? 0 : 1;
    }

    /// <summary>
    /// Samples a 5x5 grid inside the middle of where the marker square would be in this screenshot
    /// and reports whether most samples are close to magenta. JPEG blurs edges, so only the
    /// inner part of the square is sampled.
    /// </summary>
    private static bool IsCaptureTestMarkerVisible(CapturedScreen capturedScreen)
    {
        BitmapSource decodedScreenshot = DecodeJpegToBgra(capturedScreen.JpegImageBytes);
        int screenshotWidth = decodedScreenshot.PixelWidth;
        int screenshotHeight = decodedScreenshot.PixelHeight;
        int stride = screenshotWidth * 4;
        byte[] screenshotPixels = new byte[stride * screenshotHeight];
        decodedScreenshot.CopyPixels(screenshotPixels, stride, 0);

        PhysicalPixelRectangle monitorBounds = capturedScreen.MonitorBoundsInPhysicalPixels;
        double physicalToScreenshotScaleX = (double)screenshotWidth / monitorBounds.Width;
        double physicalToScreenshotScaleY = (double)screenshotHeight / monitorBounds.Height;

        int markerSampleCount = 0;
        int magentaSampleCount = 0;

        for (int sampleRow = 0; sampleRow < 5; sampleRow++)
        {
            for (int sampleColumn = 0; sampleColumn < 5; sampleColumn++)
            {
                // Inner 50% of the square: offsets from 25% to 75% of its size.
                double sampleOffsetXInDips = MarkerSizeInDips * (0.25 + (0.125 * sampleColumn));
                double sampleOffsetYInDips = MarkerSizeInDips * (0.25 + (0.125 * sampleRow));
                double samplePhysicalX = (MarkerLeftInDips + sampleOffsetXInDips) * capturedScreen.MonitorDpiScale;
                double samplePhysicalY = (MarkerTopInDips + sampleOffsetYInDips) * capturedScreen.MonitorDpiScale;
                int sampleScreenshotX = (int)(samplePhysicalX * physicalToScreenshotScaleX);
                int sampleScreenshotY = (int)(samplePhysicalY * physicalToScreenshotScaleY);

                if (sampleScreenshotX < 0 || sampleScreenshotY < 0 || sampleScreenshotX >= screenshotWidth || sampleScreenshotY >= screenshotHeight)
                {
                    continue;
                }

                int pixelOffset = (sampleScreenshotY * stride) + (sampleScreenshotX * 4);
                byte bluePixelValue = screenshotPixels[pixelOffset];
                byte greenPixelValue = screenshotPixels[pixelOffset + 1];
                byte redPixelValue = screenshotPixels[pixelOffset + 2];

                markerSampleCount++;
                bool isMagenta = redPixelValue > 200 && bluePixelValue > 200 && greenPixelValue < 80;
                if (isMagenta)
                {
                    magentaSampleCount++;
                }
            }
        }

        return markerSampleCount > 0 && magentaSampleCount * 2 > markerSampleCount;
    }

    private static BitmapSource DecodeJpegToBgra(byte[] jpegImageBytes)
    {
        using MemoryStream jpegStream = new(jpegImageBytes);
        BitmapDecoder jpegDecoder = BitmapDecoder.Create(jpegStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        FormatConvertedBitmap bgraBitmap = new(jpegDecoder.Frames[0], PixelFormats.Bgra32, null, 0);
        bgraBitmap.Freeze();
        return bgraBitmap;
    }
}
