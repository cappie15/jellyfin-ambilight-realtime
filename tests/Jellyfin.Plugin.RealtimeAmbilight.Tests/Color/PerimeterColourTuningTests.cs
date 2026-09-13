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
    public void AnAnchorsBrightnessFloorIs20PercentNotTheIntensityFloorOf50()
    {
        // Widened on the operator's own finding that a secondary colour
        // (Yellow/Cyan/Magenta) lights two physical LED channels at once, so
        // it can start out measurably brighter than a primary and need more
        // than a 50%-floor's worth of headroom to dim back down.
        var atTwenty = PerimeterColourTuning.Default with { RedBrightnessPercent = 20 };
        var belowTwenty = PerimeterColourTuning.Default with { RedBrightnessPercent = 5 };
        var layout = new LedLayout(1, 1, 1, 1);
        var source = new LinearRgb(1f, 0f, 0f);

        Assert.Equal(0.2f, atTwenty.ToAdjustment().Apply(source, 0, layout).Red, 3);
        Assert.Equal(0.2f, belowTwenty.ToAdjustment().Apply(source, 0, layout).Red, 3);
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
    public void WhiteExtractionFactorIsOneWhenTheWhiteBalanceIsCentred()
    {
        Assert.Equal(1f, PerimeterColourTuning.Default.ToAdjustment().WhiteExtractionFactor, 5);
    }

    [Fact]
    public void WhiteExtractionFactorTapersTowardsZeroAsTheWhiteBalanceShiftsToItsExtreme()
    {
        // RedGainPercent's clamp is 40-160 (see the test above); its extreme
        // is the White step's colour-temperature slider's own extreme.
        var extreme = PerimeterColourTuning.Default with { RedGainPercent = 160 };

        Assert.Equal(0f, extreme.ToAdjustment().WhiteExtractionFactor, 4);
    }

    [Fact]
    public void WhiteExtractionFactorDecreasesMonotonicallyAsTheShiftGrows()
    {
        var centred = PerimeterColourTuning.Default.ToAdjustment().WhiteExtractionFactor;
        var slight = (PerimeterColourTuning.Default with { RedGainPercent = 120 }).ToAdjustment().WhiteExtractionFactor;
        var extreme = (PerimeterColourTuning.Default with { RedGainPercent = 160 }).ToAdjustment().WhiteExtractionFactor;

        Assert.True(centred > slight, $"{centred} should be greater than {slight}");
        Assert.True(slight > extreme, $"{slight} should be greater than {extreme}");
    }

    [Fact]
    public void BrightnessAbove100PercentBoostsAScenePastTheSourcesOwnValue()
    {
        // The picture's own pixel value is the ceiling everywhere else in
        // this pipeline; a physical LED strip next to a bright HDR screen
        // still reads as dim next to it, so this is the one place an
        // operator can push already-dim content brighter than the source.
        var tuning = PerimeterColourTuning.Default with { BrightnessPercent = 150 };
        var layout = new LedLayout(1, 1, 1, 1);

        var boosted = tuning.ToAdjustment().Apply(new LinearRgb(0.4f, 0.4f, 0.4f), 0, layout);

        Assert.Equal(0.6f, boosted.Red, 3);
    }

    [Fact]
    public void SideTrimBrightnessAlsoBoostsAbove100Percent()
    {
        var tuning = PerimeterColourTuning.Default with { TopBrightnessPercent = 150 };
        var layout = new LedLayout(1, 1, 1, 1);

        var boosted = tuning.ToAdjustment().Apply(new LinearRgb(0.4f, 0.4f, 0.4f), 0, layout);

        Assert.Equal(0.6f, boosted.Red, 3);
    }

    [Fact]
    public void AnAggressiveOverallSaturationClipsAWeakenedAnchorsIntensityBackToInvisible()
    {
        // Reproduces an operator's own real settings: 180% overall saturation
        // stretches every channel away from grey *after* the anchor curve
        // runs, and clamps to 0-1 -- so a 90% Red Intensity (a small pull
        // toward grey) gets pulled negative by that stretch and clipped
        // straight back to zero, as if the slider had never moved.
        var tuning = PerimeterColourTuning.Default with { SaturationPercent = 180, RedIntensityPercent = 90 };
        var layout = new LedLayout(1, 1, 1, 1);

        var result = tuning.ToAdjustment().Apply(new LinearRgb(0.28f, 0f, 0f), 0, layout);

        Assert.Equal(0f, result.Green);
        Assert.Equal(0f, result.Blue);
    }

    [Fact]
    public void NeutralizingGeneralAdjustmentsMakesThatSameWeakenedAnchorVisibleAgain()
    {
        var tuning = PerimeterColourTuning.Default with { SaturationPercent = 180, RedIntensityPercent = 90 };
        var layout = new LedLayout(1, 1, 1, 1);

        var result = tuning.WithGeneralAdjustmentsNeutralized().ToAdjustment().Apply(new LinearRgb(0.28f, 0f, 0f), 0, layout);

        Assert.True(result.Green > 0f, $"expected a visible green tint from the 90% intensity anchor, got {result.Green}");
        Assert.Equal(result.Green, result.Blue, 5);
    }

    [Fact]
    public void NeutralizingGeneralAdjustmentsLeavesTheAnchorCurveAndBlackLevelFloorAlone()
    {
        var tuning = PerimeterColourTuning.Default with
        {
            SaturationPercent = 180,
            BrightnessPercent = 158,
            RedGainPercent = 130,
            RedHueShiftDegrees = 10,
            RedBrightnessPercent = 70,
            RedIntensityPercent = 80,
            WhiteChannelStrengthPercent = 55,
        };

        var neutralized = tuning.WithGeneralAdjustmentsNeutralized();

        Assert.Equal(100, neutralized.SaturationPercent);
        Assert.Equal(100, neutralized.BrightnessPercent);
        Assert.Equal(100, neutralized.RedGainPercent);
        Assert.Equal(10, neutralized.RedHueShiftDegrees);
        Assert.Equal(70, neutralized.RedBrightnessPercent);
        Assert.Equal(80, neutralized.RedIntensityPercent);
        Assert.Equal(55, neutralized.WhiteChannelStrengthPercent);
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
