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

    [Fact]
    public void FullExtractionFactorIsByteIdenticalToTheDefault()
    {
        var withDefault = new DitheredRgbw32Encoder();
        var withExplicitOne = new DitheredRgbw32Encoder();
        var colour = new LinearRgb(0.8f, 0.5f, 0.2f);

        var defaultResult = withDefault.Encode([colour], Rgb24Encoding.Linear);
        var explicitResult = withExplicitOne.Encode([colour], Rgb24Encoding.Linear, whiteExtractionFactor: 1f);

        Assert.Equal(defaultResult, explicitResult);
    }

    [Fact]
    public void ReducingTheExtractionFactorMovesGreyFromWhiteToTheColourResidual()
    {
        // A calibration-shifted near-white pixel: red pushed up, blue pushed
        // down, green untouched -- exactly the White step's own red/blue
        // push-pull.
        var colour = new LinearRgb(0.55f, 0.5f, 0.45f);

        var full = new DitheredRgbw32Encoder().Encode([colour], Rgb24Encoding.Linear, whiteExtractionFactor: 1f);
        var tapered = new DitheredRgbw32Encoder().Encode([colour], Rgb24Encoding.Linear, whiteExtractionFactor: 0.3f);

        Assert.True(tapered[3] < full[3], $"expected less white at a lower extraction factor: tapered={tapered[3]}, full={full[3]}");
        // At least one colour channel must have picked up the difference.
        Assert.True(tapered[0] > full[0] || tapered[1] > full[1] || tapered[2] > full[2]);
    }

    [Fact]
    public void NoExtractionSendsThePixelAsAPureColourResidualWithNothingOnWhite()
    {
        var colour = new LinearRgb(0.55f, 0.5f, 0.45f);

        var pureResidual = new DitheredRgbw32Encoder().Encode([colour], Rgb24Encoding.Linear, whiteExtractionFactor: 0f);

        Assert.Equal(0, pureResidual[3]);
    }

    [Fact]
    public void TotalCombinedOutputDoesNotIncreaseAsTheExtractionFactorIsTapered()
    {
        // The actual bug being fixed: a warmer/cooler White shift must not
        // make the strip's total output climb just because less of the
        // shared grey lands on the (unshiftable) white die. Checked at every
        // taper level against the same input colour.
        var colour = new LinearRgb(0.7f, 0.5f, 0.3f);
        var fullTotal = SumRgbw(new DitheredRgbw32Encoder().Encode([colour], Rgb24Encoding.Linear, whiteExtractionFactor: 1f));

        foreach (var factor in new[] { 0.75f, 0.5f, 0.25f, 0f })
        {
            var total = SumRgbw(new DitheredRgbw32Encoder().Encode([colour], Rgb24Encoding.Linear, whiteExtractionFactor: factor));
            Assert.True(total <= fullTotal + 1, $"factor {factor}: total {total} exceeded the full-extraction total {fullTotal}");
        }
    }

    [Fact]
    public void WhiteChannelCeilingLeavesTheRestOfABrightGreyOnTheColourResidual()
    {
        // A near-white pixel above the ceiling: only the ceiling's worth
        // goes to white, the rest stays on red/green/blue rather than being
        // subtracted out and lost -- the whole point of the ceiling.
        var colour = new LinearRgb(0.9f, 0.9f, 0.9f);

        var uncapped = new DitheredRgbw32Encoder().Encode([colour], Rgb24Encoding.Linear, whiteExtractionFactor: 1f, whiteChannelCeiling: 1f);
        var capped = new DitheredRgbw32Encoder().Encode([colour], Rgb24Encoding.Linear, whiteExtractionFactor: 1f, whiteChannelCeiling: 0.5f);

        Assert.Equal(0, uncapped[0]); // fully extracted today: colour channels hollowed out
        Assert.Equal((byte)Math.Round(0.5f * 255f), capped[3]); // white pinned at the ceiling
        Assert.True(capped[0] > uncapped[0], $"expected the capped run to leave more on red: capped={capped[0]}, uncapped={uncapped[0]}");
    }

    [Fact]
    public void WhiteChannelCeilingDoesNothingBelowItself()
    {
        // A dim grey never reaches the ceiling in the first place, so
        // behaviour must be byte-identical to today's uncapped default --
        // the ceiling only ever matters for bright/near-white content.
        var colour = new LinearRgb(0.3f, 0.3f, 0.3f);

        var uncapped = new DitheredRgbw32Encoder().Encode([colour], Rgb24Encoding.Linear, whiteExtractionFactor: 1f, whiteChannelCeiling: 1f);
        var capped = new DitheredRgbw32Encoder().Encode([colour], Rgb24Encoding.Linear, whiteExtractionFactor: 1f, whiteChannelCeiling: 0.5f);

        Assert.Equal(uncapped, capped);
    }

    [Fact]
    public void WhiteChannelCeilingRaisesTotalCombinedOutputForABrightGreyInsteadOfSuppressingIt()
    {
        // Unlike the colour-temperature compensation above, the ceiling must
        // NOT be rescaled back down -- red/green/blue keeping more of their
        // own value is the fix, so total output for a bright pixel should
        // rise compared to full (uncapped) extraction, not stay pinned.
        var colour = new LinearRgb(0.9f, 0.9f, 0.9f);
        var fullTotal = SumRgbw(new DitheredRgbw32Encoder().Encode([colour], Rgb24Encoding.Linear, whiteExtractionFactor: 1f, whiteChannelCeiling: 1f));
        var cappedTotal = SumRgbw(new DitheredRgbw32Encoder().Encode([colour], Rgb24Encoding.Linear, whiteExtractionFactor: 1f, whiteChannelCeiling: 0.5f));

        Assert.True(cappedTotal > fullTotal, $"expected the capped total ({cappedTotal}) to exceed the uncapped total ({fullTotal})");
    }

    private static int SumRgbw(byte[] frame) => frame[0] + frame[1] + frame[2] + frame[3];

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
