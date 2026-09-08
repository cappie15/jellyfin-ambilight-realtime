using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Output;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Output;

public sealed class DitheredRgbw32EncoderTests
{
    [Fact]
    public void ExtractsTheSharedGreyComponentIntoTheFourthChannel()
    {
        var encoder = new DitheredRgbw32Encoder();

        var rgbw = encoder.Encode([new LinearRgb(0.8f, 0.5f, 0.2f)], Rgb24Encoding.Linear);

        // white = min(r, g, b) = 0.2; each colour channel keeps only its
        // excess over that shared grey, and the grey itself lands on w.
        Assert.Equal((byte)Math.Round((0.8f - 0.2f) * 255f), rgbw[0]);
        Assert.Equal((byte)Math.Round((0.5f - 0.2f) * 255f), rgbw[1]);
        Assert.Equal((byte)Math.Round((0.2f - 0.2f) * 255f), rgbw[2]);
        Assert.Equal((byte)Math.Round(0.2f * 255f), rgbw[3]);
    }

    [Fact]
    public void PureWhiteGoesEntirelyToTheWhiteChannel()
    {
        var encoder = new DitheredRgbw32Encoder();

        var rgbw = encoder.Encode([new LinearRgb(1f, 1f, 1f)], Rgb24Encoding.Linear);

        Assert.Equal(0, rgbw[0]);
        Assert.Equal(0, rgbw[1]);
        Assert.Equal(0, rgbw[2]);
        Assert.Equal(255, rgbw[3]);
    }

    [Fact]
    public void CarriesErrorBetweenSeparateEncodeCallsOnTheColourResidualOnly()
    {
        var encoder = new DitheredRgbw32Encoder();
        // white = min(r, g, b) = 100/255 exactly, leaving a red residual of
        // 10.4/255 -- non-representable, and green/blue residuals of exactly
        // zero. Isolates that the colour channels still dither the residual
        // they actually carry, unaffected by white no longer doing so.
        var target = new LinearRgb(110.4f / 255f, 100f / 255f, 100f / 255f);

        var outputs = Enumerable.Range(0, 10)
            .Select(_ => encoder.Encode([target], Rgb24Encoding.Linear))
            .ToArray();

        Assert.Contains(outputs, frame => frame[0] != outputs[0][0]);
    }

    [Fact]
    public void AConstantWhiteTargetNeverFlickersUnlikeDithering()
    {
        // Exactly the case a held calibration photo or a static scene
        // produces: a perfectly unchanging fractional target. Error-diffusion
        // dithering (as used for the colour residual, and as RGB24 uses
        // throughout) must alternate two adjacent byte values to represent
        // this on average -- which is fine spread across three channels, but
        // reads as a distinct blink once it is the one white channel doing
        // it alone. Plain rounding cannot flicker here by construction.
        var encoder = new DitheredRgbw32Encoder();
        var target = new LinearRgb(10.4f / 255f, 10.4f / 255f, 10.4f / 255f);

        var whiteBytes = Enumerable.Range(0, 90)
            .Select(_ => encoder.Encode([target], Rgb24Encoding.Linear)[3])
            .ToArray();

        Assert.All(whiteBytes, value => Assert.Equal(whiteBytes[0], value));
    }

    [Fact]
    public void PlainRoundingHalvesTheWhiteFlipRateUnderRealisticSamplingNoiseComparedToDithering()
    {
        // A real sampled edge is never perfectly constant frame to frame --
        // compression noise and motion nudge it slightly even in a mostly
        // static scene. Measured against that kind of input (not just a
        // frozen target), plain rounding still changes the output byte far
        // less often than temporally dithering the same values would.
        var random = new Random(42);
        var noisyGreys = Enumerable.Range(0, 90)
            .Select(_ => 10.4f + ((float)(random.NextDouble() * 0.6) - 0.3f))
            .Select(value => new LinearRgb(value / 255f, value / 255f, value / 255f))
            .ToArray();

        var plainRounded = new DitheredRgbw32Encoder();
        var plainFlips = CountFlips(noisyGreys.Select(colour => plainRounded.Encode([colour], Rgb24Encoding.Linear)[3]));

        var ditheredEquivalent = new DitheredRgb24Encoder();
        var ditheredFlips = CountFlips(noisyGreys.Select(colour => ditheredEquivalent.Encode([colour], Rgb24Encoding.Linear)[0]));

        Assert.True(plainFlips < ditheredFlips, $"expected fewer flips plain-rounded ({plainFlips}) than dithered ({ditheredFlips})");
    }

    private static int CountFlips(IEnumerable<byte> sequence)
    {
        var flips = 0;
        byte? previous = null;
        foreach (var value in sequence)
        {
            if (previous is { } last && last != value)
            {
                flips++;
            }

            previous = value;
        }

        return flips;
    }
}
