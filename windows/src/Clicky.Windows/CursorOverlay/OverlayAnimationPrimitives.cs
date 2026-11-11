using System.Globalization;
using Clicky.Core.Overlay;
using Clicky.Core.Pointing;

namespace Clicky.Windows.CursorOverlay;

/// <summary>Timing curves for overlay fades and turns.</summary>
internal enum OverlayEasingCurve
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
}

internal static class OverlayEasing
{
    /// <summary>
    /// Simple polynomial stand-ins for SwiftUI's .easeIn, .easeOut and .easeInOut. At the
    /// durations the overlay uses (0.15 to 2 seconds) they are visually indistinguishable from
    /// the exact cubic bezier curves and far easier to read.
    /// </summary>
    public static double Apply(OverlayEasingCurve easingCurve, double linearProgress)
    {
        double clampedProgress = Math.Clamp(linearProgress, 0, 1);

        return easingCurve switch
        {
            OverlayEasingCurve.EaseIn => clampedProgress * clampedProgress,
            OverlayEasingCurve.EaseOut => 1 - ((1 - clampedProgress) * (1 - clampedProgress)),
            OverlayEasingCurve.EaseInOut => clampedProgress * clampedProgress * (3 - (2 * clampedProgress)),
            _ => clampedProgress,
        };
    }
}

/// <summary>
/// A number that eases toward a target over a fixed duration, sampled once per frame with the
/// overlay clock. This replaces SwiftUI's implicit .animation modifiers: the overlay computes
/// every value itself so it can skip WPF entirely when nothing is moving.
/// </summary>
internal sealed class EasedValueTween
{
    private double animationStartValue;
    private double animationStartTimeInSeconds;
    private double animationDurationInSeconds;
    private OverlayEasingCurve animationEasingCurve;

    public EasedValueTween(double initialValue)
    {
        CurrentValue = initialValue;
        TargetValue = initialValue;
    }

    public double CurrentValue { get; private set; }

    public double TargetValue { get; private set; }

    public bool IsAnimating { get; private set; }

    public void SnapTo(double snapValue)
    {
        CurrentValue = snapValue;
        TargetValue = snapValue;
        IsAnimating = false;
    }

    /// <summary>Starts easing from the current value. Asking for the target it already has does nothing, so callers can repeat the request every frame.</summary>
    public void AnimateTo(double newTargetValue, double durationInSeconds, OverlayEasingCurve easingCurve, double animationStartTimeInSeconds)
    {
        if (newTargetValue == TargetValue)
        {
            return;
        }

        if (durationInSeconds <= 0)
        {
            SnapTo(newTargetValue);
            return;
        }

        animationStartValue = CurrentValue;
        TargetValue = newTargetValue;
        this.animationStartTimeInSeconds = animationStartTimeInSeconds;
        animationDurationInSeconds = durationInSeconds;
        animationEasingCurve = easingCurve;
        IsAnimating = true;
    }

    public double Sample(double nowInSeconds)
    {
        if (!IsAnimating)
        {
            return CurrentValue;
        }

        double linearProgress = (nowInSeconds - animationStartTimeInSeconds) / animationDurationInSeconds;
        if (linearProgress >= 1)
        {
            CurrentValue = TargetValue;
            IsAnimating = false;
            return CurrentValue;
        }

        double easedProgress = OverlayEasing.Apply(animationEasingCurve, linearProgress);
        CurrentValue = animationStartValue + ((TargetValue - animationStartValue) * easedProgress);
        return CurrentValue;
    }
}

/// <summary>
/// Bubble text that appears one character at a time on a schedule fixed up front, so the
/// per-frame code only has to ask "what is visible now". Characters are text elements
/// (what a person sees as one character), matching how Swift counts String characters,
/// so an emoji or accented letter is never cut in half.
/// </summary>
internal sealed class StreamedBubbleText
{
    private readonly string[] textElements;
    private readonly double[] textElementRevealTimesInSeconds;
    private int lastVisibleTextElementCount;
    private string lastVisibleText = string.Empty;

    private StreamedBubbleText(string[] textElements, double[] textElementRevealTimesInSeconds, double completionTimeInSeconds)
    {
        this.textElements = textElements;
        this.textElementRevealTimesInSeconds = textElementRevealTimesInSeconds;
        CompletionTimeInSeconds = completionTimeInSeconds;
    }

    /// <summary>When the streaming is over and the hold period starts. Mac: when the streaming callback fires onComplete.</summary>
    public double CompletionTimeInSeconds { get; }

