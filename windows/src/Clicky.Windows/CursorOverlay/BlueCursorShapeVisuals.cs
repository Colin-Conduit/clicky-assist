using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Clicky.Core.Overlay;
using Clicky.Core.Pointing;

namespace Clicky.Windows.CursorOverlay;

/// <summary>
/// The blue triangle pointer shown while idle and while the reply is spoken.
/// Port of the Triangle shape and the modifiers BlueCursorView applies to it.
/// </summary>
internal sealed class BlueCursorTriangleVisual
{
    private const double RestingGlowBlurRadiusInDips = 8;
    private const double GlowBlurGrowthPerUnitOfExtraScale = 20;
    private const double RotationChangeThresholdInDegrees = 0.01;
    private const double ScaleChangeThreshold = 0.0005;

    private readonly Path trianglePath;
    private readonly DropShadowEffect triangleGlowEffect;
    private readonly ScaleTransform triangleScaleTransform = new(1, 1);
    private readonly RotateTransform triangleRotateTransform = new(CursorOverlayGeometry.DefaultTriangleRotationDegrees);
    private readonly TranslateTransform trianglePositionTransform = new();
    private readonly OverlayElementUpdater triangleUpdater;
    private double appliedRotationDegrees = CursorOverlayGeometry.DefaultTriangleRotationDegrees;
    private double appliedFlightScale = 1.0;

    public BlueCursorTriangleVisual()
    {
        triangleGlowEffect = new DropShadowEffect
        {
            Color = OverlayVisualStyle.OverlayCursorBlueColor,
            ShadowDepth = 0,
            BlurRadius = RestingGlowBlurRadiusInDips,
            Opacity = 1.0,
        };

        // Scale first, then rotate, then move: the geometry is centered on (0, 0), so scaling and
        // rotating happen around the triangle's center exactly like SwiftUI's scaleEffect and rotationEffect.
        TransformGroup triangleTransforms = new();
        triangleTransforms.Children.Add(triangleScaleTransform);
        triangleTransforms.Children.Add(triangleRotateTransform);
        triangleTransforms.Children.Add(trianglePositionTransform);

        trianglePath = new Path
        {
            Data = CreateTriangleGeometry(CursorOverlayGeometry.TriangleSizeInDips),
            Fill = OverlayVisualStyle.OverlayCursorBlueBrush,
            Effect = triangleGlowEffect,
            RenderTransform = triangleTransforms,
            IsHitTestVisible = false,
        };

        triangleUpdater = new OverlayElementUpdater(trianglePath, trianglePositionTransform);
    }

    public UIElement RootElement => trianglePath;

    public void Apply(double opacity, OverlayDipPoint centerPosition, double rotationDegrees, double flightScale)
    {
        if (!triangleUpdater.ApplyOpacity(opacity))
        {
            return;
        }

        triangleUpdater.ApplyTranslation(centerPosition.X, centerPosition.Y);

        if (Math.Abs(rotationDegrees - appliedRotationDegrees) > RotationChangeThresholdInDegrees)
        {
            triangleRotateTransform.Angle = rotationDegrees;
            appliedRotationDegrees = rotationDegrees;
        }

        if (Math.Abs(flightScale - appliedFlightScale) > ScaleChangeThreshold)
        {
            triangleScaleTransform.ScaleX = flightScale;
            triangleScaleTransform.ScaleY = flightScale;

            // Mac: .shadow(radius: 8 + (buddyFlightScale - 1.0) * 20). The glow swells mid-flight.
            triangleGlowEffect.BlurRadius = Math.Max(0, RestingGlowBlurRadiusInDips + ((flightScale - 1.0) * GlowBlurGrowthPerUnitOfExtraScale));
            appliedFlightScale = flightScale;
        }
    }

