using Clicky.Core.Overlay;
using Clicky.Core.Pointing;

namespace Clicky.Core.Tests;

public sealed class CursorOverlayAnimationMathTests
{
    [Theory]
    [InlineData(100, 0.6)]
    [InlineData(800, 1.0)]
    [InlineData(4000, 1.4)]
    public void FlightDurationScalesWithDistanceAndIsClamped(double flightDistance, double expectedDurationInSeconds)
    {
        CursorFlightPath flightPath = new(new OverlayDipPoint(0, 0), new OverlayDipPoint(flightDistance, 0));

        Assert.Equal(expectedDurationInSeconds, flightPath.FlightDuration.TotalSeconds, 6);
    }

    [Fact]
    public void FlightStartsAndEndsAtTheRightPointsAndArcsUpward()
    {
        OverlayDipPoint flightStart = new(100, 500);
        OverlayDipPoint flightEnd = new(900, 500);
        CursorFlightPath flightPath = new(flightStart, flightEnd);

        CursorFlightFrame startFrame = flightPath.SampleAtLinearProgress(0);
        CursorFlightFrame midpointFrame = flightPath.SampleAtLinearProgress(0.5);
        CursorFlightFrame endFrame = flightPath.SampleAtLinearProgress(1);

        Assert.Equal(flightStart, startFrame.Position);
        Assert.Equal(flightEnd.X, endFrame.Position.X, 6);
        Assert.Equal(flightEnd.Y, endFrame.Position.Y, 6);

        // Arc height is min(800 * 0.2, 80) = 80, so the control point is 80 above the line and
        // the curve's midpoint is half that, 40 above.
        Assert.Equal(500 - 80, flightPath.BezierControlPoint.Y, 6);
        Assert.Equal(500 - 40, midpointFrame.Position.Y, 6);
        Assert.Equal(1.3, midpointFrame.Scale, 6);
        Assert.Equal(1.0, endFrame.Scale, 6);

        // Flying right and level at the apex means the tip points right: 0 degrees tangent + 90.
        Assert.Equal(90, midpointFrame.RotationDegrees, 6);
    }

    [Fact]
    public void ProgressOutsideZeroToOneIsClamped()
    {
        CursorFlightPath flightPath = new(new OverlayDipPoint(0, 0), new OverlayDipPoint(300, 300));

        Assert.Equal(flightPath.SampleAtLinearProgress(1).Position, flightPath.SampleAtLinearProgress(1.7).Position);
        Assert.Equal(flightPath.SampleAtLinearProgress(0).Position, flightPath.SampleAtElapsedTime(TimeSpan.FromSeconds(-1)).Position);
    }

    [Fact]
    public void NavigationDestinationIsOffsetAndKeptInsideTheOverlay()
    {
        OverlayDipPoint middleDestination = CursorOverlayGeometry.ComputeNavigationDestination(new OverlayDipPoint(500, 300), 1920, 1080);
        OverlayDipPoint cornerDestination = CursorOverlayGeometry.ComputeNavigationDestination(new OverlayDipPoint(1919, 1079), 1920, 1080);
        OverlayDipPoint originDestination = CursorOverlayGeometry.ComputeNavigationDestination(new OverlayDipPoint(0, 0), 1920, 1080);

        Assert.Equal(new OverlayDipPoint(508, 312), middleDestination);
        Assert.Equal(new OverlayDipPoint(1900, 1060), cornerDestination);
        Assert.Equal(new OverlayDipPoint(20, 20), originDestination);
    }

    [Fact]
    public void FollowSpringSettlesOnTheTarget()
    {
        CursorFollowSpring followSpring = new();
        followSpring.SnapTo(new OverlayDipPoint(0, 0));
        OverlayDipPoint targetPosition = new(400, -250);

        for (int frameIndex = 0; frameIndex < 120; frameIndex++)
        {
            followSpring.Advance(targetPosition, 1.0 / 60.0);
        }

        Assert.Equal(targetPosition.X, followSpring.CurrentPosition.X, 1);
        Assert.Equal(targetPosition.Y, followSpring.CurrentPosition.Y, 1);
    }

    [Fact]
    public void FollowSpringSurvivesALongFrameGap()
    {
        CursorFollowSpring followSpring = new();
        followSpring.SnapTo(new OverlayDipPoint(0, 0));

        OverlayDipPoint positionAfterGap = followSpring.Advance(new OverlayDipPoint(1000, 1000), elapsedSeconds: 30);

        Assert.InRange(positionAfterGap.X, 0, 1300);
        Assert.False(double.IsNaN(positionAfterGap.X));
    }

    [Fact]
    public void WaveformBarsReactToAudioLevel()
    {
        double quietCenterBarHeight = ListeningWaveformGeometry.ComputeBarHeightInDips(2, animationTimeInSeconds: 0, audioPowerLevel: 0);
        double loudCenterBarHeight = ListeningWaveformGeometry.ComputeBarHeightInDips(2, animationTimeInSeconds: 0, audioPowerLevel: 1);
        double loudEdgeBarHeight = ListeningWaveformGeometry.ComputeBarHeightInDips(0, animationTimeInSeconds: 0, audioPowerLevel: 1);

        Assert.InRange(quietCenterBarHeight, 3, 4.5);
        Assert.True(loudCenterBarHeight > loudEdgeBarHeight);
        Assert.InRange(loudCenterBarHeight, 13, 14.5);
    }

    [Fact]
    public void OverlayCopyHasNoExclamationPoints()
    {
        Assert.All(PointingBubblePhrases.NavigationPointerPhrases, pointerPhrase => Assert.DoesNotContain('!', pointerPhrase));
        Assert.DoesNotContain('!', PointingBubblePhrases.WelcomeMessage);
        Assert.Equal("press ctrl + alt and introduce yourself", PointingBubblePhrases.BuildOnboardingPromptText("Ctrl + Alt"));
    }
}