    /// <summary>
    /// Welcome style (Mac startWelcomeAnimation): a repeating timer adds one character per tick,
    /// so the first character waits one interval, and the timer needs one more tick after the
    /// last character to notice it is done.
    /// </summary>
    public static StreamedBubbleText CreateWithFixedInterval(string fullText, double streamStartTimeInSeconds, double characterIntervalInSeconds)
    {
        string[] textElements = SplitIntoTextElements(fullText);
        double[] revealTimesInSeconds = new double[textElements.Length];

        for (int textElementIndex = 0; textElementIndex < textElements.Length; textElementIndex++)
        {
            revealTimesInSeconds[textElementIndex] = streamStartTimeInSeconds + (characterIntervalInSeconds * (textElementIndex + 1));
        }

        double completionTimeInSeconds = streamStartTimeInSeconds + (characterIntervalInSeconds * (textElements.Length + 1));
        return new StreamedBubbleText(textElements, revealTimesInSeconds, completionTimeInSeconds);
    }

    /// <summary>
    /// Pointing style (Mac streamNavigationBubbleCharacter): the first character appears at once,
    /// then each character is followed by a random 30 to 60 ms pause, including the last one.
    /// </summary>
    public static StreamedBubbleText CreateWithRandomPauses(string fullText, double streamStartTimeInSeconds, Random pauseRandomSource)
    {
        string[] textElements = SplitIntoTextElements(fullText);
        double[] revealTimesInSeconds = new double[textElements.Length];
        double nextRevealTimeInSeconds = streamStartTimeInSeconds;

        for (int textElementIndex = 0; textElementIndex < textElements.Length; textElementIndex++)
        {
            revealTimesInSeconds[textElementIndex] = nextRevealTimeInSeconds;

            double pauseAfterCharacterInSeconds = PointingBubblePhrases.MinimumCharacterDelayInSeconds
                + (pauseRandomSource.NextDouble() * (PointingBubblePhrases.MaximumCharacterDelayInSeconds - PointingBubblePhrases.MinimumCharacterDelayInSeconds));
            nextRevealTimeInSeconds += pauseAfterCharacterInSeconds;
        }

        return new StreamedBubbleText(textElements, revealTimesInSeconds, nextRevealTimeInSeconds);
    }

    public string GetVisibleText(double nowInSeconds)
    {
        int visibleTextElementCount = 0;
        while (visibleTextElementCount < textElementRevealTimesInSeconds.Length
            && textElementRevealTimesInSeconds[visibleTextElementCount] <= nowInSeconds)
        {
            visibleTextElementCount++;
        }

        // Only build a new string when another character appeared, not on every frame.
        if (visibleTextElementCount != lastVisibleTextElementCount)
        {
            lastVisibleTextElementCount = visibleTextElementCount;
            lastVisibleText = string.Concat(textElements.Take(visibleTextElementCount));
        }

        return lastVisibleText;
    }

    private static string[] SplitIntoTextElements(string fullText)
    {
        List<string> textElements = [];
        TextElementEnumerator textElementEnumerator = StringInfo.GetTextElementEnumerator(fullText);
        while (textElementEnumerator.MoveNext())
        {
            textElements.Add(textElementEnumerator.GetTextElement());
        }

        return textElements.ToArray();
    }
}

/// <summary>
/// The springy pop of the pointing bubble: Mac .spring(response: 0.4, dampingFraction: 0.6) on its
/// scaleEffect, from 0.5x to 1x. Core's damped spring already integrates exactly this kind of
/// spring for the cursor, so this reuses it and reads only its X axis instead of re-deriving the math.
/// </summary>
internal sealed class BubblePopScaleSpring
{
    private const double PopSpringResponseInSeconds = 0.4;
    private const double PopSpringDampingFraction = 0.6;
    private const double SettleThreshold = 0.001;

    private readonly CursorFollowSpring scaleSpring = new(PopSpringResponseInSeconds, PopSpringDampingFraction);
    private double targetScale = 1.0;

    public BubblePopScaleSpring()
    {
        scaleSpring.SnapTo(new OverlayDipPoint(1.0, 0));
    }

    public double CurrentScale => scaleSpring.CurrentPosition.X;

    public bool IsSettled { get; private set; } = true;

    public void Restart(double startScale, double newTargetScale)
    {
        scaleSpring.SnapTo(new OverlayDipPoint(startScale, 0));
        targetScale = newTargetScale;
        IsSettled = startScale == newTargetScale;
    }

    public double Advance(double elapsedSeconds)
    {
        if (IsSettled)
        {
            return CurrentScale;
        }

        double previousScale = CurrentScale;
        scaleSpring.Advance(new OverlayDipPoint(targetScale, 0), elapsedSeconds);

        bool isAtTargetScale = Math.Abs(CurrentScale - targetScale) < SettleThreshold;
        bool barelyMovedThisFrame = Math.Abs(CurrentScale - previousScale) < SettleThreshold;
        if (isAtTargetScale && barelyMovedThisFrame)
        {
            scaleSpring.SnapTo(new OverlayDipPoint(targetScale, 0));
            IsSettled = true;
        }

        return CurrentScale;
    }
}
