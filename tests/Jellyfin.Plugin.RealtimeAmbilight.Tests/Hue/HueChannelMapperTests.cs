using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Mapping;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Model;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Hue;

public class HueChannelMapperTests
{
    private static readonly LinearRgb TopLeft = new(1f, 0f, 0f);
    private static readonly LinearRgb TopRight = new(0f, 1f, 0f);
    private static readonly LinearRgb RightTop = new(0f, 1f, 0f);
    private static readonly LinearRgb RightBottom = new(0f, 0f, 1f);
    private static readonly LinearRgb BottomRight = new(0f, 0f, 1f);
    private static readonly LinearRgb BottomLeft = new(1f, 1f, 0f);
    private static readonly LinearRgb LeftBottom = new(1f, 1f, 0f);
    private static readonly LinearRgb LeftTop = new(1f, 0f, 0f);
    private static readonly LinearRgb SceneAverage = new(0.5f, 0.5f, 0.5f);

    // Matches EdgeSampler's own run order: Top left-to-right, Right
    // top-to-bottom, Bottom right-to-left, Left bottom-to-top.
    private static PerimeterSamples CreateSamples() => new(
        Top: [TopLeft, TopRight],
        Right: [RightTop, RightBottom],
        Bottom: [BottomRight, BottomLeft],
        Left: [LeftBottom, LeftTop]);

    [Fact]
    public void AChannelDirectlyAboveScreenCentreFollowsTheTopEdgesMidpoint()
    {
        var colour = HueChannelMapper.Map(new HuePosition(0, 0, 1), CreateSamples(), SceneAverage);

        var expected = LinearRgb.Lerp(TopLeft, TopRight, 0.5f);
        AssertApproximatelyEqual(expected, colour);
    }

    [Fact]
    public void AChannelBesideTheScreenOnTheRightLeansTowardTheRightEdge()
    {
        var atScreenCentre = HueChannelMapper.Map(new HuePosition(0, 0, 0.5), CreateSamples(), SceneAverage);
        var besideScreenRight = HueChannelMapper.Map(new HuePosition(1, 0, 0.5), CreateSamples(), SceneAverage);

        // The fully-right position must be closer to the pure right-edge
        // sample (RightTop/RightBottom average at z=0.5) than the centred one.
        var pureRight = LinearRgb.Lerp(RightTop, RightBottom, 0.5f);
        Assert.True(
            DistanceSquared(besideScreenRight, pureRight) < DistanceSquared(atScreenCentre, pureRight),
            "expected the far-right channel to sit closer to the right edge sample");
    }

    [Fact]
    public void AChannelBesideTheViewerIsExactlyTheCorrespondingSideEdge()
    {
        var colour = HueChannelMapper.Map(new HuePosition(1, 0.5, 0.5), CreateSamples(), SceneAverage);

        var expectedRight = LinearRgb.Lerp(RightTop, RightBottom, 0.5f);
        AssertApproximatelyEqual(expectedRight, colour);
    }

    [Fact]
    public void LeftAndRightAreSymmetric()
    {
        var right = HueChannelMapper.Map(new HuePosition(1, 0.5, 0.25), CreateSamples(), SceneAverage);
        var left = HueChannelMapper.Map(new HuePosition(-1, 0.5, 0.25), CreateSamples(), SceneAverage);

        var expectedRight = LinearRgb.Lerp(RightTop, RightBottom, 0.75f); // z=0.25 -> RightFraction = 1-0.25 = 0.75
        var expectedLeft = LinearRgb.Lerp(LeftBottom, LeftTop, 0.25f); // z=0.25 -> LeftFraction = 0.25

        AssertApproximatelyEqual(expectedRight, right);
        AssertApproximatelyEqual(expectedLeft, left);
    }

    [Fact]
    public void AChannelBehindTheViewerIsTheSceneAverageRegardlessOfXAndZ()
    {
        var colourA = HueChannelMapper.Map(new HuePosition(-1, 1, 0), CreateSamples(), SceneAverage);
        var colourB = HueChannelMapper.Map(new HuePosition(1, 1, 1), CreateSamples(), SceneAverage);

        AssertApproximatelyEqual(SceneAverage, colourA);
        AssertApproximatelyEqual(SceneAverage, colourB);
    }

    [Fact]
    public void DepthTransitionsSmoothlyFromScreenThroughSideToScene()
    {
        var samples = CreateSamples();
        var atScreen = HueChannelMapper.Map(new HuePosition(1, 0, 0.5), samples, SceneAverage);
        var quarter = HueChannelMapper.Map(new HuePosition(1, 0.25, 0.5), samples, SceneAverage);
        var beside = HueChannelMapper.Map(new HuePosition(1, 0.5, 0.5), samples, SceneAverage);
        var threeQuarter = HueChannelMapper.Map(new HuePosition(1, 0.75, 0.5), samples, SceneAverage);
        var behind = HueChannelMapper.Map(new HuePosition(1, 1, 0.5), samples, SceneAverage);

        // Blue (from RightBottom) should increase monotonically front-to-back
        // toward the 0.5 scene average, since RightTop/RightBottom for this
        // fixture average to (0, 0.5, 0.5) and SceneAverage is (0.5,0.5,0.5).
        Assert.True(atScreen.Blue <= quarter.Blue + 1e-4f);
        Assert.True(quarter.Blue <= beside.Blue + 1e-4f);
        Assert.True(beside.Blue <= threeQuarter.Blue + 1e-4f);
        Assert.True(threeQuarter.Blue <= behind.Blue + 1e-4f);
    }

    [Fact]
    public void PositionsOutsideTheDocumentedRangeAreClamped()
    {
        var farOutside = HueChannelMapper.Map(new HuePosition(5, 5, 5), CreateSamples(), SceneAverage);
        var clampedEquivalent = HueChannelMapper.Map(new HuePosition(1, 1, 1), CreateSamples(), SceneAverage);

        AssertApproximatelyEqual(clampedEquivalent, farOutside);
    }

    private static double DistanceSquared(LinearRgb a, LinearRgb b)
    {
        var dr = a.Red - b.Red;
        var dg = a.Green - b.Green;
        var db = a.Blue - b.Blue;
        return (dr * dr) + (dg * dg) + (db * db);
    }

    private static void AssertApproximatelyEqual(LinearRgb expected, LinearRgb actual)
    {
        Assert.Equal(expected.Red, actual.Red, precision: 3);
        Assert.Equal(expected.Green, actual.Green, precision: 3);
        Assert.Equal(expected.Blue, actual.Blue, precision: 3);
    }
}
