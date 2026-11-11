using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Clicky.Core.Companion;
using Clicky.Core.Overlay;
using Clicky.Core.Pointing;
using Clicky.Windows.Companion;
using Clicky.Windows.Display;

namespace Clicky.Windows.CursorOverlay;

/// <summary>What the buddy is doing on this monitor. Mac: BuddyNavigationMode, with its isReturningToCursor flag as its own phase.</summary>
internal enum BuddyNavigationPhase
{
    /// <summary>Trailing the mouse on a spring.</summary>
    FollowingCursor,

    /// <summary>Flying along the bezier arc to the element Claude pointed at.</summary>
    FlyingToTarget,

    /// <summary>Parked beside the element with the pointing bubble.</summary>
    PointingAtTarget,

    /// <summary>Flying back to the mouse. The only flight the user can cut short by moving the mouse.</summary>
    FlyingBackToCursor,
}

/// <summary>
/// Everything one monitor's overlay draws, plus the per-frame behavior behind it.
/// Windows port of BlueCursorView in OverlayWindow.swift. Instead of SwiftUI's implicit
/// animations and a set of timers, one <see cref="AdvanceFrame"/> call per frame moves every
/// value forward with the shared overlay clock, then pushes only the values that changed to WPF.
/// </summary>
internal sealed class BlueCursorOverlayView : Canvas
{
    // Welcome sequence timing (Mac: BlueCursorView.onAppear and startWelcomeAnimation).
    private const double WelcomeCursorFadeInSeconds = 2.0;
    private const double WelcomeBubbleFadeInSeconds = 0.4;
    private const double WelcomeCharacterIntervalSeconds = 0.03;
    private const double WelcomeBubbleHoldSeconds = 2.0;
    private const double WelcomeBubbleFadeOutSeconds = 0.5;

    // Cross-fades between the triangle, waveform and spinner (Mac: .animation(.easeIn(...), value: voiceState)).
    private const double TriangleCrossFadeSeconds = 0.25;
    private const double WaveformAndSpinnerCrossFadeSeconds = 0.15;

    /// <summary>Mac: .animation(.easeInOut(duration: 0.3), value: triangleRotationDegrees) outside of flights.</summary>
    private const double TriangleRotationEaseSeconds = 0.3;

    /// <summary>Mac pointing bubble: starts at scaleEffect 0.5 and springs to 1.</summary>
    private const double PointingBubblePopStartScale = 0.5;

    private const double OnboardingPromptFadeInSeconds = 0.4;
    private const double OnboardingPromptFadeOutSeconds = 0.3;
    private const double ResponseBubbleFadeSeconds = 0.25;

    /// <summary>The spring is considered at rest once it is this close to the mouse and barely moving.</summary>
    private const double FollowSpringSettleDistanceInDips = 0.05;

    /// <summary>Mac CompanionResponseOverlayView shows "..." until the first words of the reply arrive.</summary>
    private const string EmptyResponsePlaceholderText = "...";

    private const double CaptureTestMarkerSizeInDips = 200;
    private const double CaptureTestMarkerOffsetFromCornerInDips = 100;

    private readonly DisplayMonitorInfo overlayMonitor;
    private readonly CompanionOverlayState overlayState;
    private readonly Stopwatch overlayAnimationClock;
    private readonly bool playsWelcomeAnimation;

    private readonly BlueCursorTriangleVisual triangleVisual = new();
    private readonly ListeningWaveformVisual waveformVisual = new();
    private readonly ProcessingSpinnerVisual spinnerVisual = new();
    private readonly CompanionBlueBubbleVisual welcomeBubbleVisual = new();
    private readonly CompanionBlueBubbleVisual onboardingPromptBubbleVisual = new();
    private readonly CompanionBlueBubbleVisual pointingBubbleVisual = new();
    private readonly CompanionResponseBubbleVisual responseBubbleVisual = new();
    private readonly Rectangle captureTestMarkerRectangle;

    // Monitor geometry in this window's DIPs, recomputed whenever WPF reports a new DPI for the window.
    private double windowDpiScale;
    private double overlayWidthInDips;
    private double overlayHeightInDips;
    private Rect workAreaInOverlayDips;

    // Frame inputs.
    private bool hasStartedTimeline;
    private bool isCursorOnThisScreen;
    private OverlayDipPoint latestCursorPositionInOverlayDips;

    // Cursor following.
    private readonly CursorFollowSpring cursorFollowSpring = new();
    private bool isFollowSpringSettled;
    private OverlayDipPoint settledFollowTargetPosition;
    private bool shouldSnapBuddyToCursorOnNextFrame;
    private OverlayDipPoint buddyPosition;

    // Flying to, pointing at and returning from an element.
    private BuddyNavigationPhase navigationPhase = BuddyNavigationPhase.FollowingCursor;
    private OverlayPointingTarget? activePointingTarget;
    private CursorFlightPath? activeFlightPath;
    private double flightStartTimeInSeconds;
    private double buddyFlightScale = 1.0;
    private OverlayDipPoint cursorPositionWhenReturnFlightStarted;
    private readonly EasedValueTween triangleRotationTween = new(CursorOverlayGeometry.DefaultTriangleRotationDegrees);

