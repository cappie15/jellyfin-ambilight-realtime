using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Color;

public sealed class HueCorrectionCurveTests
{
    private static readonly LinearRgb PureRed = new(1f, 0f, 0f);
    private static readonly LinearRgb PureGreen = new(0f, 1f, 0f);
    private static readonly LinearRgb PureBlue = new(0f, 0f, 1f);

    [Fact]
    public void IdentityCurveLeavesEveryColourUntouched()
    {
        var curve = HueCorrectionCurve.Identity;

        Assert.Equal(PureRed, curve.Apply(PureRed));
        Assert.Equal(new LinearRgb(0.2f, 0.6f, 0.1f), curve.Apply(new LinearRgb(0.2f, 0.6f, 0.1f)));
    }

    [Fact]
    public void ADimmerRedAnchorOnlyDimsRedNotGreenOrBlue()
    {
        var curve = new HueCorrectionCurve(
            red: new HueAnchor(0f, 0.5f, 1f),
            yellow: HueAnchor.Identity,
            green: HueAnchor.Identity,
            cyan: HueAnchor.Identity,
            blue: HueAnchor.Identity,
            magenta: HueAnchor.Identity);

        var red = curve.Apply(PureRed);
        var green = curve.Apply(PureGreen);
        var blue = curve.Apply(PureBlue);

        Assert.True(red.Red < 0.6f, $"expected red dimmed, got {red.Red}");
        Assert.Equal(1f, green.Green, 3);
        Assert.Equal(1f, blue.Blue, 3);
    }

    [Fact]
    public void IntensityAt100PercentHasNoEffectOnAnAlreadyFullySaturatedColour()
    {
        // A calibration swatch (like every real HSV colour at S=1) has no
        // more "saturation" left for a same-or-lower multiplier to add --
        // this is the case an operator reported as Intensity "doing
        // nothing" at exactly 100%, and it is mathematically correct that
        // it should not, for a swatch that starts fully saturated.
        var curve = new HueCorrectionCurve(
            red: HueAnchor.Identity,
            yellow: HueAnchor.Identity,
            green: HueAnchor.Identity,
            cyan: HueAnchor.Identity,
            blue: HueAnchor.Identity,
            magenta: new HueAnchor(0f, 1f, 1f));
        var pureMagenta = new LinearRgb(1f, 0f, 1f);

        Assert.Equal(pureMagenta, curve.Apply(pureMagenta));
    }

    [Fact]
    public void IntensityAboveHalfSaturationPercentPushesAnAlreadyFullySaturatedColourFurtherFromGrey()
    {
        // Above 100%, Intensity switches to a direct RGB-domain push away
        // from the colour's own luminance (the same technique the global
        // Saturation control already uses), specifically because HSV's own
        // saturation is bounded at 1.0 and cannot be pushed past that by
        // scaling the HSV parameter alone.
        var curve = new HueCorrectionCurve(
            red: HueAnchor.Identity,
            yellow: HueAnchor.Identity,
            green: HueAnchor.Identity,
            cyan: HueAnchor.Identity,
            blue: HueAnchor.Identity,
            magenta: new HueAnchor(0f, 1f, 1.5f));
        var dimMagenta = new LinearRgb(0.4f, 0f, 0.4f);

        var boosted = curve.Apply(dimMagenta);

        // Pushed further from its own luminance: red/blue rise, green (the
        // channel already below the other two) is pushed lower still.
        Assert.True(boosted.Red > dimMagenta.Red, $"expected red boosted above {dimMagenta.Red}, got {boosted.Red}");
        Assert.True(boosted.Green < dimMagenta.Green, $"expected green pushed below {dimMagenta.Green}, got {boosted.Green}");
    }

    [Fact]
    public void ARedHueShiftTowardsOrangeMovesRedTowardsYellowNotTowardsMagenta()
    {
        // +15 degrees on red should read as "a bit orange": more green
        // mixed in (which is how orange differs from pure red), no blue.
        var curve = new HueCorrectionCurve(
            red: new HueAnchor(15f, 1f, 1f),
            yellow: HueAnchor.Identity,
            green: HueAnchor.Identity,
            cyan: HueAnchor.Identity,
            blue: HueAnchor.Identity,
            magenta: HueAnchor.Identity);

        var result = curve.Apply(PureRed);

        Assert.True(result.Green > 0f, "expected some green mixed in toward orange");
        Assert.Equal(0f, result.Blue, 4);
        Assert.True(result.Red > result.Green, "still predominantly red, not fully orange/yellow");
    }

