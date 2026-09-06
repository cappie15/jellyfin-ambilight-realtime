using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Color;

public sealed class ColourAdjustmentTests
{
    [Fact]
    public void BrightnessScalesLightNotEncodedValues()
    {
        var halved = new ColourAdjustment(0.5f, 1f).Apply(new LinearRgb(0.4f, 0.2f, 0.1f));

        Assert.Equal(0.2f, halved.Red, 5);
        Assert.Equal(0.1f, halved.Green, 5);
        Assert.Equal(0.05f, halved.Blue, 5);
    }

    [Fact]
    public void SaturationLeavesGreyAlone()
    {
        // Grey has no hue to push away from its own luminance, so it must survive
        // any saturation setting untouched rather than drifting towards a cast.
        var grey = new ColourAdjustment(1f, 1.8f).Apply(new LinearRgb(0.3f, 0.3f, 0.3f));

        Assert.Equal(0.3f, grey.Red, 5);
        Assert.Equal(0.3f, grey.Green, 5);
        Assert.Equal(0.3f, grey.Blue, 5);
    }

    [Fact]
    public void SaturationDeepensAColourWithoutChangingItsLuminance()
    {
        var colour = new LinearRgb(0.1f, 0.2f, 0.6f);
        var deepened = new ColourAdjustment(1f, 1.5f).Apply(colour);

        static float Luminance(LinearRgb c) => (0.2126f * c.Red) + (0.7152f * c.Green) + (0.0722f * c.Blue);

        Assert.Equal(Luminance(colour), Luminance(deepened), 4);
        Assert.True(deepened.Blue > colour.Blue);
        Assert.True(deepened.Red < colour.Red);
    }

    [Fact]
    public void ClampingKeepsAnOverdrivenColourInRange()
    {
        var pushed = new ColourAdjustment(1f, 2f).Apply(new LinearRgb(0.05f, 0.05f, 0.9f));

        Assert.InRange(pushed.Red, 0f, 1f);
        Assert.InRange(pushed.Blue, 0f, 1f);
    }

    [Fact]
    public void IdentityIsRecognisedSoTheFrameIsNotWalked()
    {
        Assert.True(ColourAdjustment.None.IsIdentity);
        Assert.True(ColourAdjustment.FromPercentages(100, 100).IsIdentity);
        Assert.False(ColourAdjustment.FromPercentages(60, 100).IsIdentity);
    }
}