    // Pointing bubble.
    private StreamedBubbleText? pointingBubbleTextStream;
    private double pointingBubbleFadeOutStartTimeInSeconds;
    private bool hasPointingBubbleFadeOutStarted;
    private readonly EasedValueTween pointingBubbleOpacityTween = new(0);
    private readonly BubblePopScaleSpring pointingBubblePopScaleSpring = new();

    // Welcome sequence (only on the monitor the manager picked).
    private readonly EasedValueTween cursorOpacityTween = new(1);
    private double welcomeTextStartTimeInSeconds;
    private StreamedBubbleText? welcomeBubbleTextStream;
    private readonly EasedValueTween welcomeBubbleOpacityTween = new(0);
    private bool hasWelcomeBubbleFadeOutStarted;
    private bool isWelcomeAnimationFinished;

    // Triangle, waveform and spinner cross-fades driven by the voice state.
    private CompanionVoiceState lastObservedVoiceState;
    private readonly EasedValueTween trianglePresenceTween = new(0);
    private readonly EasedValueTween waveformPresenceTween = new(0);
    private readonly EasedValueTween spinnerPresenceTween = new(0);

    // Onboarding prompt bubble.
    private bool lastObservedOnboardingPromptRequest;
    private string displayedOnboardingPromptText = string.Empty;
    private readonly EasedValueTween onboardingPromptOpacityTween = new(0);

    // Response bubble.
    private bool lastObservedResponseBubbleRequest;
    private string displayedResponseText = string.Empty;
    private readonly EasedValueTween responseBubbleOpacityTween = new(0);

    public BlueCursorOverlayView(
        DisplayMonitorInfo overlayMonitor,
        CompanionOverlayState overlayState,
        Stopwatch overlayAnimationClock,
        bool playsWelcomeAnimation,
        Color? captureTestMarkerColor)
    {
        this.overlayMonitor = overlayMonitor;
        this.overlayState = overlayState;
        this.overlayAnimationClock = overlayAnimationClock;
        this.playsWelcomeAnimation = playsWelcomeAnimation;

        // The overlay never takes mouse or keyboard input; clicks go to the apps underneath.
        IsHitTestVisible = false;
        Focusable = false;

        // A layered window has no opaque background for ClearType to blend against, so WPF would fall
        // back to grayscale anyway. Saying so up front keeps text rendering identical on every machine.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        captureTestMarkerRectangle = new Rectangle
        {
            Width = CaptureTestMarkerSizeInDips,
            Height = CaptureTestMarkerSizeInDips,
            IsHitTestVisible = false,
        };
        SetLeft(captureTestMarkerRectangle, CaptureTestMarkerOffsetFromCornerInDips);
        SetTop(captureTestMarkerRectangle, CaptureTestMarkerOffsetFromCornerInDips);
        SetCaptureTestMarkerColor(captureTestMarkerColor);

        // Drawing order follows the Mac ZStack: bubbles underneath, the cursor shapes on top.
        Children.Add(captureTestMarkerRectangle);
        Children.Add(responseBubbleVisual.RootElement);
        Children.Add(welcomeBubbleVisual.RootElement);
        Children.Add(onboardingPromptBubbleVisual.RootElement);
        Children.Add(pointingBubbleVisual.RootElement);
        Children.Add(triangleVisual.RootElement);
        Children.Add(waveformVisual.RootElement);
        Children.Add(spinnerVisual.RootElement);

        // Until the window exists, assume the monitor's own scale; the window corrects it from SourceInitialized on.
        UpdateWindowDpiScale(overlayMonitor.DpiScale);
    }

    /// <summary>Raised once, from inside a frame, after the welcome bubble has faded out.</summary>
    public event Action? WelcomeAnimationFinished;

    /// <summary>Raised when something outside a frame changed that needs frames again (a new DPI), so an idle manager wakes up.</summary>
    public event Action? FrameTicksRequested;

    public double WindowDpiScale => windowDpiScale;

    /// <summary>
    /// True when another frame would change nothing on screen: the buddy is following and its spring
    /// is at rest, no fade, turn, flight, welcome or pointing is in progress, and neither the waveform
    /// nor the spinner (which animate continuously) is on. Only the mouse moving or the companion
    /// changing the overlay state can make this false again.
    /// </summary>
    public bool IsAtRest =>
        hasStartedTimeline
        && navigationPhase == BuddyNavigationPhase.FollowingCursor
        && isFollowSpringSettled
        && !shouldSnapBuddyToCursorOnNextFrame
        && (!playsWelcomeAnimation || isWelcomeAnimationFinished)
        && lastObservedVoiceState != CompanionVoiceState.Listening
        && lastObservedVoiceState != CompanionVoiceState.Processing
        && !cursorOpacityTween.IsAnimating
        && !triangleRotationTween.IsAnimating
        && !trianglePresenceTween.IsAnimating
        && !waveformPresenceTween.IsAnimating
        && !spinnerPresenceTween.IsAnimating
        && !onboardingPromptOpacityTween.IsAnimating
        && !responseBubbleOpacityTween.IsAnimating;