    [Fact]
    public void IsContinuousAcrossTheZeroThreeSixtyWrap()
    {
        // Give magenta (300 degrees) a real shift so there is something to
        // be discontinuous about right where the wheel wraps back to red.
        var curve = new HueCorrectionCurve(
            red: HueAnchor.Identity,
            yellow: HueAnchor.Identity,
            green: HueAnchor.Identity,
            cyan: HueAnchor.Identity,
            blue: HueAnchor.Identity,
            magenta: new HueAnchor(10f, 1.2f, 0.8f));

        // Sample a colour whose hue sits just below 360 degrees and one just
        // above (i.e. just past 0/red): the curve must not jump between them.
        var justBelow360 = FromHueDegrees(359f);
        var justAbove0 = FromHueDegrees(1f);

        var resultBelow = curve.Apply(justBelow360);
        var resultAbove = curve.Apply(justAbove0);

        Assert.True(MathF.Abs(resultBelow.Red - resultAbove.Red) < 0.05f, "red component jumped across the wrap");
        Assert.True(MathF.Abs(resultBelow.Green - resultAbove.Green) < 0.05f, "green component jumped across the wrap");
        Assert.True(MathF.Abs(resultBelow.Blue - resultAbove.Blue) < 0.05f, "blue component jumped across the wrap");
    }

    [Fact]
    public void SmoothlyBlendsBetweenTwoAdjacentAnchorsRatherThanJumping()
    {
        // Red dimmed hard, yellow untouched: a hue exactly halfway between
        // them (30 degrees, orange) should land roughly in between, not
        // equal to either endpoint.
        var curve = new HueCorrectionCurve(
            red: new HueAnchor(0f, 0.3f, 1f),
            yellow: HueAnchor.Identity,
            green: HueAnchor.Identity,
            cyan: HueAnchor.Identity,
            blue: HueAnchor.Identity,
            magenta: HueAnchor.Identity);

        var atRed = curve.Apply(FromHueDegrees(0f)).Red;
        var atOrange = curve.Apply(FromHueDegrees(30f)).Red;
        var atYellowRegion = curve.Apply(FromHueDegrees(55f)).Red;

        Assert.True(atRed < atOrange, "orange should be brighter than fully-dimmed red");
        Assert.True(atOrange < atYellowRegion + 0.05f, "should keep climbing back up toward yellow's untouched value");
    }

    [Fact]
    public void AGreyPixelIsUnaffectedByAnySingleAnchorsHueShift()
    {
        var curve = new HueCorrectionCurve(
            red: new HueAnchor(25f, 1f, 1f),
            yellow: HueAnchor.Identity,
            green: HueAnchor.Identity,
            cyan: HueAnchor.Identity,
            blue: HueAnchor.Identity,
            magenta: HueAnchor.Identity);

        var grey = new LinearRgb(0.4f, 0.4f, 0.4f);
        var result = curve.Apply(grey);

        // No saturation means no hue to rotate toward -- a large hue shift on
        // one anchor must not turn true grey into a tinted colour.
        Assert.Equal(grey.Red, result.Red, 4);
        Assert.Equal(grey.Green, result.Green, 4);
        Assert.Equal(grey.Blue, result.Blue, 4);
    }

    private static LinearRgb FromHueDegrees(float hueDegrees)
    {
        // A saturated, full-value colour at the given hue, built the same
        // way HueCorrectionCurve itself converts back from HSV, so these
        // tests exercise the real HSV round trip rather than a hand-picked
        // RGB triplet that happens to be near the target hue.
        var c = 1f;
        var x = c * (1f - MathF.Abs(((hueDegrees / 60f) % 2f) - 1f));
        return hueDegrees switch
        {
            < 60f => new LinearRgb(c, x, 0f),
            < 120f => new LinearRgb(x, c, 0f),
            < 180f => new LinearRgb(0f, c, x),
            < 240f => new LinearRgb(0f, x, c),
            < 300f => new LinearRgb(x, 0f, c),
            _ => new LinearRgb(c, 0f, x),
        };
    }
}
