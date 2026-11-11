using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.Core.Pointing;
using Clicky.Windows.Companion;
using Clicky.Windows.Diagnostics;
using Clicky.Windows.Display;

namespace Clicky.Windows.CursorOverlay;

/// <summary>
/// Creates one cursor overlay window per monitor, so the buddy can follow the mouse across
/// monitors, and drives all of them from a single per-frame tick. Windows port of
/// OverlayWindowManager in OverlayWindow.swift. Create and use it on the WPF UI thread only.
/// </summary>
public sealed class OverlayWindowManager
{
    private const string OverlayLogCategory = "Overlay";

    private readonly CompanionOverlayState overlayState;
    private readonly Dispatcher userInterfaceDispatcher;

    /// <summary>One clock for every overlay, so flights, fades and the waveform stay in step across monitors.</summary>
    private readonly Stopwatch overlayAnimationClock = Stopwatch.StartNew();

    private readonly List<OverlayWindow> activeOverlayWindows = [];

    /// <summary>Windows playing their fade-out. They still draw, but no longer count as shown and never take part in pointing.</summary>
    private readonly List<OverlayWindow> fadingOverlayWindows = [];

    /// <summary>Reused every frame so the tick can safely iterate while handlers show or hide overlays.</summary>
    private readonly List<OverlayWindow> overlayWindowsToAdvanceThisFrame = [];

    /// <summary>
    /// While every overlay is at rest, the manager stops the per-frame tick and only polls the mouse.
    /// On a test machine with two 2560x1440 layered windows, an empty CompositionTarget.Rendering
    /// handler alone cost 2 to 9 percent of one core with nothing to draw; idle polling measured near 0.
    /// </summary>
    private static readonly TimeSpan IdleCursorPollInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// Frames in a row with nothing changing before switching to idle polling. Waking up is
    /// immediate, so this only avoids flipping between the two modes on every pause in mouse movement.
    /// </summary>
    private const int SettledFramesBeforeIdlePolling = 10;

    private readonly DispatcherTimer idleCursorPollTimer;
    private bool isSubscribedToCompositionTargetRendering;
    private bool isIdlePolling;
    private int consecutiveSettledFrameCount;
    private PhysicalPixelPoint cursorPositionWhenIdlePollingStarted;
    private TimeSpan lastRenderingTime = TimeSpan.MinValue;
    private double lastFrameTimeInSeconds;
    private bool hasRaisedWelcomeAnimationCompletedForCurrentOverlays;
    private Color? captureTestMarkerColor;

    public OverlayWindowManager(CompanionOverlayState overlayState)
    {
        this.overlayState = overlayState;
        userInterfaceDispatcher = Dispatcher.CurrentDispatcher;

        idleCursorPollTimer = new DispatcherTimer(DispatcherPriority.Background, userInterfaceDispatcher)
        {
            Interval = IdleCursorPollInterval,
        };
        idleCursorPollTimer.Tick += HandleIdleCursorPollTick;

        // The manager lives as long as the app, so this subscription is never removed.
        overlayState.PropertyChanged += HandleOverlayStatePropertyChanged;
    }

    /// <summary>Raised once per ShowOverlays(isFirstAppearance: true), after the welcome bubble has faded out. The companion starts the onboarding demo here.</summary>
    public event Action? WelcomeAnimationCompleted;

    public bool IsShowingOverlay => activeOverlayWindows.Count > 0;

    /// <summary>
    /// Default true: every overlay window calls SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) so it
    /// never appears in the screenshots sent to Claude. A debug flag turns it off so a person can take
    /// documentation screenshots. Applies to windows created after the change.
    /// </summary>
    public bool ExcludeOverlaysFromScreenCapture { get; set; } = true;

    /// <summary>
    /// Debug only, default null. When set, each overlay draws a solid 200x200 DIP square of this color
    /// 100 DIPs from its top-left corner, so the capture test can prove the overlay is left out of screenshots.
    /// </summary>
    public Color? CaptureTestMarkerColor
    {
        get => captureTestMarkerColor;
        set
        {
            captureTestMarkerColor = value;
            foreach (OverlayWindow overlayWindow in activeOverlayWindows)
            {
                overlayWindow.CursorOverlayView.SetCaptureTestMarkerColor(value);
            }
        }
    }

