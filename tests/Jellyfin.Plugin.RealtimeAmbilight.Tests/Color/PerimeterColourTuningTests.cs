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
