using System.Diagnostics;
using System.Runtime.InteropServices;
using Clicky.Core.Companion;
using Clicky.Core.Pointing;
using Clicky.Windows.Companion;
using Clicky.Windows.CursorOverlay;
using Clicky.Windows.Diagnostics;
using Clicky.Windows.Display;

namespace Clicky.Windows.Debugging;

/// <summary>
/// --overlay-test: drives the real overlay windows through every visual state without a
/// microphone, worker or Claude, so the overlay can be checked (and screenshotted with
/// --show-in-captures) on its own:
/// idle following, listening waveform, processing spinner, a pointing flight to a fixed
/// point on each monitor, and the responding state with a response bubble.
///
/// It also checks the two promises the overlay makes: it never takes focus (the foreground
/// window is unchanged) and it never blocks clicks (WindowFromPoint never lands on it).
/// </summary>
public static class OverlayTestRunner
{
    private const string LogCategory = "OverlayTest";

    private static readonly TimeSpan IdleFollowingDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ListeningDuration = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ProcessingDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RespondingDuration = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PointingFlightTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SyntheticAudioLevelInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Fixed pointing target: this fraction of each monitor's width and height from its top-left.</summary>
    private const double PointingTargetHorizontalFraction = 0.5;
    private const double PointingTargetVerticalFraction = 0.4;

    public static async Task<int> RunAsync(ClickyCommandLineOptions commandLineOptions)
    {
        IReadOnlyList<DisplayMonitorInfo> connectedMonitors = DisplayMonitorEnumerator.EnumerateMonitors();
        CompanionOverlayState overlayState = new();
        OverlayWindowManager overlayWindowManager = new(overlayState)
        {
            ExcludeOverlaysFromScreenCapture = !commandLineOptions.ShouldShowOverlaysInCaptures,
        };

        IntPtr foregroundWindowBeforeOverlay = GetForegroundWindow();
        LogStep($"Showing overlays on {connectedMonitors.Count} monitor(s); capture exclusion {overlayWindowManager.ExcludeOverlaysFromScreenCapture}.");
        overlayWindowManager.ShowOverlays(connectedMonitors, isFirstAppearance: false);
        foreach (string exclusionStatusLine in overlayWindowManager.DescribeCaptureExclusionStatus())
        {
            LogStep($"Overlay window: {exclusionStatusLine}");
        }

        LogStep("STATE idle: the triangle follows the mouse.");
        await Task.Delay(IdleFollowingDuration);

        LogStep("STATE listening: waveform with a synthetic microphone level.");
        overlayState.VoiceState = CompanionVoiceState.Listening;
        Stopwatch listeningStopwatch = Stopwatch.StartNew();
        while (listeningStopwatch.Elapsed < ListeningDuration)
        {
            double elapsedSeconds = listeningStopwatch.Elapsed.TotalSeconds;
            overlayState.CurrentAudioPowerLevel = 0.35 + (0.3 * Math.Sin(elapsedSeconds * 5.0));
            await Task.Delay(SyntheticAudioLevelInterval);
        }

        overlayState.CurrentAudioPowerLevel = 0;

        LogStep("STATE processing: spinner.");
        overlayState.VoiceState = CompanionVoiceState.Processing;
        await Task.Delay(ProcessingDuration);

        overlayState.VoiceState = CompanionVoiceState.Idle;
        bool didEveryPointingFlightFinish = true;

        foreach (DisplayMonitorInfo connectedMonitor in connectedMonitors)
        {
            PhysicalPixelRectangle monitorBounds = connectedMonitor.BoundsInPhysicalPixels;
            PhysicalPixelPoint pointingTarget = new(
                monitorBounds.Left + (monitorBounds.Width * PointingTargetHorizontalFraction),
                monitorBounds.Top + (monitorBounds.Height * PointingTargetVerticalFraction));

            LogStep($"STATE pointing: flying to {pointingTarget} on {connectedMonitor.DeviceName}.");
            TaskCompletionSource pointingFinishedCompletionSource = new();
            void HandlePointingAnimationFinished() => pointingFinishedCompletionSource.TrySetResult();
            overlayState.PointingAnimationFinished += HandlePointingAnimationFinished;

            overlayState.DetectedElementPointingTarget = new OverlayPointingTarget(pointingTarget, monitorBounds, "overlay test target");
            Task completedTask = await Task.WhenAny(pointingFinishedCompletionSource.Task, Task.Delay(PointingFlightTimeout));
            overlayState.PointingAnimationFinished -= HandlePointingAnimationFinished;

            bool didPointingFinish = completedTask == pointingFinishedCompletionSource.Task;
            didEveryPointingFlightFinish &= didPointingFinish;
            LogStep($"Pointing on {connectedMonitor.DeviceName} {(didPointingFinish ? "finished" : "TIMED OUT")}.");
            if (!didPointingFinish)
            {
                overlayState.DetectedElementPointingTarget = null;
            }
        }

        LogStep("STATE responding: triangle plus response bubble.");
        overlayState.VoiceState = CompanionVoiceState.Responding;
        overlayState.ResponseBubbleText = "this is the response bubble. it shows the spoken reply next to the cursor while the voice plays.";
        overlayState.IsResponseBubbleVisible = true;
        await Task.Delay(RespondingDuration);
        overlayState.IsResponseBubbleVisible = false;
        overlayState.VoiceState = CompanionVoiceState.Idle;

        bool isOverlayClickThrough = VerifyOverlayDoesNotBlockClicks(connectedMonitors);
        IntPtr foregroundWindowAfterOverlay = GetForegroundWindow();
        bool didForegroundWindowStayTheSame = foregroundWindowAfterOverlay == foregroundWindowBeforeOverlay
            || !IsWindowOwnedByThisProcess(foregroundWindowAfterOverlay);
        LogStep($"Focus check: foreground window {(didForegroundWindowStayTheSame ? "never moved to the overlay" : "MOVED TO THE OVERLAY")}.");

        LogStep("Fading out.");
        overlayWindowManager.FadeOutAndHideOverlays(TimeSpan.FromSeconds(0.4));
        await Task.Delay(TimeSpan.FromSeconds(0.6));

        bool didOverlayTestPass = didEveryPointingFlightFinish && isOverlayClickThrough && didForegroundWindowStayTheSame;
        LogStep($"Result: {(didOverlayTestPass ? "PASS" : "FAIL")}");
        return didOverlayTestPass ? 0 : 1;
    }