    /// <summary>
    /// Same vertices as the Swift Triangle shape in a square of side triangleSize, shifted so the
    /// square's center sits at (0, 0). That center is also the triangle's centroid.
    /// </summary>
    private static Geometry CreateTriangleGeometry(double triangleSize)
    {
        double triangleHeight = triangleSize * Math.Sqrt(3.0) / 2.0;

        PathFigure triangleFigure = new()
        {
            StartPoint = new Point(0, -triangleHeight / 1.5),
            IsClosed = true,
            IsFilled = true,
        };
        triangleFigure.Segments.Add(new LineSegment(new Point(-triangleSize / 2, triangleHeight / 3), isStroked: true));
        triangleFigure.Segments.Add(new LineSegment(new Point(triangleSize / 2, triangleHeight / 3), isStroked: true));

        PathGeometry triangleGeometry = new();
        triangleGeometry.Figures.Add(triangleFigure);
        triangleGeometry.Freeze();
        return triangleGeometry;
    }
}

/// <summary>
/// Five small bars that replace the triangle while push-to-talk is held.
/// Port of BlueCursorWaveformView; bar heights come from Core's ListeningWaveformGeometry.
/// </summary>
internal sealed class ListeningWaveformVisual
{
    /// <summary>Mac: TimelineView(.animation(minimumInterval: 1.0 / 36.0)). Fewer bar updates means fewer full-window redraws.</summary>
    private const double MinimumSecondsBetweenBarUpdates = 1.0 / 36.0;

    /// <summary>Mac: .animation(.linear(duration: 0.08), value: audioPowerLevel) smooths jumps in the microphone level.</summary>
    private const double AudioLevelSmoothingSeconds = 0.08;

    private const double GlowOpacity = 0.6;
    private const double GlowBlurRadiusInDips = 6;
    private const double BarHeightChangeThresholdInDips = 0.01;

    private readonly Canvas waveformCanvas;
    private readonly Rectangle[] waveformBars = new Rectangle[ListeningWaveformGeometry.BarCount];
    private readonly double[] appliedBarHeightsInDips = new double[ListeningWaveformGeometry.BarCount];
    private readonly TranslateTransform waveformPositionTransform = new();
    private readonly OverlayElementUpdater waveformUpdater;
    private double lastBarUpdateTimeInSeconds = double.NegativeInfinity;
    private double smoothedAudioPowerLevel;

    public ListeningWaveformVisual()
    {
        waveformCanvas = new Canvas
        {
            RenderTransform = waveformPositionTransform,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = OverlayVisualStyle.OverlayCursorBlueColor,
                ShadowDepth = 0,
                BlurRadius = GlowBlurRadiusInDips,
                Opacity = GlowOpacity,
            },
        };

        // The bars sit side by side, centered horizontally and vertically on the buddy position (0, 0 in this canvas).
        double totalWaveformWidthInDips = (ListeningWaveformGeometry.BarCount * ListeningWaveformGeometry.BarWidthInDips)
            + ((ListeningWaveformGeometry.BarCount - 1) * ListeningWaveformGeometry.BarSpacingInDips);

        for (int barIndex = 0; barIndex < ListeningWaveformGeometry.BarCount; barIndex++)
        {
            Rectangle waveformBar = new()
            {
                Width = ListeningWaveformGeometry.BarWidthInDips,
                Height = 0,
                RadiusX = ListeningWaveformGeometry.BarCornerRadiusInDips,
                RadiusY = ListeningWaveformGeometry.BarCornerRadiusInDips,
                Fill = OverlayVisualStyle.OverlayCursorBlueBrush,
                IsHitTestVisible = false,
            };

            double barLeftInDips = (-totalWaveformWidthInDips / 2)
                + (barIndex * (ListeningWaveformGeometry.BarWidthInDips + ListeningWaveformGeometry.BarSpacingInDips));
            Canvas.SetLeft(waveformBar, barLeftInDips);
            Canvas.SetTop(waveformBar, 0);

            waveformBars[barIndex] = waveformBar;
            waveformCanvas.Children.Add(waveformBar);
        }