    /// <summary>
    /// Removes existing overlays, then creates one overlay window per monitor.
    /// <paramref name="isFirstAppearance"/> plays the welcome animation on the monitor with the mouse.
    /// </summary>
    public void ShowOverlays(IReadOnlyList<DisplayMonitorInfo> monitors, bool isFirstAppearance)
    {
        HideOverlays();
        hasRaisedWelcomeAnimationCompletedForCurrentOverlays = false;

        // Exactly one overlay plays the welcome: the monitor under the mouse, or the nearest one if the
        // mouse sits exactly on an outer edge. Letting each window decide could pick no monitor at all,
        // and WelcomeAnimationCompleted would then never fire.
        DisplayMonitorInfo? welcomeMonitor = isFirstAppearance
            ? DisplayMonitorEnumerator.FindMonitorContainingPoint(monitors, CursorPositionReader.GetCursorPositionInPhysicalPixels())
            : null;

        foreach (DisplayMonitorInfo monitor in monitors)
        {
            bool playsWelcomeAnimation = welcomeMonitor is not null && ReferenceEquals(monitor, welcomeMonitor);

            OverlayWindow overlayWindow = new(
                monitor,
                overlayState,
                overlayAnimationClock,
                ExcludeOverlaysFromScreenCapture,
                playsWelcomeAnimation,
                captureTestMarkerColor);
            overlayWindow.CursorOverlayView.WelcomeAnimationFinished += () => HandleWelcomeAnimationFinished(overlayWindow);
            overlayWindow.CursorOverlayView.FrameTicksRequested += ResumeFrameTicksIfIdlePolling;

            activeOverlayWindows.Add(overlayWindow);
            overlayWindow.Show();

            // WPF finishes its own initial placement inside Show, so put the exact physical bounds back afterwards.
            overlayWindow.ApplyExactMonitorBounds();
        }

        ClickyApplicationLog.Write(OverlayLogCategory, $"Showing {activeOverlayWindows.Count} cursor overlay(s), first appearance: {isFirstAppearance}.");
        UpdateFrameTickSource();

        // New overlays only react to targets set after they exist (Mac: onChange). A target that is
        // already pending would keep every buddy hidden, so release it.
        if (overlayState.DetectedElementPointingTarget is OverlayPointingTarget pendingPointingTarget)
        {
            ReleasePointingTargetIfNoOverlayHandlesIt(pendingPointingTarget);
        }
    }

    public void HideOverlays()
    {
        foreach (OverlayWindow overlayWindow in activeOverlayWindows)
        {
            overlayWindow.CloseOverlay();
        }

        foreach (OverlayWindow overlayWindow in fadingOverlayWindows)
        {
            overlayWindow.CloseOverlay();
        }

        activeOverlayWindows.Clear();
        fadingOverlayWindows.Clear();
        UpdateFrameTickSource();

        // An overlay closed mid-pointing can no longer finish, so let the companion move on.
        if (overlayState.DetectedElementPointingTarget is OverlayPointingTarget pendingPointingTarget)
        {
            ReleasePointingTargetIfNoOverlayHandlesIt(pendingPointingTarget);
        }
    }

    /// <summary>
    /// Mac: fadeOutAndHideOverlay. Fades every window to transparent with an ease-in curve, then
    /// closes it. <see cref="IsShowingOverlay"/> turns false immediately.
    /// </summary>
    public void FadeOutAndHideOverlays(TimeSpan fadeDuration)
    {
        if (activeOverlayWindows.Count == 0)
        {
            return;
        }

        List<OverlayWindow> overlayWindowsToFade = [.. activeOverlayWindows];
        activeOverlayWindows.Clear();

        foreach (OverlayWindow overlayWindow in overlayWindowsToFade)
        {
            // A fading overlay is on its way out: it keeps drawing for the fade but stops pointing,
            // so a ShowOverlays during the fade can never end up with two buddies pointing.
            overlayWindow.CursorOverlayView.AbandonPointingTarget();
            fadingOverlayWindows.Add(overlayWindow);
            overlayWindow.BeginFadeOut(fadeDuration, () => HandleOverlayFadeOutCompleted(overlayWindow));
        }

        // Fading overlays keep the buddy following during the fade, so they need frames.
        ResumeFrameTicksIfIdlePolling();

        if (overlayState.DetectedElementPointingTarget is OverlayPointingTarget pendingPointingTarget)
        {
            ReleasePointingTargetIfNoOverlayHandlesIt(pendingPointingTarget);
        }
    }

    /// <summary>For the capture test report: one line per shown window with device name, bounds and the SetWindowDisplayAffinity result.</summary>
    public IReadOnlyList<string> DescribeCaptureExclusionStatus()
    {
        return activeOverlayWindows
            .Select(overlayWindow => overlayWindow.DescribeCaptureExclusionStatus())
            .ToList();
    }

    private void HandleOverlayFadeOutCompleted(OverlayWindow fadedOverlayWindow)
    {
        // HideOverlays may already have closed this window and cleared the list; both calls below are safe to repeat.
        fadingOverlayWindows.Remove(fadedOverlayWindow);
        fadedOverlayWindow.CloseOverlay();
        UpdateFrameTickSource();
    }

