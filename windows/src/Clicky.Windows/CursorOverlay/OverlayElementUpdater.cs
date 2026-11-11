using System.Windows;
using System.Windows.Media;

namespace Clicky.Windows.CursorOverlay;

/// <summary>
/// Pushes opacity and position to one WPF element only when they really changed.
/// Every overlay is a full-monitor layered window, and any property change makes WPF redraw
/// and re-upload it, so per-frame code has to stay silent when nothing moved. Elements at zero
/// opacity are switched to Hidden and stop receiving position updates until they show again.
/// </summary>
internal sealed class OverlayElementUpdater
{
    private const double OpacityChangeThreshold = 0.002;
    private const double TranslationChangeThresholdInDips = 0.01;

    private readonly UIElement updatedElement;
    private readonly TranslateTransform positionTransform;
    private double appliedOpacity;
    private double appliedTranslationX = double.NaN;
    private double appliedTranslationY = double.NaN;

    public OverlayElementUpdater(UIElement updatedElement, TranslateTransform positionTransform)
    {
        this.updatedElement = updatedElement;
        this.positionTransform = positionTransform;

        updatedElement.Opacity = 0;
        updatedElement.Visibility = Visibility.Hidden;
        appliedOpacity = 0;
    }

    public bool IsShowing => appliedOpacity > 0;

    /// <returns>True when the element is showing after this call, so the caller should also update its other properties.</returns>
    public bool ApplyOpacity(double requestedOpacity)
    {
        double clampedOpacity = Math.Clamp(requestedOpacity, 0, 1);

        // Values this small are invisible; treating them as zero lets the element go Hidden at the end of a fade.
        if (clampedOpacity < OpacityChangeThreshold)
        {
            clampedOpacity = 0;
        }

        if (clampedOpacity == appliedOpacity)
        {
            return IsShowing;
        }

        // Skip imperceptible steps mid-fade, but always land exactly on fully shown or fully hidden.
        bool isFadeEndpoint = clampedOpacity == 0 || clampedOpacity == 1;
        if (!isFadeEndpoint && Math.Abs(clampedOpacity - appliedOpacity) < OpacityChangeThreshold)
        {
            return IsShowing;
        }

        bool wasShowing = IsShowing;
        updatedElement.Opacity = clampedOpacity;
        appliedOpacity = clampedOpacity;

        if (IsShowing != wasShowing)
        {
            updatedElement.Visibility = IsShowing ? Visibility.Visible : Visibility.Hidden;
        }

        return IsShowing;
    }

    public void ApplyTranslation(double translationX, double translationY)
    {
        if (!IsShowing)
        {
            return;
        }

        if (double.IsNaN(appliedTranslationX) || Math.Abs(translationX - appliedTranslationX) > TranslationChangeThresholdInDips)
        {
            positionTransform.X = translationX;
            appliedTranslationX = translationX;
        }

        if (double.IsNaN(appliedTranslationY) || Math.Abs(translationY - appliedTranslationY) > TranslationChangeThresholdInDips)
        {
            positionTransform.Y = translationY;
            appliedTranslationY = translationY;
        }
    }
}
