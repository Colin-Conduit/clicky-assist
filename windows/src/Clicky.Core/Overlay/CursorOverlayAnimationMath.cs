using Clicky.Core.Pointing;

namespace Clicky.Core.Overlay;

/// <summary>One sampled frame of a cursor flight.</summary>
/// <param name="Position">Triangle center in overlay DIPs.</param>
/// <param name="RotationDegrees">Clockwise rotation; the triangle's tip points up at 0 degrees.</param>
/// <param name="Scale">Size multiplier; peaks at about 1.3 halfway through the flight.</param>
public readonly record struct CursorFlightFrame(OverlayDipPoint Position, double RotationDegrees, double Scale);

/// <summary>
/// Layout constants for the blue cursor, all in DIPs (equal to points on the Mac).
/// Values come straight from OverlayWindow.swift.
/// </summary>
public static class CursorOverlayGeometry
{
    /// <summary>The buddy trails the real mouse pointer by this offset so it never covers it.</summary>
    public const double CursorFollowOffsetX = 35;
    public const double CursorFollowOffsetY = 25;

    /// <summary>Resting rotation that makes the triangle look like a pointer.</summary>
    public const double DefaultTriangleRotationDegrees = -35;

    public const double TriangleSizeInDips = 16;

    /// <summary>The buddy lands beside the element rather than on top of it.</summary>
    public const double NavigationTargetOffsetX = 8;
    public const double NavigationTargetOffsetY = 12;
    public const double NavigationTargetEdgePadding = 20;

    /// <summary>Moving the mouse farther than this during the return flight snaps the buddy back to following.</summary>
    public const double ReturnFlightCancelDistanceInDips = 100;

    /// <summary>How long the pointing bubble stays fully visible after its text finishes streaming.</summary>
    public static readonly TimeSpan PointingBubbleHoldDuration = TimeSpan.FromSeconds(3);

    /// <summary>Fade-out time for the pointing bubble before the return flight starts.</summary>
    public static readonly TimeSpan PointingBubbleFadeOutDuration = TimeSpan.FromSeconds(0.5);

    public static OverlayDipPoint ApplyCursorFollowOffset(OverlayDipPoint mousePositionInOverlayDips)
    {
        return new OverlayDipPoint(mousePositionInOverlayDips.X + CursorFollowOffsetX, mousePositionInOverlayDips.Y + CursorFollowOffsetY);
    }

    /// <summary>
    /// Offsets the element location 8 right and 12 down, then keeps it at least 20 DIPs
    /// inside the overlay edges.
    /// </summary>
    public static OverlayDipPoint ComputeNavigationDestination(
        OverlayDipPoint elementLocationInOverlayDips,
        double overlayWidthInDips,
        double overlayHeightInDips)
    {
        double offsetDestinationX = elementLocationInOverlayDips.X + NavigationTargetOffsetX;
        double offsetDestinationY = elementLocationInOverlayDips.Y + NavigationTargetOffsetY;

        double clampedDestinationX = Math.Max(NavigationTargetEdgePadding, Math.Min(offsetDestinationX, overlayWidthInDips - NavigationTargetEdgePadding));
        double clampedDestinationY = Math.Max(NavigationTargetEdgePadding, Math.Min(offsetDestinationY, overlayHeightInDips - NavigationTargetEdgePadding));

        return new OverlayDipPoint(clampedDestinationX, clampedDestinationY);
    }
}

/// <summary>
/// Quadratic bezier flight from the cursor to a target, ported from
/// BlueCursorView.animateBezierFlightArc. The arc bows upward, the triangle turns to
/// face its direction of travel, and it swells to 1.3x at the midpoint.
/// </summary>
public sealed class CursorFlightPath
{
    public const double DistanceDivisorForDurationInDips = 800;
    public const double MinimumFlightDurationInSeconds = 0.6;
    public const double MaximumFlightDurationInSeconds = 1.4;
    public const double ArcHeightFractionOfDistance = 0.2;
    public const double MaximumArcHeightInDips = 80;
    public const double MidpointScaleBoost = 0.3;