    private void HandleWelcomeAnimationFinished(OverlayWindow welcomeOverlayWindow)
    {
        // Only a welcome that played on a shown overlay counts. If the overlays were hidden or
        // replaced in the meantime, starting the onboarding demo would play it on nothing.
        if (hasRaisedWelcomeAnimationCompletedForCurrentOverlays || !activeOverlayWindows.Contains(welcomeOverlayWindow))
        {
            return;
        }

        hasRaisedWelcomeAnimationCompletedForCurrentOverlays = true;
        ClickyApplicationLog.Write(OverlayLogCategory, "Welcome animation finished.");
        WelcomeAnimationCompleted?.Invoke();
    }

    private void HandleOverlayStatePropertyChanged(object? sender, PropertyChangedEventArgs propertyChangedEventArgs)
    {
        // Everything the overlays draw comes from this state, so any change needs frames again.
        ResumeFrameTicksIfIdlePolling();

        if (propertyChangedEventArgs.PropertyName != nameof(CompanionOverlayState.DetectedElementPointingTarget))
        {
            return;
        }

        // Null means pointing finished or the companion cleared the target. Overlays re-check their
        // visibility every frame, and one still mid-animation plays it out, the same as on the Mac.
        if (overlayState.DetectedElementPointingTarget is not OverlayPointingTarget newPointingTarget)
        {
            return;
        }

        RoutePointingTargetToItsMonitor(newPointingTarget);
    }

    /// <summary>
    /// Mac: every BlueCursorView watched detectedElementScreenLocation and only the one whose screen
    /// matched flew. Routing it here keeps that rule in one place: the overlay whose monitor bounds
    /// equal the target's flies, and every other overlay drops any older pointing it was doing.
    /// </summary>
    private void RoutePointingTargetToItsMonitor(OverlayPointingTarget pointingTarget)
    {
        OverlayWindow? targetMonitorOverlayWindow = activeOverlayWindows.FirstOrDefault(overlayWindow =>
            overlayWindow.OverlayMonitor.BoundsInPhysicalPixels == pointingTarget.TargetMonitorBoundsInPhysicalPixels);

        foreach (OverlayWindow overlayWindow in activeOverlayWindows)
        {
            if (!ReferenceEquals(overlayWindow, targetMonitorOverlayWindow))
            {
                overlayWindow.CursorOverlayView.AbandonPointingTarget();
            }
        }

        bool didOverlayStartPointing = targetMonitorOverlayWindow is not null
            && targetMonitorOverlayWindow.CursorOverlayView.TryBeginPointingAt(pointingTarget);

        if (!didOverlayStartPointing)
        {
            // Small fix over the Mac: there, a target nobody could animate (no overlay on that monitor,
            // or the welcome text still showing) stayed set until the next push-to-talk, hiding the buddy
            // and leaving the companion waiting. Releasing it lets the companion carry on.
            ReleasePointingTargetIfNoOverlayHandlesIt(pointingTarget);
        }
    }

    /// <summary>
    /// Clears the target on the next dispatcher turn if it is still pending and no shown overlay is
    /// animating it. Deferred on purpose: this often runs inside the target's own PropertyChanged,
    /// and clearing it synchronously there would fire PointingAnimationFinished before the code that
    /// set the target had even returned.
    /// </summary>
    private void ReleasePointingTargetIfNoOverlayHandlesIt(OverlayPointingTarget pointingTarget)
    {
        userInterfaceDispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(overlayState.DetectedElementPointingTarget, pointingTarget))
            {
                return;
            }

            bool isAnyOverlayHandlingTarget = activeOverlayWindows.Any(overlayWindow =>
                overlayWindow.CursorOverlayView.IsHandlingPointingTarget(pointingTarget));
            if (isAnyOverlayHandlingTarget)
            {
                return;
            }