        waveformUpdater = new OverlayElementUpdater(waveformCanvas, waveformPositionTransform);
    }

    public UIElement RootElement => waveformCanvas;

    public void Apply(double opacity, OverlayDipPoint centerPosition, double animationTimeInSeconds, double audioPowerLevel)
    {
        if (!waveformUpdater.ApplyOpacity(opacity))
        {
            // Hidden: start the next appearance from the live level instead of easing up from an old one.
            smoothedAudioPowerLevel = audioPowerLevel;
            lastBarUpdateTimeInSeconds = double.NegativeInfinity;
            return;
        }

        waveformUpdater.ApplyTranslation(centerPosition.X, centerPosition.Y);

        double secondsSinceLastBarUpdate = animationTimeInSeconds - lastBarUpdateTimeInSeconds;
        if (secondsSinceLastBarUpdate < MinimumSecondsBetweenBarUpdates)
        {
            return;
        }

        double smoothingFraction = Math.Min(1.0, secondsSinceLastBarUpdate / AudioLevelSmoothingSeconds);
        smoothedAudioPowerLevel += (audioPowerLevel - smoothedAudioPowerLevel) * smoothingFraction;
        lastBarUpdateTimeInSeconds = animationTimeInSeconds;

        for (int barIndex = 0; barIndex < ListeningWaveformGeometry.BarCount; barIndex++)
        {
            double barHeightInDips = ListeningWaveformGeometry.ComputeBarHeightInDips(barIndex, animationTimeInSeconds, smoothedAudioPowerLevel);
            if (Math.Abs(barHeightInDips - appliedBarHeightsInDips[barIndex]) <= BarHeightChangeThresholdInDips)
            {
                continue;
            }

            Rectangle waveformBar = waveformBars[barIndex];
            waveformBar.Height = barHeightInDips;
            Canvas.SetTop(waveformBar, -barHeightInDips / 2);
            appliedBarHeightsInDips[barIndex] = barHeightInDips;
        }
    }
}

/// <summary>
/// The small spinning arc shown while the companion is processing.
/// Port of BlueCursorSpinnerView.
/// </summary>
internal sealed class ProcessingSpinnerVisual
{
    private const double SpinnerDiameterInDips = 14;
    private const double SpinnerStrokeThicknessInDips = 2.5;
    private const double ArcTrimStartFraction = 0.15;
    private const double ArcTrimEndFraction = 0.85;
    private const double SecondsPerFullRotation = 0.8;
    private const double GlowOpacity = 0.6;
    private const double GlowBlurRadiusInDips = 6;
    private const double RotationChangeThresholdInDegrees = 0.01;

    private readonly Path spinnerArcPath;
    private readonly RotateTransform spinnerRotateTransform = new(0);
    private readonly TranslateTransform spinnerPositionTransform = new();
    private readonly OverlayElementUpdater spinnerUpdater;
    private double appliedRotationDegrees;

    public ProcessingSpinnerVisual()
    {
        double spinnerRadiusInDips = SpinnerDiameterInDips / 2;

        TransformGroup spinnerTransforms = new();
        spinnerTransforms.Children.Add(spinnerRotateTransform);
        spinnerTransforms.Children.Add(spinnerPositionTransform);

        spinnerArcPath = new Path
        {
            Data = CreateTrimmedCircleArcGeometry(spinnerRadiusInDips),
            Stroke = CreateArcStrokeBrush(spinnerRadiusInDips),
            StrokeThickness = SpinnerStrokeThicknessInDips,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = spinnerTransforms,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = OverlayVisualStyle.OverlayCursorBlueColor,
                ShadowDepth = 0,
                BlurRadius = GlowBlurRadiusInDips,
                Opacity = GlowOpacity,
            },
        };

