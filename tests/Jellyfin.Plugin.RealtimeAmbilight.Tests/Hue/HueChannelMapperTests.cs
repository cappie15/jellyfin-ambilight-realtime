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

    // Y is +1 at/near the screen, -1 behind the viewer, 0 beside the viewer
    // -- confirmed against the operator's own real bridge data 2026-09-15
    // (see HueChannelMapper's own remarks); the mirror image of Y running
    // 0..1 with 0 at the screen, which every position below used before
    // that finding.
    [Fact]
    public void AChannelDirectlyAboveScreenCentreFollowsTheTopEdgesMidpoint()
    {
        var colour = HueChannelMapper.Map(new HuePosition(0, 1, 1), CreateSamples(), SceneAverage);

        var expected = LinearRgb.Lerp(TopLeft, TopRight, 0.5f);
        AssertApproximatelyEqual(expected, colour);
    }

    [Fact]
    public void AChannelBesideTheScreenOnTheRightLeansTowardTheRightEdge()
    {
        var atScreenCentre = HueChannelMapper.Map(new HuePosition(0, 1, 0), CreateSamples(), SceneAverage);
        var besideScreenRight = HueChannelMapper.Map(new HuePosition(1, 1, 0), CreateSamples(), SceneAverage);

        // The fully-right position must be closer to the pure right-edge
        // sample (RightTop/RightBottom average at mid height) than the centred one.
        var pureRight = LinearRgb.Lerp(RightTop, RightBottom, 0.5f);
        Assert.True(
            DistanceSquared(besideScreenRight, pureRight) < DistanceSquared(atScreenCentre, pureRight),
            "expected the far-right channel to sit closer to the right edge sample");
    }

    [Fact]
    public void AChannelBesideTheViewerIsExactlyTheCorrespondingSideEdge()
    {
        var colour = HueChannelMapper.Map(new HuePosition(1, 0, 0), CreateSamples(), SceneAverage);

        var expectedRight = LinearRgb.Lerp(RightTop, RightBottom, 0.5f);
        AssertApproximatelyEqual(expectedRight, colour);
    }

    [Fact]
    public void LeftAndRightAreSymmetric()
    {
        var right = HueChannelMapper.Map(new HuePosition(1, 0, -0.5), CreateSamples(), SceneAverage);
        var left = HueChannelMapper.Map(new HuePosition(-1, 0, -0.5), CreateSamples(), SceneAverage);

        var expectedRight = LinearRgb.Lerp(RightTop, RightBottom, 0.75f); // z=-0.5 is a quarter of the way up, so RightFraction = 1-0.25 = 0.75
        var expectedLeft = LinearRgb.Lerp(LeftBottom, LeftTop, 0.25f); // z=-0.5 is a quarter of the way up, so LeftFraction = 0.25

        AssertApproximatelyEqual(expectedRight, right);
        AssertApproximatelyEqual(expectedLeft, left);
    }

    [Fact]
    public void AChannelBehindTheViewerIsTheSceneAverageRegardlessOfXAndZ()
    {
        var colourA = HueChannelMapper.Map(new HuePosition(-1, -1, -1), CreateSamples(), SceneAverage);
        var colourB = HueChannelMapper.Map(new HuePosition(1, -1, 1), CreateSamples(), SceneAverage);

        AssertApproximatelyEqual(SceneAverage, colourA);
        AssertApproximatelyEqual(SceneAverage, colourB);
    }

    [Fact]
    public void DepthTransitionsSmoothlyFromScreenThroughSideToScene()
    {
        var samples = CreateSamples();
        var atScreen = HueChannelMapper.Map(new HuePosition(1, 1, 0), samples, SceneAverage);
        var quarter = HueChannelMapper.Map(new HuePosition(1, 0.5, 0), samples, SceneAverage);
        var beside = HueChannelMapper.Map(new HuePosition(1, 0, 0), samples, SceneAverage);
        var threeQuarter = HueChannelMapper.Map(new HuePosition(1, -0.5, 0), samples, SceneAverage);
        var behind = HueChannelMapper.Map(new HuePosition(1, -1, 0), samples, SceneAverage);

        // Blue (from RightBottom) should increase monotonically front-to-back
        // toward the 0.5 scene average, since RightTop/RightBottom for this
        // fixture average to (0, 0.5, 0.5) and SceneAverage is (0.5,0.5,0.5).
        Assert.True(atScreen.Blue <= quarter.Blue + 1e-4f);
        Assert.True(quarter.Blue <= beside.Blue + 1e-4f);
        Assert.True(beside.Blue <= threeQuarter.Blue + 1e-4f);
        Assert.True(threeQuarter.Blue <= behind.Blue + 1e-4f);
    }

    [Fact]
    public void NegativeHeightIsBelowEyeLevelRatherThanPinnedToTheBottomEdge()
    {
        // Z runs -1 (floor) to +1 (ceiling). It was read as 0..1 until
        // 2026-10-19, which clamped every real negative height to the very
        // bottom of the picture: in the operator's own room that was half the
        // channels, all showing the bottom edge whatever their real height.
        var samples = CreateSamples();
        var quarterUp = HueChannelMapper.Map(new HuePosition(1, 0, -0.5), samples, SceneAverage);
        var onTheFloor = HueChannelMapper.Map(new HuePosition(1, 0, -1), samples, SceneAverage);

        Assert.NotEqual(onTheFloor, quarterUp);
        AssertApproximatelyEqual(LinearRgb.Lerp(RightTop, RightBottom, 1f), onTheFloor);
        AssertApproximatelyEqual(LinearRgb.Lerp(RightTop, RightBottom, 0.75f), quarterUp);
    }

    [Fact]
    public void HeightsSpanTheWholePictureBetweenFloorAndCeiling()
    {
        var samples = CreateSamples();
        var floor = HueChannelMapper.Map(new HuePosition(0, 1, -1), samples, SceneAverage);
        var middle = HueChannelMapper.Map(new HuePosition(0, 1, 0), samples, SceneAverage);
        var ceiling = HueChannelMapper.Map(new HuePosition(0, 1, 1), samples, SceneAverage);

        AssertApproximatelyEqual(LinearRgb.Lerp(BottomLeft, BottomRight, 0.5f), floor);
        AssertApproximatelyEqual(LinearRgb.Lerp(TopLeft, TopRight, 0.5f), ceiling);
        // The midpoint is a real blend of the two, not either extreme.
        Assert.True(DistanceSquared(middle, floor) > 1e-6);
        Assert.True(DistanceSquared(middle, ceiling) > 1e-6);
    }

    [Fact]
    public void AScreenNarrowerThanTheRoomPutsItsOwnEdgeAtThePicturesEdge()
    {
        // The bridge reports positions against the room, not the screen. On
        // the reference installation the strips hugging the television report
        // X of only +-0.4, so with no extent they sampled the middle 40% of
        // the picture and never its sides at all.
        var samples = CreateSamples();
        // At the screen (y = 1), where how far across the picture a light sits
        // actually changes the colour; beside the viewer only the sign of x
        // matters, so the extent would be invisible there.
        var withoutExtent = HueChannelMapper.Map(new HuePosition(0.4, 1, 0), samples, SceneAverage);
        var withExtent = HueChannelMapper.Map(new HuePosition(0.4, 1, 0), samples, SceneAverage, new HueScreenExtent(0.4, 0.4));
        var atTheRoomEdge = HueChannelMapper.Map(new HuePosition(1, 1, 0), samples, SceneAverage);

        Assert.NotEqual(withoutExtent, withExtent);
        AssertApproximatelyEqual(atTheRoomEdge, withExtent);
    }

    [Fact]
    public void TheDefaultExtentIsTheWholeRoomSoExistingInstallationsAreUnchanged()
    {
        var samples = CreateSamples();
        foreach (var position in new[] { new HuePosition(0.4, 0.8, -0.4), new HuePosition(-1, 0, 1), new HuePosition(0, 1, 0) })
        {
            AssertApproximatelyEqual(
                HueChannelMapper.Map(position, samples, SceneAverage),
                HueChannelMapper.Map(position, samples, SceneAverage, HueScreenExtent.Full));
            AssertApproximatelyEqual(
                HueChannelMapper.Map(position, samples, SceneAverage),
                HueChannelMapper.Map(position, samples, SceneAverage, default));
        }
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
