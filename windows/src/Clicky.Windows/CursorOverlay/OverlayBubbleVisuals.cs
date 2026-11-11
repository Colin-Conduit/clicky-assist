using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Clicky.Core.Pointing;

namespace Clicky.Windows.CursorOverlay;

/// <summary>
/// The small blue speech bubble beside the buddy: the welcome text, the pointing phrase and
/// the onboarding prompt all use it. Port of the three identical Text bubbles in BlueCursorView.
/// </summary>
internal sealed class CompanionBlueBubbleVisual
{
    private const double BubbleCornerRadiusInDips = 6;
    private const double BubbleHorizontalPaddingInDips = 8;
    private const double BubbleVerticalPaddingInDips = 4;
    private const double BubbleFontSizeInDips = 11;

    /// <summary>Mac: .position(x: cursorPosition.x + 10 + (bubbleSize.width / 2), y: cursorPosition.y + 18).</summary>
    private const double BubbleLeftEdgeOffsetFromBuddyInDips = 10;
    private const double BubbleVerticalCenterOffsetFromBuddyInDips = 18;

    private const double RestingGlowOpacity = 0.5;
    private const double RestingGlowBlurRadiusInDips = 6;

    /// <summary>Mac pointing bubble: glow opacity 0.5 + (1 - scale) * 1.0 and radius 6 + (1 - scale) * 16, a bright flash that settles as it pops in.</summary>
    private const double PopGlowOpacityBoostPerUnitOfMissingScale = 1.0;
    private const double PopGlowBlurBoostPerUnitOfMissingScale = 16;
    private const double PopScaleChangeThreshold = 0.0005;

    private readonly Grid bubbleRoot;
    private readonly TextBlock bubbleTextBlock;
    private readonly DropShadowEffect bubbleGlowEffect;
    private readonly ScaleTransform bubblePopScaleTransform = new(1, 1);
    private readonly TranslateTransform bubblePositionTransform = new();
    private readonly OverlayElementUpdater bubbleUpdater;
    private string? appliedBubbleText;
    private Size measuredBubbleSize;
    private double appliedPopScale = 1.0;

    public CompanionBlueBubbleVisual()
    {
        bubbleGlowEffect = new DropShadowEffect
        {
            Color = OverlayVisualStyle.OverlayCursorBlueColor,
            ShadowDepth = 0,
            BlurRadius = RestingGlowBlurRadiusInDips,
            Opacity = RestingGlowOpacity,
        };

        // The glow sits on the background shape only, as on the Mac (.background(...shadow)),
        // so the text itself stays sharp and never goes through the effect pipeline.
        Border bubbleBackground = new()
        {
            Background = OverlayVisualStyle.OverlayCursorBlueBrush,
            CornerRadius = new CornerRadius(BubbleCornerRadiusInDips),
            Effect = bubbleGlowEffect,
            IsHitTestVisible = false,
        };

        bubbleTextBlock = new TextBlock
        {
            Foreground = OverlayVisualStyle.BlueBubbleTextBrush,
            FontFamily = OverlayVisualStyle.CompanionBubbleFontFamily,
            FontSize = BubbleFontSizeInDips,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(BubbleHorizontalPaddingInDips, BubbleVerticalPaddingInDips, BubbleHorizontalPaddingInDips, BubbleVerticalPaddingInDips),
            IsHitTestVisible = false,
        };

        // Pop scale happens around the bubble's own center (SwiftUI scaleEffect), then the bubble moves into place.
        TransformGroup bubbleTransforms = new();
        bubbleTransforms.Children.Add(bubblePopScaleTransform);
        bubbleTransforms.Children.Add(bubblePositionTransform);

        bubbleRoot = new Grid
        {
            RenderTransform = bubbleTransforms,
            IsHitTestVisible = false,
        };
        bubbleRoot.Children.Add(bubbleBackground);
        bubbleRoot.Children.Add(bubbleTextBlock);

        bubbleUpdater = new OverlayElementUpdater(bubbleRoot, bubblePositionTransform);
    }

    public UIElement RootElement => bubbleRoot;

    /// <summary>Forces a fresh size measurement next time, for example after the window's DPI changed.</summary>
    public void InvalidateMeasuredSize()
    {
        appliedBubbleText = null;
    }

