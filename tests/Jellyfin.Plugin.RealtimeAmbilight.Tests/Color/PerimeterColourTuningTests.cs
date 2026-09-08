using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Color;

public sealed class PerimeterColourTuningTests
{
    [Fact]
    public void WhiteWallLeavesTheSharedColourUntouched()
    {
        var adjustment = PerimeterColourTuning.Default.ToAdjustment();
        var colour = adjustment.Apply(new LinearRgb(0.3f, 0.4f, 0.5f), 0, new LedLayout(1, 1, 1, 1));

        Assert.Equal(0.3f, colour.Red, 5);
        Assert.Equal(0.4f, colour.Green, 5);
        Assert.Equal(0.5f, colour.Blue, 5);
    }

    [Fact]
    public void ColouredWallAddsThePrimaryItAbsorbsMost()
    {
        var correction = WallColourCorrection.FromHtmlColour("#ffd0a0");

        Assert.Equal(1f, correction.RedGain, 5);
        Assert.True(correction.GreenGain > correction.RedGain);
        Assert.True(correction.BlueGain > correction.GreenGain);
        Assert.InRange(correction.BlueGain, 1f, 2f);
    }

    [Fact]
    public void ZeroWallStrengthDisablesWallCompensation()
    {
        var correction = WallColourCorrection.FromHtmlColour("#ff0000", 0);

        Assert.Equal(WallColourCorrection.None, correction);
    }

    [Fact]
    public void BlackLevelFloorTurnsOffOnlyTheDarkestLeds()
    {
        var tuning = PerimeterColourTuning.Default with { BlackLevelFloorPercent = 5 };
        var adjustment = tuning.ToAdjustment();
        var layout = new LedLayout(1, 1, 1, 1);

        var belowFloor = adjustment.Apply(new LinearRgb(0.02f, 0.02f, 0.02f), 0, layout);
        Assert.Equal(0f, belowFloor.Red);
        Assert.Equal(0f, belowFloor.Green);
        Assert.Equal(0f, belowFloor.Blue);

        var full = adjustment.Apply(new LinearRgb(1f, 1f, 1f), 0, layout);
        Assert.Equal(1f, full.Red, 4);
        Assert.Equal(1f, full.Green, 4);
        Assert.Equal(1f, full.Blue, 4);
    }

    [Fact]
    public void BlackLevelFloorPreservesHueAboveTheFloor()
    {
        var tuning = PerimeterColourTuning.Default with { BlackLevelFloorPercent = 5 };
        var adjustment = tuning.ToAdjustment();
        var layout = new LedLayout(1, 1, 1, 1);

        var result = adjustment.Apply(new LinearRgb(0.5f, 0.25f, 0f), 0, layout);

        Assert.True(result.Red > result.Green);
        Assert.Equal(0f, result.Blue);
        Assert.Equal(2f, result.Red / result.Green, 2);
    }

    [Fact]
    public void DefaultTuningBuildsAnIdentityCurve()
    {
        var adjustment = PerimeterColourTuning.Default.ToAdjustment();

        Assert.True(adjustment.IsIdentity);
    }

    [Fact]
    public void OneAnchorsBrightnessChangeAffectsOnlyThatColourNotTheWholeWheel()
    {
        var tuning = PerimeterColourTuning.Default with { RedBrightnessPercent = 60 };
        var adjustment = tuning.ToAdjustment();
        var layout = new LedLayout(1, 1, 1, 1);

        var red = adjustment.Apply(new LinearRgb(1f, 0f, 0f), 0, layout);
        var green = adjustment.Apply(new LinearRgb(0f, 1f, 0f), 0, layout);

        Assert.True(red.Red < 0.9f, $"expected red dimmed by its own anchor, got {red.Red}");
        Assert.Equal(1f, green.Green, 3);
    }

    [Fact]
    public void RedAndBlueGainClampWidensTo40To160PercentForWhiteWhileGreenStaysAt50To150()
    {
        var wideRed = PerimeterColourTuning.Default with { RedGainPercent = 160 };
        var wideBlue = PerimeterColourTuning.Default with { BlueGainPercent = 40 };
        var narrowGreen = PerimeterColourTuning.Default with { GreenGainPercent = 160 };
        var layout = new LedLayout(1, 1, 1, 1);
        var source = new LinearRgb(0.5f, 0.5f, 0.5f);

        Assert.Equal(0.8f, wideRed.ToAdjustment().Apply(source, 0, layout).Red, 3);
        Assert.Equal(0.2f, wideBlue.ToAdjustment().Apply(source, 0, layout).Blue, 3);
        // Green's clamp was not part of this round's 20%-wider request and
        // stays capped at 150%, i.e. a requested 160% clamps down to 150%.
        Assert.Equal(0.75f, narrowGreen.ToAdjustment().Apply(source, 0, layout).Green, 3);
    }

    [Fact]
    public void SideTrimOnlyChangesItsOwnPhysicalRun()
    {
        var tuning = PerimeterColourTuning.Default with { RightBlueGainPercent = 125 };
        var adjustment = tuning.ToAdjustment();
        var layout = new LedLayout(1, 1, 1, 1);
        var source = new LinearRgb(0.2f, 0.2f, 0.2f);

        Assert.Equal(0.2f, adjustment.Apply(source, 0, layout).Blue, 5);
        Assert.Equal(0.25f, adjustment.Apply(source, 1, layout).Blue, 5);
        Assert.Equal(0.2f, adjustment.Apply(source, 2, layout).Blue, 5);
        Assert.Equal(0.2f, adjustment.Apply(source, 3, layout).Blue, 5);
    }
}
