using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Output;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Output;

/// <summary>
/// The stateless sibling of <see cref="DitheredRgbw32Encoder"/> -- same white
/// extraction and compensation maths, but every channel is plainly rounded so
/// a held, unchanging target encodes to the exact same bytes every time,
/// which is what a calibration preview needs and dithering cannot give it.
/// </summary>
public sealed class Rgbw32EncoderTests
{
    [Fact]
    public void ExtractsTheSharedGreyComponentIntoTheFourthChannel()
    {
        var rgbw = Rgbw32Encoder.Encode([new LinearRgb(0.8f, 0.5f, 0.2f)], Rgb24Encoding.Linear);

        Assert.Equal((byte)Math.Round((0.8f - 0.2f) * 255f), rgbw[0]);
        Assert.Equal((byte)Math.Round((0.5f - 0.2f) * 255f), rgbw[1]);
        Assert.Equal((byte)Math.Round((0.2f - 0.2f) * 255f), rgbw[2]);
        Assert.Equal((byte)Math.Round(0.2f * 255f), rgbw[3]);
    }

    [Fact]
    public void PureWhiteGoesEntirelyToTheWhiteChannel()
    {
        var rgbw = Rgbw32Encoder.Encode([new LinearRgb(1f, 1f, 1f)], Rgb24Encoding.Linear);

        Assert.Equal(0, rgbw[0]);
        Assert.Equal(0, rgbw[1]);
        Assert.Equal(0, rgbw[2]);
        Assert.Equal(255, rgbw[3]);
    }

    [Fact]
    public void RepeatedCallsWithAnUnrepresentableTargetProduceIdenticalBytesEveryTime()
    {
        // Exactly the scenario that made the dithered encoder alternate: a
        // residual (110.4/255) that cannot be represented in a whole byte.
        // Unlike DitheredRgbw32EncoderTests' equivalent case, every call here
        // must return the same bytes -- a calibration swatch held at one
        // target must not visibly flicker between two adjacent values while
        // the operator is judging it.
        var target = new LinearRgb(110.4f / 255f, 100f / 255f, 100f / 255f);

        var outputs = Enumerable.Range(0, 10)
            .Select(_ => Rgbw32Encoder.Encode([target], Rgb24Encoding.Linear))
            .ToArray();

        Assert.All(outputs, output => Assert.Equal(outputs[0], output));
    }
}