    /// <summary>
    /// Hit-tests a grid of points on every monitor. A click-through overlay is skipped by
    /// WindowFromPoint, so none of the hits may belong to this process.
    /// </summary>
    private static bool VerifyOverlayDoesNotBlockClicks(IReadOnlyList<DisplayMonitorInfo> connectedMonitors)
    {
        bool isOverlayClickThrough = true;

        foreach (DisplayMonitorInfo connectedMonitor in connectedMonitors)
        {
            PhysicalPixelRectangle monitorBounds = connectedMonitor.BoundsInPhysicalPixels;
            int pointsOwnedByThisProcess = 0;
            int pointsTested = 0;

            for (int gridRow = 1; gridRow <= 3; gridRow++)
            {
                for (int gridColumn = 1; gridColumn <= 3; gridColumn++)
                {
                    NativePoint testPoint = new()
                    {
                        X = monitorBounds.Left + (monitorBounds.Width * gridColumn / 4),
                        Y = monitorBounds.Top + (monitorBounds.Height * gridRow / 4),
                    };

                    IntPtr windowUnderPoint = WindowFromPoint(testPoint);
                    pointsTested++;
                    if (IsWindowOwnedByThisProcess(windowUnderPoint))
                    {
                        pointsOwnedByThisProcess++;
                    }
                }
            }

            bool isThisMonitorClickThrough = pointsOwnedByThisProcess == 0;
            isOverlayClickThrough &= isThisMonitorClickThrough;
            LogStep($"Click-through check on {connectedMonitor.DeviceName}: {pointsOwnedByThisProcess} of {pointsTested} test points hit Clicky -> {(isThisMonitorClickThrough ? "PASS" : "FAIL")}.");
        }

        return isOverlayClickThrough;
    }

    private static bool IsWindowOwnedByThisProcess(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(windowHandle, out uint owningProcessId);
        return owningProcessId == (uint)Environment.ProcessId;
    }

    private static void LogStep(string stepDescription)
    {
        ClickyApplicationLog.Write(LogCategory, stepDescription);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint screenPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}