    /// <summary>True while this overlay is flying to, pointing at, or returning from exactly this target.</summary>
    public bool IsHandlingPointingTarget(OverlayPointingTarget pointingTarget)
    {
        return navigationPhase != BuddyNavigationPhase.FollowingCursor
            && ReferenceEquals(activePointingTarget, pointingTarget);
    }

    public void SetCaptureTestMarkerColor(Color? captureTestMarkerColor)
    {
        if (captureTestMarkerColor is Color markerColor)
        {
            captureTestMarkerRectangle.Fill = OverlayVisualStyle.CreateFrozenSolidBrush(markerColor, 1.0);
            captureTestMarkerRectangle.Visibility = Visibility.Visible;
        }
        else
        {
            captureTestMarkerRectangle.Fill = null;
            captureTestMarkerRectangle.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Called with the window's real DPI once it exists and again after every WM_DPICHANGED.</summary>
    public void UpdateWindowDpiScale(double newWindowDpiScale)
    {
        if (newWindowDpiScale <= 0 || double.IsNaN(newWindowDpiScale) || double.IsInfinity(newWindowDpiScale))
        {
            return;
        }

        windowDpiScale = newWindowDpiScale;
        (overlayWidthInDips, overlayHeightInDips) = ScreenCoordinateMapper.ComputeOverlaySizeInDips(overlayMonitor.BoundsInPhysicalPixels, windowDpiScale);
        workAreaInOverlayDips = ComputeWorkAreaInOverlayDips();

        welcomeBubbleVisual.InvalidateMeasuredSize();
        onboardingPromptBubbleVisual.InvalidateMeasuredSize();
        pointingBubbleVisual.InvalidateMeasuredSize();
        responseBubbleVisual.InvalidateMeasuredSize();

        // The spring's remembered position is in the old DIP units, so jump straight to the mouse
        // instead of letting it swing across the screen. A flight in progress keeps going.
        if (navigationPhase == BuddyNavigationPhase.FollowingCursor)
        {
            shouldSnapBuddyToCursorOnNextFrame = true;
        }

        FrameTicksRequested?.Invoke();
    }

    /// <summary>
    /// Mac: onChange(of: detectedElementScreenLocation) on the screen that owns the target.
    /// Starts the flight from wherever the buddy is right now, which also restarts a flight
    /// already in progress when a new target replaces the old one.
    /// </summary>
    /// <returns>False when this overlay cannot point right now (the welcome bubble is showing), so the caller can release the target.</returns>
    public bool TryBeginPointingAt(OverlayPointingTarget pointingTarget)
    {
        double nowInSeconds = overlayAnimationClock.Elapsed.TotalSeconds;

        // A target can arrive before this overlay drew its first frame; start from the real mouse position.
        if (!hasStartedTimeline)
        {
            UpdateCursorObservation(CursorPositionReader.GetCursorPositionInPhysicalPixels());
            StartTimeline(nowInSeconds);
        }

        // Mac: startNavigatingToElement returns early while the welcome text is on screen, so pointing never interrupts it.
        if (IsWelcomeBubbleShowingText(nowInSeconds))
        {
            return false;
        }

        activePointingTarget = pointingTarget;

        OverlayDipPoint targetLocationInOverlayDips = ScreenCoordinateMapper.MapPhysicalPixelsToOverlayDips(
            pointingTarget.TargetLocationInPhysicalPixels,
            overlayMonitor.BoundsInPhysicalPixels,
            windowDpiScale);
        OverlayDipPoint flightDestination = CursorOverlayGeometry.ComputeNavigationDestination(
            targetLocationInOverlayDips,
            overlayWidthInDips,
            overlayHeightInDips);

        ClearPointingBubble();
        StartFlight(flightDestination, BuddyNavigationPhase.FlyingToTarget, nowInSeconds);
        return true;
    }

    /// <summary>
    /// Another monitor took over pointing, or this overlay is fading away: stop quietly and follow
    /// the mouse again. The companion is not notified, because the target is not this overlay's to finish.
    /// </summary>
    public void AbandonPointingTarget()
    {
        if (navigationPhase == BuddyNavigationPhase.FollowingCursor)
        {
            return;
        }

        ResumeFollowingCursor(overlayAnimationClock.Elapsed.TotalSeconds, shouldNotifyCompanion: false);
    }

    /// <summary>Moves every animation forward to <paramref name="nowInSeconds"/> and updates whatever changed on screen.</summary>
    public void AdvanceFrame(PhysicalPixelPoint cursorPositionInPhysicalPixels, double nowInSeconds, double frameElapsedInSeconds)
    {
        UpdateCursorObservation(cursorPositionInPhysicalPixels);

        if (!hasStartedTimeline)
        {
            StartTimeline(nowInSeconds);
        }

        AdvanceBuddyMovement(nowInSeconds, frameElapsedInSeconds);
        AdvanceWelcomeAnimation(nowInSeconds);
        ObserveVoiceStateChange(nowInSeconds);
        ObserveOnboardingPromptChange(nowInSeconds);
        ObserveResponseBubbleChange(nowInSeconds);

        RenderFrame(nowInSeconds, frameElapsedInSeconds);
    }

    private void UpdateCursorObservation(PhysicalPixelPoint cursorPositionInPhysicalPixels)
    {
        isCursorOnThisScreen = overlayMonitor.BoundsInPhysicalPixels.ContainsPoint(cursorPositionInPhysicalPixels.X, cursorPositionInPhysicalPixels.Y);

        // Mapped even when the mouse is on another monitor (the result is outside this overlay), so the
        // buddy here is already near the right spot when the mouse crosses over, as on the Mac.
        latestCursorPositionInOverlayDips = ScreenCoordinateMapper.MapPhysicalPixelsToOverlayDips(
            cursorPositionInPhysicalPixels,
            overlayMonitor.BoundsInPhysicalPixels,
            windowDpiScale);
    }

    /// <summary>Mac: BlueCursorView.init and onAppear. Runs once, on the first frame or the first pointing request.</summary>
    private void StartTimeline(double nowInSeconds)
    {
        hasStartedTimeline = true;

        // Seed the buddy at the mouse so it does not fly in from the corner.
        OverlayDipPoint initialBuddyPosition = CursorOverlayGeometry.ApplyCursorFollowOffset(latestCursorPositionInOverlayDips);
        cursorFollowSpring.SnapTo(initialBuddyPosition);
        buddyPosition = initialBuddyPosition;
        isFollowSpringSettled = true;
        settledFollowTargetPosition = initialBuddyPosition;
        shouldSnapBuddyToCursorOnNextFrame = false;

        if (playsWelcomeAnimation)
        {
            cursorOpacityTween.SnapTo(0);
            cursorOpacityTween.AnimateTo(1, WelcomeCursorFadeInSeconds, OverlayEasingCurve.EaseIn, nowInSeconds);
            welcomeTextStartTimeInSeconds = nowInSeconds + WelcomeCursorFadeInSeconds;
        }
        else
        {
            cursorOpacityTween.SnapTo(1);
        }

        // Whatever is already true when the overlay appears shows at once, without a fade.
        lastObservedVoiceState = overlayState.VoiceState;
        trianglePresenceTween.SnapTo(ShowsTriangle(lastObservedVoiceState) ? 1 : 0);
        waveformPresenceTween.SnapTo(lastObservedVoiceState == CompanionVoiceState.Listening ? 1 : 0);
        spinnerPresenceTween.SnapTo(lastObservedVoiceState == CompanionVoiceState.Processing ? 1 : 0);

        lastObservedOnboardingPromptRequest = overlayState.IsOnboardingPromptVisible;
        onboardingPromptOpacityTween.SnapTo(lastObservedOnboardingPromptRequest ? 1 : 0);
        if (lastObservedOnboardingPromptRequest)
        {
            displayedOnboardingPromptText = overlayState.OnboardingPromptText;
        }

        lastObservedResponseBubbleRequest = overlayState.IsResponseBubbleVisible;
        responseBubbleOpacityTween.SnapTo(lastObservedResponseBubbleRequest ? 1 : 0);
        if (lastObservedResponseBubbleRequest)
        {
            displayedResponseText = BuildDisplayedResponseText(overlayState.ResponseBubbleText);
        }
    }

    // Buddy movement

    private void AdvanceBuddyMovement(double nowInSeconds, double frameElapsedInSeconds)
    {
        switch (navigationPhase)
        {
            case BuddyNavigationPhase.FollowingCursor:
                AdvanceCursorFollowing(frameElapsedInSeconds);
                break;

            case BuddyNavigationPhase.FlyingToTarget:
                // The outbound flight and the pointing hold are never interrupted by mouse movement.
                AdvanceFlight(nowInSeconds);
                break;

            case BuddyNavigationPhase.PointingAtTarget:
                AdvancePointing(nowInSeconds);
                break;

            case BuddyNavigationPhase.FlyingBackToCursor:
                // Only the return flight can be cut short: if the user moves the mouse far enough,
                // the buddy snaps back to following instead of finishing a flight to a stale spot.
                double mouseTravelSinceReturnStartedInDips = ComputeDistanceInDips(latestCursorPositionInOverlayDips, cursorPositionWhenReturnFlightStarted);
                if (mouseTravelSinceReturnStartedInDips > CursorOverlayGeometry.ReturnFlightCancelDistanceInDips)
                {
                    ResumeFollowingCursor(nowInSeconds, shouldNotifyCompanion: true);
                }
                else
                {
                    AdvanceFlight(nowInSeconds);
                }

                break;
        }
    }

    private void AdvanceCursorFollowing(double frameElapsedInSeconds)
    {
        OverlayDipPoint followTargetPosition = CursorOverlayGeometry.ApplyCursorFollowOffset(latestCursorPositionInOverlayDips);

        if (shouldSnapBuddyToCursorOnNextFrame)
        {
            shouldSnapBuddyToCursorOnNextFrame = false;
            cursorFollowSpring.SnapTo(followTargetPosition);
            isFollowSpringSettled = true;
            settledFollowTargetPosition = followTargetPosition;
            buddyPosition = followTargetPosition;
            return;
        }

        // Mouse has not moved and the spring is at rest: nothing to compute and nothing to redraw.
        if (isFollowSpringSettled && followTargetPosition == settledFollowTargetPosition)
        {
            return;
        }

        OverlayDipPoint positionBeforeAdvance = cursorFollowSpring.CurrentPosition;
        OverlayDipPoint positionAfterAdvance = cursorFollowSpring.Advance(followTargetPosition, frameElapsedInSeconds);

        // A damped spring only approaches its target, so stop it once it is close and slow.
        bool isCloseToMouse = ComputeDistanceInDips(positionAfterAdvance, followTargetPosition) < FollowSpringSettleDistanceInDips;
        bool barelyMovedThisFrame = ComputeDistanceInDips(positionAfterAdvance, positionBeforeAdvance) < FollowSpringSettleDistanceInDips;
        if (isCloseToMouse && barelyMovedThisFrame)
        {
            cursorFollowSpring.SnapTo(followTargetPosition);
            isFollowSpringSettled = true;
            settledFollowTargetPosition = followTargetPosition;
        }
        else
        {
            isFollowSpringSettled = false;
        }

        buddyPosition = cursorFollowSpring.CurrentPosition;
    }

    private void StartFlight(OverlayDipPoint flightDestination, BuddyNavigationPhase flightPhase, double nowInSeconds)
    {
        activeFlightPath = new CursorFlightPath(buddyPosition, flightDestination);
        flightStartTimeInSeconds = nowInSeconds;
        navigationPhase = flightPhase;
    }

    /// <summary>Mac: animateBezierFlightArc, sampled by real elapsed time instead of counting 60 Hz timer ticks.</summary>
    private void AdvanceFlight(double nowInSeconds)
    {
        if (activeFlightPath is null)
        {
            ResumeFollowingCursor(nowInSeconds, shouldNotifyCompanion: true);
            return;
        }

        double elapsedFlightSeconds = nowInSeconds - flightStartTimeInSeconds;
        if (elapsedFlightSeconds >= activeFlightPath.FlightDuration.TotalSeconds)
        {
            buddyPosition = activeFlightPath.FlightEndPosition;
            buddyFlightScale = 1.0;
            activeFlightPath = null;

            if (navigationPhase == BuddyNavigationPhase.FlyingToTarget)
            {
                StartPointingAtTarget(nowInSeconds);
            }
            else
            {
                ResumeFollowingCursor(nowInSeconds, shouldNotifyCompanion: true);
            }

            return;
        }

        CursorFlightFrame flightFrame = activeFlightPath.SampleAtElapsedTime(TimeSpan.FromSeconds(elapsedFlightSeconds));
        buddyPosition = flightFrame.Position;
        buddyFlightScale = flightFrame.Scale;

        // During flight the triangle faces its direction of travel every frame, with no easing.
        triangleRotationTween.SnapTo(flightFrame.RotationDegrees);
    }

    /// <summary>Mac: startPointingAtElement.</summary>
    private void StartPointingAtTarget(double nowInSeconds)
    {
        navigationPhase = BuddyNavigationPhase.PointingAtTarget;

        // Turn back to the resting pointer angle now that the buddy has landed.
        triangleRotationTween.AnimateTo(CursorOverlayGeometry.DefaultTriangleRotationDegrees, TriangleRotationEaseSeconds, OverlayEasingCurve.EaseInOut, nowInSeconds);

        // The onboarding demo supplies its own comment; otherwise use a short random pointer phrase.
        string? customBubbleText = activePointingTarget?.CustomBubbleText;
        string pointerPhrase = string.IsNullOrWhiteSpace(customBubbleText)
            ? PointingBubblePhrases.PickNavigationPointerPhrase(Random.Shared.Next())
            : customBubbleText;

        pointingBubbleTextStream = StreamedBubbleText.CreateWithRandomPauses(pointerPhrase, nowInSeconds, Random.Shared);
        pointingBubbleFadeOutStartTimeInSeconds = pointingBubbleTextStream.CompletionTimeInSeconds + CursorOverlayGeometry.PointingBubbleHoldDuration.TotalSeconds;
        hasPointingBubbleFadeOutStarted = false;
        pointingBubbleOpacityTween.SnapTo(1);

        // The first character appears immediately, which is when the Mac triggers the scale pop.
        pointingBubblePopScaleSpring.Restart(PointingBubblePopStartScale, 1.0);
    }

    /// <summary>Mac: the hold and fade inside startPointingAtElement's completion, then startFlyingBackToCursor.</summary>
    private void AdvancePointing(double nowInSeconds)
    {
        if (!hasPointingBubbleFadeOutStarted && nowInSeconds >= pointingBubbleFadeOutStartTimeInSeconds)
        {
            hasPointingBubbleFadeOutStarted = true;
            pointingBubbleOpacityTween.AnimateTo(0, CursorOverlayGeometry.PointingBubbleFadeOutDuration.TotalSeconds, OverlayEasingCurve.EaseOut, pointingBubbleFadeOutStartTimeInSeconds);
        }

        double returnFlightStartTimeInSeconds = pointingBubbleFadeOutStartTimeInSeconds + CursorOverlayGeometry.PointingBubbleFadeOutDuration.TotalSeconds;
        if (nowInSeconds >= returnFlightStartTimeInSeconds)
        {
            StartFlyingBackToCursor(nowInSeconds);
        }
    }

    /// <summary>Mac: startFlyingBackToCursor. The destination is fixed now; the spring takes over after landing.</summary>
    private void StartFlyingBackToCursor(double nowInSeconds)
    {
        cursorPositionWhenReturnFlightStarted = latestCursorPositionInOverlayDips;
        ClearPointingBubble();
        StartFlight(CursorOverlayGeometry.ApplyCursorFollowOffset(latestCursorPositionInOverlayDips), BuddyNavigationPhase.FlyingBackToCursor, nowInSeconds);
    }

    /// <summary>Mac: finishNavigationAndResumeFollowing (and cancelNavigationAndResumeFollowing).</summary>
    private void ResumeFollowingCursor(double nowInSeconds, bool shouldNotifyCompanion)
    {
        navigationPhase = BuddyNavigationPhase.FollowingCursor;
        activeFlightPath = null;
        buddyFlightScale = 1.0;
        triangleRotationTween.AnimateTo(CursorOverlayGeometry.DefaultTriangleRotationDegrees, TriangleRotationEaseSeconds, OverlayEasingCurve.EaseInOut, nowInSeconds);
        ClearPointingBubble();

        // Follow from wherever the buddy is now, with no leftover velocity.
        cursorFollowSpring.SnapTo(buddyPosition);
        isFollowSpringSettled = false;

        OverlayPointingTarget? finishedPointingTarget = activePointingTarget;
        activePointingTarget = null;

        // Every field above is final before notifying: NotifyPointingAnimationFinished clears the
        // target, which synchronously re-enters the manager and anything else listening.
        // Only notify for the target this overlay was actually handling, and only if the companion
        // has not already cleared or replaced it (Mac: clearDetectedElementLocation).
        if (shouldNotifyCompanion
            && finishedPointingTarget is not null
            && ReferenceEquals(overlayState.DetectedElementPointingTarget, finishedPointingTarget))
        {
            overlayState.NotifyPointingAnimationFinished();
        }
    }

    private void ClearPointingBubble()
    {
        pointingBubbleTextStream = null;
        hasPointingBubbleFadeOutStarted = false;
        pointingBubbleOpacityTween.SnapTo(0);
        pointingBubblePopScaleSpring.Restart(1.0, 1.0);
    }

    // Welcome

    /// <summary>Mac: onAppear (isFirstAppearance branch) and startWelcomeAnimation.</summary>
    private void AdvanceWelcomeAnimation(double nowInSeconds)
    {
        if (!playsWelcomeAnimation || isWelcomeAnimationFinished)
        {
            return;
        }

        // The cursor fades in for two seconds, then the welcome text starts streaming.
        if (welcomeBubbleTextStream is null)
        {
            if (nowInSeconds < welcomeTextStartTimeInSeconds)
            {
                return;
            }

            welcomeBubbleTextStream = StreamedBubbleText.CreateWithFixedInterval(PointingBubblePhrases.WelcomeMessage, welcomeTextStartTimeInSeconds, WelcomeCharacterIntervalSeconds);
            welcomeBubbleOpacityTween.SnapTo(0);
            welcomeBubbleOpacityTween.AnimateTo(1, WelcomeBubbleFadeInSeconds, OverlayEasingCurve.EaseIn, welcomeTextStartTimeInSeconds);
        }

        // Hold the full text for two seconds, fade it out, then hand over to the onboarding demo.
        double welcomeBubbleFadeOutStartTimeInSeconds = welcomeBubbleTextStream.CompletionTimeInSeconds + WelcomeBubbleHoldSeconds;
        if (!hasWelcomeBubbleFadeOutStarted && nowInSeconds >= welcomeBubbleFadeOutStartTimeInSeconds)
        {
            hasWelcomeBubbleFadeOutStarted = true;
            welcomeBubbleOpacityTween.AnimateTo(0, WelcomeBubbleFadeOutSeconds, OverlayEasingCurve.EaseOut, welcomeBubbleFadeOutStartTimeInSeconds);
        }

        if (nowInSeconds >= welcomeBubbleFadeOutStartTimeInSeconds + WelcomeBubbleFadeOutSeconds)
        {
            isWelcomeAnimationFinished = true;
            WelcomeAnimationFinished?.Invoke();
        }
    }

    private bool IsWelcomeBubbleShowingText(double nowInSeconds)
    {
        return playsWelcomeAnimation
            && !isWelcomeAnimationFinished
            && welcomeBubbleTextStream is not null
            && welcomeBubbleTextStream.GetVisibleText(nowInSeconds).Length > 0;
    }

    // State observed from the companion

    private void ObserveVoiceStateChange(double nowInSeconds)
    {
        CompanionVoiceState currentVoiceState = overlayState.VoiceState;
        if (currentVoiceState == lastObservedVoiceState)
        {
            return;
        }

        lastObservedVoiceState = currentVoiceState;

        // All three shapes stay in the visual tree and cross-fade, as on the Mac, so a state change never pops.
        trianglePresenceTween.AnimateTo(ShowsTriangle(currentVoiceState) ? 1 : 0, TriangleCrossFadeSeconds, OverlayEasingCurve.EaseIn, nowInSeconds);
        waveformPresenceTween.AnimateTo(currentVoiceState == CompanionVoiceState.Listening ? 1 : 0, WaveformAndSpinnerCrossFadeSeconds, OverlayEasingCurve.EaseIn, nowInSeconds);
        spinnerPresenceTween.AnimateTo(currentVoiceState == CompanionVoiceState.Processing ? 1 : 0, WaveformAndSpinnerCrossFadeSeconds, OverlayEasingCurve.EaseIn, nowInSeconds);
    }

    private void ObserveOnboardingPromptChange(double nowInSeconds)
    {
        bool isOnboardingPromptRequested = overlayState.IsOnboardingPromptVisible;
        if (isOnboardingPromptRequested != lastObservedOnboardingPromptRequest)
        {
            lastObservedOnboardingPromptRequest = isOnboardingPromptRequested;
            if (isOnboardingPromptRequested)
            {
                onboardingPromptOpacityTween.AnimateTo(1, OnboardingPromptFadeInSeconds, OverlayEasingCurve.EaseIn, nowInSeconds);
            }
            else
            {
                onboardingPromptOpacityTween.AnimateTo(0, OnboardingPromptFadeOutSeconds, OverlayEasingCurve.EaseOut, nowInSeconds);
            }
        }

        // While fading out, keep the last text so the bubble does not shrink or empty mid-fade
        // if the companion clears the text at the same moment.
        if (isOnboardingPromptRequested)
        {
            displayedOnboardingPromptText = overlayState.OnboardingPromptText;
        }
    }

    private void ObserveResponseBubbleChange(double nowInSeconds)
    {
        bool isResponseBubbleRequested = overlayState.IsResponseBubbleVisible;
        if (isResponseBubbleRequested != lastObservedResponseBubbleRequest)
        {
            lastObservedResponseBubbleRequest = isResponseBubbleRequested;
            if (isResponseBubbleRequested)
            {
                responseBubbleOpacityTween.AnimateTo(1, ResponseBubbleFadeSeconds, OverlayEasingCurve.EaseIn, nowInSeconds);
            }
            else
            {
                responseBubbleOpacityTween.AnimateTo(0, ResponseBubbleFadeSeconds, OverlayEasingCurve.EaseOut, nowInSeconds);
            }
        }

        // Same reason as the onboarding prompt: the text freezes while the bubble fades out.
        if (isResponseBubbleRequested)
        {
            displayedResponseText = BuildDisplayedResponseText(overlayState.ResponseBubbleText);
        }
    }

    // Rendering

    private void RenderFrame(double nowInSeconds, double frameElapsedInSeconds)
    {
        bool buddyIsVisibleOnThisScreen = ComputeBuddyIsVisibleOnThisScreen();
        double cursorOpacity = cursorOpacityTween.Sample(nowInSeconds);
        double buddyOpacityOnThisScreen = buddyIsVisibleOnThisScreen ? cursorOpacity : 0;
        double triangleRotationDegrees = triangleRotationTween.Sample(nowInSeconds);

        triangleVisual.Apply(trianglePresenceTween.Sample(nowInSeconds) * buddyOpacityOnThisScreen, buddyPosition, triangleRotationDegrees, buddyFlightScale);
        waveformVisual.Apply(waveformPresenceTween.Sample(nowInSeconds) * buddyOpacityOnThisScreen, buddyPosition, nowInSeconds, overlayState.CurrentAudioPowerLevel);
        spinnerVisual.Apply(spinnerPresenceTween.Sample(nowInSeconds) * buddyOpacityOnThisScreen, buddyPosition, nowInSeconds);

        RenderWelcomeBubble(nowInSeconds);
        RenderOnboardingPromptBubble(nowInSeconds);
        RenderPointingBubble(nowInSeconds, frameElapsedInSeconds);
        RenderResponseBubble(nowInSeconds, buddyIsVisibleOnThisScreen);
    }

    /// <summary>
    /// Mac: buddyIsVisibleOnThisScreen. While following, the buddy shows only on the monitor with
    /// the mouse, and hides everywhere while another monitor's overlay is pointing, so exactly one
    /// buddy is ever on screen. While flying or pointing it always shows on this monitor.
    /// </summary>
    private bool ComputeBuddyIsVisibleOnThisScreen()
    {
        if (navigationPhase != BuddyNavigationPhase.FollowingCursor)
        {
            return true;
        }

        if (overlayState.DetectedElementPointingTarget is not null)
        {
            return false;
        }

        return isCursorOnThisScreen;
    }

    private void RenderWelcomeBubble(double nowInSeconds)
    {
        // Mac: shown only on the screen with the mouse, while the welcome runs and has text.
        bool isWelcomeBubbleActive = playsWelcomeAnimation
            && !isWelcomeAnimationFinished
            && isCursorOnThisScreen
            && welcomeBubbleTextStream is not null;

        string welcomeText = isWelcomeBubbleActive && welcomeBubbleTextStream is not null
            ? welcomeBubbleTextStream.GetVisibleText(nowInSeconds)
            : string.Empty;
        double welcomeBubbleOpacity = isWelcomeBubbleActive ? welcomeBubbleOpacityTween.Sample(nowInSeconds) : 0;

        welcomeBubbleVisual.Apply(welcomeText, welcomeBubbleOpacity, buddyPosition, popScale: 1.0, windowDpiScale);
    }

    private void RenderOnboardingPromptBubble(double nowInSeconds)
    {
        // Mac: shown only on the screen with the mouse; its opacity is shared, its visibility per screen.
        double onboardingPromptOpacity = onboardingPromptOpacityTween.Sample(nowInSeconds);
        double onboardingPromptOpacityOnThisScreen = isCursorOnThisScreen ? onboardingPromptOpacity : 0;

        onboardingPromptBubbleVisual.Apply(displayedOnboardingPromptText, onboardingPromptOpacityOnThisScreen, buddyPosition, popScale: 1.0, windowDpiScale);
    }

    private void RenderPointingBubble(double nowInSeconds, double frameElapsedInSeconds)
    {
        if (navigationPhase != BuddyNavigationPhase.PointingAtTarget || pointingBubbleTextStream is null)
        {
            pointingBubbleVisual.Apply(string.Empty, 0, buddyPosition, popScale: 1.0, windowDpiScale);
            return;
        }

        string pointingBubbleText = pointingBubbleTextStream.GetVisibleText(nowInSeconds);
        double pointingBubblePopScale = pointingBubblePopScaleSpring.Advance(frameElapsedInSeconds);
        double pointingBubbleOpacity = pointingBubbleOpacityTween.Sample(nowInSeconds);

        pointingBubbleVisual.Apply(pointingBubbleText, pointingBubbleOpacity, buddyPosition, pointingBubblePopScale, windowDpiScale);
    }

    private void RenderResponseBubble(double nowInSeconds, bool buddyIsVisibleOnThisScreen)
    {
        // The reply sits beside the buddy, so it shows on the same monitor the buddy does.
        double responseBubbleOpacity = responseBubbleOpacityTween.Sample(nowInSeconds);
        double responseBubbleOpacityOnThisScreen = buddyIsVisibleOnThisScreen ? responseBubbleOpacity : 0;

        responseBubbleVisual.Apply(displayedResponseText, responseBubbleOpacityOnThisScreen, buddyPosition, workAreaInOverlayDips, windowDpiScale);
    }

    // Helpers

    private Rect ComputeWorkAreaInOverlayDips()
    {
        PhysicalPixelRectangle workAreaInPhysicalPixels = overlayMonitor.WorkAreaInPhysicalPixels;
        Rect fullOverlayRectangle = new(0, 0, overlayWidthInDips, overlayHeightInDips);

        if (workAreaInPhysicalPixels.Width <= 0 || workAreaInPhysicalPixels.Height <= 0)
        {
            return fullOverlayRectangle;
        }

        OverlayDipPoint workAreaTopLeft = ScreenCoordinateMapper.MapPhysicalPixelsToOverlayDips(
            new PhysicalPixelPoint(workAreaInPhysicalPixels.Left, workAreaInPhysicalPixels.Top),
            overlayMonitor.BoundsInPhysicalPixels,
            windowDpiScale);
        OverlayDipPoint workAreaBottomRight = ScreenCoordinateMapper.MapPhysicalPixelsToOverlayDips(
            new PhysicalPixelPoint(workAreaInPhysicalPixels.Right, workAreaInPhysicalPixels.Bottom),
            overlayMonitor.BoundsInPhysicalPixels,
            windowDpiScale);

        Rect workAreaRectangle = new(new Point(workAreaTopLeft.X, workAreaTopLeft.Y), new Point(workAreaBottomRight.X, workAreaBottomRight.Y));
        workAreaRectangle.Intersect(fullOverlayRectangle);
        return workAreaRectangle.IsEmpty ? fullOverlayRectangle : workAreaRectangle;
    }

    private static bool ShowsTriangle(CompanionVoiceState voiceState)
    {
        return voiceState == CompanionVoiceState.Idle || voiceState == CompanionVoiceState.Responding;
    }

    private static string BuildDisplayedResponseText(string responseBubbleText)
    {
        return string.IsNullOrEmpty(responseBubbleText) ? EmptyResponsePlaceholderText : responseBubbleText;
    }

    private static double ComputeDistanceInDips(OverlayDipPoint firstPoint, OverlayDipPoint secondPoint)
    {
        double horizontalDistance = firstPoint.X - secondPoint.X;
        double verticalDistance = firstPoint.Y - secondPoint.Y;
        return Math.Sqrt((horizontalDistance * horizontalDistance) + (verticalDistance * verticalDistance));
    }
}