    public CursorFlightPath(OverlayDipPoint flightStartPosition, OverlayDipPoint flightEndPosition)
    {
        FlightStartPosition = flightStartPosition;
        FlightEndPosition = flightEndPosition;

        double flightDistance = Math.Sqrt(
            Math.Pow(flightEndPosition.X - flightStartPosition.X, 2) +
            Math.Pow(flightEndPosition.Y - flightStartPosition.Y, 2));

        double flightDurationInSeconds = Math.Clamp(
            flightDistance / DistanceDivisorForDurationInDips,
            MinimumFlightDurationInSeconds,
            MaximumFlightDurationInSeconds);
        FlightDuration = TimeSpan.FromSeconds(flightDurationInSeconds);

        double arcHeight = Math.Min(flightDistance * ArcHeightFractionOfDistance, MaximumArcHeightInDips);
        double midpointX = (flightStartPosition.X + flightEndPosition.X) / 2.0;
        double midpointY = (flightStartPosition.Y + flightEndPosition.Y) / 2.0;

        // Negative Y is up on screen, so subtracting lifts the control point and the arc.
        BezierControlPoint = new OverlayDipPoint(midpointX, midpointY - arcHeight);
    }

    public OverlayDipPoint FlightStartPosition { get; }
    public OverlayDipPoint FlightEndPosition { get; }
    public OverlayDipPoint BezierControlPoint { get; }
    public TimeSpan FlightDuration { get; }

    /// <summary>Samples the flight at elapsed time since it started.</summary>
    public CursorFlightFrame SampleAtElapsedTime(TimeSpan elapsedFlightTime)
    {
        double linearProgress = FlightDuration.TotalSeconds <= 0
            ? 1
            : elapsedFlightTime.TotalSeconds / FlightDuration.TotalSeconds;
        return SampleAtLinearProgress(linearProgress);
    }

    /// <summary>Samples the flight at linear progress 0 (start) to 1 (landed).</summary>
    public CursorFlightFrame SampleAtLinearProgress(double linearProgress)
    {
        double clampedLinearProgress = Math.Clamp(linearProgress, 0, 1);

        // Smoothstep ease-in-out: 3t^2 - 2t^3.
        double easedProgress = clampedLinearProgress * clampedLinearProgress * (3.0 - (2.0 * clampedLinearProgress));
        double oneMinusEasedProgress = 1.0 - easedProgress;

        // B(t) = (1-t)^2 P0 + 2(1-t)t P1 + t^2 P2
        double bezierX = (oneMinusEasedProgress * oneMinusEasedProgress * FlightStartPosition.X)
            + (2.0 * oneMinusEasedProgress * easedProgress * BezierControlPoint.X)
            + (easedProgress * easedProgress * FlightEndPosition.X);
        double bezierY = (oneMinusEasedProgress * oneMinusEasedProgress * FlightStartPosition.Y)
            + (2.0 * oneMinusEasedProgress * easedProgress * BezierControlPoint.Y)
            + (easedProgress * easedProgress * FlightEndPosition.Y);

        // B'(t) = 2(1-t)(P1-P0) + 2t(P2-P1); the +90 turns "facing right" into "tip pointing along travel".
        double tangentX = (2.0 * oneMinusEasedProgress * (BezierControlPoint.X - FlightStartPosition.X))
            + (2.0 * easedProgress * (FlightEndPosition.X - BezierControlPoint.X));
        double tangentY = (2.0 * oneMinusEasedProgress * (BezierControlPoint.Y - FlightStartPosition.Y))
            + (2.0 * easedProgress * (FlightEndPosition.Y - BezierControlPoint.Y));
        double rotationDegrees = (Math.Atan2(tangentY, tangentX) * (180.0 / Math.PI)) + 90.0;

        double scale = 1.0 + (Math.Sin(clampedLinearProgress * Math.PI) * MidpointScaleBoost);

        return new CursorFlightFrame(new OverlayDipPoint(bezierX, bezierY), rotationDegrees, scale);
    }
}

/// <summary>
/// Damped spring that makes the buddy trail the mouse. Approximates SwiftUI's
/// .spring(response: 0.2, dampingFraction: 0.6) used on the Mac: stiffness
/// (2 pi / response)^2 and damping 4 pi * dampingFraction / response for unit mass,
/// integrated in small fixed substeps so it stays stable at any frame rate.
/// </summary>
public sealed class CursorFollowSpring
{
    public const double DefaultResponseInSeconds = 0.2;
    public const double DefaultDampingFraction = 0.6;
    private const double MaximumIntegrationStepInSeconds = 1.0 / 240.0;

    private readonly double springStiffness;
    private readonly double springDamping;
    private double velocityX;
    private double velocityY;