        spinnerUpdater = new OverlayElementUpdater(spinnerArcPath, spinnerPositionTransform);
    }

    public UIElement RootElement => spinnerArcPath;

    public void Apply(double opacity, OverlayDipPoint centerPosition, double animationTimeInSeconds)
    {
        // The rotation only advances while the spinner is visible or fading, so a hidden spinner
        // never makes the layered window redraw.
        if (!spinnerUpdater.ApplyOpacity(opacity))
        {
            return;
        }

        spinnerUpdater.ApplyTranslation(centerPosition.X, centerPosition.Y);

        // Mac: .linear(duration: 0.8).repeatForever, clockwise (positive angles turn clockwise in WPF too).
        double rotationFraction = (animationTimeInSeconds / SecondsPerFullRotation) % 1.0;
        double rotationDegrees = rotationFraction * 360.0;
        if (Math.Abs(rotationDegrees - appliedRotationDegrees) > RotationChangeThresholdInDegrees)
        {
            spinnerRotateTransform.Angle = rotationDegrees;
            appliedRotationDegrees = rotationDegrees;
        }
    }

    /// <summary>
    /// Circle().trim(from: 0.15, to: 0.85): SwiftUI starts a circle at 3 o'clock and runs clockwise,
    /// so the arc runs clockwise from 54 to 306 degrees, leaving the gap at 3 o'clock.
    /// </summary>
    private static Geometry CreateTrimmedCircleArcGeometry(double spinnerRadiusInDips)
    {
        Point arcStartPoint = ComputePointOnCircle(spinnerRadiusInDips, ArcTrimStartFraction * 360.0);
        Point arcEndPoint = ComputePointOnCircle(spinnerRadiusInDips, ArcTrimEndFraction * 360.0);
        bool arcSpansMoreThanHalfCircle = (ArcTrimEndFraction - ArcTrimStartFraction) > 0.5;

        PathFigure arcFigure = new() { StartPoint = arcStartPoint, IsClosed = false, IsFilled = false };
        arcFigure.Segments.Add(new ArcSegment(
            arcEndPoint,
            new Size(spinnerRadiusInDips, spinnerRadiusInDips),
            rotationAngle: 0,
            isLargeArc: arcSpansMoreThanHalfCircle,
            sweepDirection: SweepDirection.Clockwise,
            isStroked: true));

        PathGeometry arcGeometry = new();
        arcGeometry.Figures.Add(arcFigure);
        arcGeometry.Freeze();
        return arcGeometry;
    }

    /// <summary>
    /// The Mac strokes the arc with an AngularGradient from clear blue to solid blue, so the tail
    /// fades out like a comet. WPF has no angular (conic) gradient brush. A vertical linear gradient
    /// from clear at the bottom to solid at the top gives nearly the same opacity at each end of
    /// this particular arc (about 0.1 at the tail, 0.9 at the head, 0.5 at 9 o'clock) and rotates
    /// with the arc, so at 14 DIPs and 0.8 seconds per turn it reads the same.
    /// </summary>
    private static Brush CreateArcStrokeBrush(double spinnerRadiusInDips)
    {
        Color transparentBlue = Color.FromArgb(0, OverlayVisualStyle.OverlayCursorBlueColor.R, OverlayVisualStyle.OverlayCursorBlueColor.G, OverlayVisualStyle.OverlayCursorBlueColor.B);

        LinearGradientBrush arcStrokeBrush = new()
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, spinnerRadiusInDips),
            EndPoint = new Point(0, -spinnerRadiusInDips),
        };
        arcStrokeBrush.GradientStops.Add(new GradientStop(transparentBlue, 0.0));
        arcStrokeBrush.GradientStops.Add(new GradientStop(OverlayVisualStyle.OverlayCursorBlueColor, 1.0));
        arcStrokeBrush.Freeze();
        return arcStrokeBrush;
    }

    /// <summary>WPF's Y axis points down, so increasing angles move clockwise on screen, the same as SwiftUI.</summary>
    private static Point ComputePointOnCircle(double circleRadius, double angleInDegrees)
    {
        double angleInRadians = angleInDegrees * Math.PI / 180.0;
        return new Point(circleRadius * Math.Cos(angleInRadians), circleRadius * Math.Sin(angleInRadians));
    }
}