            ClickyApplicationLog.Write(OverlayLogCategory, "No shown overlay can point at the current target, so it was released right away.");
            overlayState.NotifyPointingAnimationFinished();
        });
    }

    /// <summary>With overlays on screen, tick every frame (or idle-poll if already at rest); with none, stop both.</summary>
    private void UpdateFrameTickSource()
    {
        bool hasOverlaysToAdvance = activeOverlayWindows.Count > 0 || fadingOverlayWindows.Count > 0;

        if (!hasOverlaysToAdvance)
        {
            StopIdlePolling();
            UnsubscribeFromCompositionTargetRendering();
            return;
        }

        if (!isIdlePolling)
        {
            SubscribeToCompositionTargetRendering();
        }
    }

    private void SubscribeToCompositionTargetRendering()
    {
        if (isSubscribedToCompositionTargetRendering)
        {
            return;
        }

        // Start the elapsed-time count fresh so the first frame after a pause does not see a huge jump.
        lastFrameTimeInSeconds = overlayAnimationClock.Elapsed.TotalSeconds;
        consecutiveSettledFrameCount = 0;
        CompositionTarget.Rendering += HandleCompositionTargetRendering;
        isSubscribedToCompositionTargetRendering = true;
    }

    private void UnsubscribeFromCompositionTargetRendering()
    {
        if (!isSubscribedToCompositionTargetRendering)
        {
            return;
        }

        CompositionTarget.Rendering -= HandleCompositionTargetRendering;
        isSubscribedToCompositionTargetRendering = false;
    }

    private void EnterIdlePolling(PhysicalPixelPoint currentCursorPositionInPhysicalPixels)
    {
        UnsubscribeFromCompositionTargetRendering();
        cursorPositionWhenIdlePollingStarted = currentCursorPositionInPhysicalPixels;
        isIdlePolling = true;
        idleCursorPollTimer.Start();
    }

    private void StopIdlePolling()
    {
        isIdlePolling = false;
        idleCursorPollTimer.Stop();
    }

    /// <summary>Called for anything that changes what the overlays draw: mouse movement, overlay state, a new DPI, a fade.</summary>
    private void ResumeFrameTicksIfIdlePolling()
    {
        if (!isIdlePolling)
        {
            return;
        }

        StopIdlePolling();
        UpdateFrameTickSource();
    }

    private void HandleIdleCursorPollTick(object? sender, EventArgs eventArgs)
    {
        // GetCursorPos is a cheap call, far cheaper than WPF running its render loop for two full-screen layered windows.
        PhysicalPixelPoint currentCursorPositionInPhysicalPixels = CursorPositionReader.GetCursorPositionInPhysicalPixels();
        if (currentCursorPositionInPhysicalPixels != cursorPositionWhenIdlePollingStarted)
        {
            ResumeFrameTicksIfIdlePolling();
        }
    }

    /// <summary>
    /// One tick per displayed frame for every overlay: the mouse is read once and each overlay
    /// moves its animations forward with real elapsed time. Overlays with nothing moving touch no
    /// WPF properties, so WPF has nothing to redraw.
    /// </summary>
    private void HandleCompositionTargetRendering(object? sender, EventArgs eventArgs)
    {
        // WPF can raise Rendering more than once for the same frame; RenderingTime only moves forward once per frame.
        if (eventArgs is RenderingEventArgs renderingEventArgs)
        {
            if (renderingEventArgs.RenderingTime == lastRenderingTime)
            {
                return;
            }

            lastRenderingTime = renderingEventArgs.RenderingTime;
        }

        double nowInSeconds = overlayAnimationClock.Elapsed.TotalSeconds;
        double frameElapsedInSeconds = Math.Max(0, nowInSeconds - lastFrameTimeInSeconds);
        lastFrameTimeInSeconds = nowInSeconds;

        PhysicalPixelPoint cursorPositionInPhysicalPixels = CursorPositionReader.GetCursorPositionInPhysicalPixels();

        // Handlers raised inside a frame (welcome finished, pointing finished) may show or hide
        // overlays, so iterate a snapshot and skip anything closed along the way.
        overlayWindowsToAdvanceThisFrame.Clear();
        overlayWindowsToAdvanceThisFrame.AddRange(activeOverlayWindows);
        overlayWindowsToAdvanceThisFrame.AddRange(fadingOverlayWindows);

        foreach (OverlayWindow overlayWindow in overlayWindowsToAdvanceThisFrame)
        {
            if (overlayWindow.IsOverlayClosed)
            {
                continue;
            }

            overlayWindow.CursorOverlayView.AdvanceFrame(cursorPositionInPhysicalPixels, nowInSeconds, frameElapsedInSeconds);
        }

        overlayWindowsToAdvanceThisFrame.Clear();

        // A handler raised during this frame may have hidden every overlay and stopped the tick already.
        if (!isSubscribedToCompositionTargetRendering)
        {
            return;
        }

        bool areAllOverlaysAtRest = fadingOverlayWindows.Count == 0
            && activeOverlayWindows.All(overlayWindow => overlayWindow.IsOverlayClosed || overlayWindow.CursorOverlayView.IsAtRest);
        consecutiveSettledFrameCount = areAllOverlaysAtRest ? consecutiveSettledFrameCount + 1 : 0;

        if (consecutiveSettledFrameCount >= SettledFramesBeforeIdlePolling)
        {
            EnterIdlePolling(cursorPositionInPhysicalPixels);
        }
    }
}