    /// <param name="popScale">1 at rest; the pointing bubble passes its spring value while it pops in.</param>
    public void Apply(string bubbleText, double opacity, OverlayDipPoint buddyPosition, double popScale, double windowDpiScale)
    {
        // An empty bubble is never shown (Mac: the bubble only exists once its text is non-empty).
        double effectiveOpacity = string.IsNullOrEmpty(bubbleText) ? 0 : opacity;
        if (!bubbleUpdater.ApplyOpacity(effectiveOpacity))
        {
            return;
        }

        if (!string.Equals(appliedBubbleText, bubbleText, StringComparison.Ordinal))
        {
            bubbleTextBlock.Text = bubbleText;
            appliedBubbleText = bubbleText;

            // Measure right away so this frame's position already uses the new width.
            bubbleRoot.Measure(OverlayVisualStyle.UnconstrainedMeasureSize);
            measuredBubbleSize = bubbleRoot.DesiredSize;
            bubblePopScaleTransform.CenterX = measuredBubbleSize.Width / 2;
            bubblePopScaleTransform.CenterY = measuredBubbleSize.Height / 2;
        }

        if (Math.Abs(popScale - appliedPopScale) > PopScaleChangeThreshold)
        {
            bubblePopScaleTransform.ScaleX = popScale;
            bubblePopScaleTransform.ScaleY = popScale;

            // The spring overshoots past 1, so keep the glow inside valid ranges.
            double missingScale = 1.0 - popScale;
            bubbleGlowEffect.Opacity = Math.Clamp(RestingGlowOpacity + (missingScale * PopGlowOpacityBoostPerUnitOfMissingScale), 0, 1);
            bubbleGlowEffect.BlurRadius = Math.Max(0, RestingGlowBlurRadiusInDips + (missingScale * PopGlowBlurBoostPerUnitOfMissingScale));
            appliedPopScale = popScale;
        }

        double bubbleLeftInDips = buddyPosition.X + BubbleLeftEdgeOffsetFromBuddyInDips;
        double bubbleTopInDips = buddyPosition.Y + BubbleVerticalCenterOffsetFromBuddyInDips - (measuredBubbleSize.Height / 2);
        bubbleUpdater.ApplyTranslation(
            OverlayVisualStyle.SnapToWholeDevicePixel(bubbleLeftInDips, windowDpiScale),
            OverlayVisualStyle.SnapToWholeDevicePixel(bubbleTopInDips, windowDpiScale));
    }
}

/// <summary>
/// The dark bubble that shows the spoken reply next to the buddy. New on Windows; its look comes
/// from CompanionResponseOverlayView in CompanionResponseOverlay.swift, which on the Mac was a
/// separate panel that followed the mouse.
/// </summary>
internal sealed class CompanionResponseBubbleVisual
{
    private const double BubbleCornerRadiusInDips = 10;
    private const double BubbleBorderThicknessInDips = 0.8;
    private const double BubbleHorizontalPaddingInDips = 14;
    private const double BubbleVerticalPaddingInDips = 10;
    private const double MaximumTextWidthInDips = 300;
    private const double ResponseFontSizeInDips = 13;
    private const double ResponseLineHeightMultiplier = 1.25;

    /// <summary>Mac: cursorOffsetX = 22 and cursorOffsetY = 6 in CompanionResponseOverlayManager.</summary>
    private const double OffsetRightOfBuddyInDips = 22;
    private const double OffsetBelowBuddyInDips = 6;

    /// <summary>Mac: .shadow(color: .black.opacity(0.35), radius: 16, x: 0, y: 8).</summary>
    private const double ShadowOpacity = 0.35;
    private const double ShadowBlurRadiusInDips = 16;
    private const double ShadowDownwardOffsetInDips = 8;

    /// <summary>DropShadowEffect.Direction is measured counterclockwise from pointing right, so 270 points straight down.</summary>
    private const double ShadowDirectionStraightDownInDegrees = 270;

    private readonly Grid bubbleRoot;
    private readonly TextBlock responseTextBlock;
    private readonly TranslateTransform bubblePositionTransform = new();
    private readonly OverlayElementUpdater bubbleUpdater;
    private string? appliedResponseText;
    private Size measuredBubbleSize;