    public CursorFollowSpring(double responseInSeconds = DefaultResponseInSeconds, double dampingFraction = DefaultDampingFraction)
    {
        if (responseInSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(responseInSeconds), "Spring response must be positive.");
        }

        double angularFrequency = 2.0 * Math.PI / responseInSeconds;
        springStiffness = angularFrequency * angularFrequency;
        springDamping = 4.0 * Math.PI * dampingFraction / responseInSeconds;
    }

    public OverlayDipPoint CurrentPosition { get; private set; }

    /// <summary>Jumps straight to a position with no motion, for example when the overlay first appears.</summary>
    public void SnapTo(OverlayDipPoint snapPosition)
    {
        CurrentPosition = snapPosition;
        velocityX = 0;
        velocityY = 0;
    }

    public OverlayDipPoint Advance(OverlayDipPoint targetPosition, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0)
        {
            return CurrentPosition;
        }

        // Cap very long gaps (the overlay was hidden, the machine slept) so the spring settles instead of exploding.
        double remainingSeconds = Math.Min(elapsedSeconds, 0.25);
        double positionX = CurrentPosition.X;
        double positionY = CurrentPosition.Y;

        while (remainingSeconds > 0)
        {
            double integrationStepInSeconds = Math.Min(remainingSeconds, MaximumIntegrationStepInSeconds);

            double accelerationX = (springStiffness * (targetPosition.X - positionX)) - (springDamping * velocityX);
            double accelerationY = (springStiffness * (targetPosition.Y - positionY)) - (springDamping * velocityY);

            velocityX += accelerationX * integrationStepInSeconds;
            velocityY += accelerationY * integrationStepInSeconds;
            positionX += velocityX * integrationStepInSeconds;
            positionY += velocityY * integrationStepInSeconds;

            remainingSeconds -= integrationStepInSeconds;
        }

        CurrentPosition = new OverlayDipPoint(positionX, positionY);
        return CurrentPosition;
    }
}

/// <summary>
/// Bar heights for the five-bar listening waveform, ported from BlueCursorWaveformView:
/// a gentle idle pulse plus a height that reacts to the microphone level.
/// </summary>
public static class ListeningWaveformGeometry
{
    public const int BarCount = 5;
    public const double BarWidthInDips = 2;
    public const double BarSpacingInDips = 2;
    public const double BarCornerRadiusInDips = 1.5;

    private static readonly double[] ListeningBarProfile = [0.4, 0.7, 1.0, 0.7, 0.4];

    public static double ComputeBarHeightInDips(int barIndex, double animationTimeInSeconds, double audioPowerLevel)
    {
        double animationPhase = (animationTimeInSeconds * 3.6) + (barIndex * 0.35);
        double normalizedAudioPowerLevel = Math.Max(audioPowerLevel - 0.008, 0);
        double easedAudioPowerLevel = Math.Pow(Math.Min(normalizedAudioPowerLevel * 2.85, 1), 0.76);
        double reactiveHeight = easedAudioPowerLevel * 10 * ListeningBarProfile[barIndex];
        double idlePulse = (Math.Sin(animationPhase) + 1) / 2 * 1.5;
        return 3 + reactiveHeight + idlePulse;
    }
}

/// <summary>
/// Pointing bubble phrases. The Mac strings end in exclamation points; the Windows
/// UI copy rules do not allow them, so the punctuation is dropped here.
/// </summary>
public static class PointingBubblePhrases
{
    public static IReadOnlyList<string> NavigationPointerPhrases { get; } =
    [
        "right here",
        "this one",
        "over here",
        "click this",
        "here it is",
        "found it",
    ];

    /// <summary>Mac: "hey! i'm clicky".</summary>
    public const string WelcomeMessage = "hey, i'm clicky";

    /// <summary>Delay between characters when streaming bubble text, chosen per character in this range.</summary>
    public const double MinimumCharacterDelayInSeconds = 0.03;
    public const double MaximumCharacterDelayInSeconds = 0.06;

    public static string PickNavigationPointerPhrase(int randomIndexSeed)
    {
        int phraseIndex = Math.Abs(randomIndexSeed % NavigationPointerPhrases.Count);
        return NavigationPointerPhrases[phraseIndex];
    }

    /// <summary>Onboarding hint streamed after the demo. Mac: "press control + option and introduce yourself".</summary>
    public static string BuildOnboardingPromptText(string pushToTalkShortcutDisplayText)
    {
        return $"press {pushToTalkShortcutDisplayText.ToLowerInvariant()} and introduce yourself";
    }
}