    public CompanionResponseBubbleVisual()
    {
        Border bubbleBackground = new()
        {
            Background = OverlayVisualStyle.ResponseBubbleSurfaceBrush,
            BorderBrush = OverlayVisualStyle.ResponseBubbleBorderBrush,
            BorderThickness = new Thickness(BubbleBorderThicknessInDips),
            CornerRadius = new CornerRadius(BubbleCornerRadiusInDips),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = ShadowOpacity,
                BlurRadius = ShadowBlurRadiusInDips,
                ShadowDepth = ShadowDownwardOffsetInDips,
                Direction = ShadowDirectionStraightDownInDegrees,
            },
        };

        responseTextBlock = new TextBlock
        {
            Foreground = OverlayVisualStyle.ResponseBubbleTextBrush,
            FontFamily = OverlayVisualStyle.CompanionBubbleFontFamily,
            FontSize = ResponseFontSizeInDips,
            FontWeight = FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = MaximumTextWidthInDips,
            LineHeight = ResponseFontSizeInDips * ResponseLineHeightMultiplier,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(BubbleHorizontalPaddingInDips, BubbleVerticalPaddingInDips, BubbleHorizontalPaddingInDips, BubbleVerticalPaddingInDips),
            IsHitTestVisible = false,
        };

        bubbleRoot = new Grid
        {
            RenderTransform = bubblePositionTransform,
            IsHitTestVisible = false,
        };
        bubbleRoot.Children.Add(bubbleBackground);
        bubbleRoot.Children.Add(responseTextBlock);

        bubbleUpdater = new OverlayElementUpdater(bubbleRoot, bubblePositionTransform);
    }

    public UIElement RootElement => bubbleRoot;

    public void InvalidateMeasuredSize()
    {
        appliedResponseText = null;
    }

    /// <param name="workAreaInOverlayDips">The monitor minus the taskbar, in this overlay's DIPs; the bubble flips and clamps to stay inside it.</param>
    public void Apply(string responseText, double opacity, OverlayDipPoint buddyPosition, Rect workAreaInOverlayDips, double windowDpiScale)
    {
        double effectiveOpacity = string.IsNullOrEmpty(responseText) ? 0 : opacity;
        if (!bubbleUpdater.ApplyOpacity(effectiveOpacity))
        {
            return;
        }

        if (!string.Equals(appliedResponseText, responseText, StringComparison.Ordinal))
        {
            responseTextBlock.Text = responseText;
            appliedResponseText = responseText;
            bubbleRoot.Measure(OverlayVisualStyle.UnconstrainedMeasureSize);
            measuredBubbleSize = bubbleRoot.DesiredSize;
        }

        double bubbleWidthInDips = measuredBubbleSize.Width;
        double bubbleHeightInDips = measuredBubbleSize.Height;

        // Default: right of and slightly below the buddy, like the Mac response panel next to the mouse.
        double bubbleLeftInDips = buddyPosition.X + OffsetRightOfBuddyInDips;
        double bubbleTopInDips = buddyPosition.Y + OffsetBelowBuddyInDips;

        // Same flips as the Mac: past the right edge it moves to the left of the buddy,
        // past the bottom edge it moves above the buddy.
        if (bubbleLeftInDips + bubbleWidthInDips > workAreaInOverlayDips.Right)
        {
            bubbleLeftInDips = buddyPosition.X - OffsetRightOfBuddyInDips - bubbleWidthInDips;
        }

        if (bubbleTopInDips + bubbleHeightInDips > workAreaInOverlayDips.Bottom)
        {
            bubbleTopInDips = buddyPosition.Y - OffsetBelowBuddyInDips - bubbleHeightInDips;
        }

        // Final clamp so a bubble bigger than the space left still starts on screen.
        bubbleLeftInDips = Math.Clamp(bubbleLeftInDips, workAreaInOverlayDips.Left, Math.Max(workAreaInOverlayDips.Left, workAreaInOverlayDips.Right - bubbleWidthInDips));
        bubbleTopInDips = Math.Clamp(bubbleTopInDips, workAreaInOverlayDips.Top, Math.Max(workAreaInOverlayDips.Top, workAreaInOverlayDips.Bottom - bubbleHeightInDips));

        bubbleUpdater.ApplyTranslation(
            OverlayVisualStyle.SnapToWholeDevicePixel(bubbleLeftInDips, windowDpiScale),
            OverlayVisualStyle.SnapToWholeDevicePixel(bubbleTopInDips, windowDpiScale));
    }
}
