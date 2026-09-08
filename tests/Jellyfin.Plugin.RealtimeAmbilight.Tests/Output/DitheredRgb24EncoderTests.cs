using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Output;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Output;

public sealed class DitheredRgb24EncoderTests
{
    [Fact]
    public void MatchesPlainRoundingOnTheFirstFrame()
    {
        var encoder = new DitheredRgb24Encoder();

        var dithered = encoder.Encode([new LinearRgb(0.2f, 0.5f, 1f)], Rgb24Encoding.Linear);
        var plain = Rgb24Encoder.Encode([new LinearRgb(0.2f, 0.5f, 1f)], Rgb24Encoding.Linear);

        Assert.Equal(plain, dithered);
    }

    [Fact]
    public void AConstantFractionalTargetAveragesToTheTrueValueAcrossFrames()
    {
        var encoder = new DitheredRgb24Encoder();
        // 10.4 of 255: not representable by a single byte, the exact case a
        // slow fade toward black produces frame after frame.
        var target = new LinearRgb(10.4f / 255f, 0f, 0f);

        var outputs = new List<byte>();
        for (var frame = 0; frame < 200; frame++)
        {
            outputs.Add(encoder.Encode([target], Rgb24Encoding.Linear)[0]);
        }

        Assert.All(outputs, value => Assert.InRange(value, (byte)10, (byte)11));
        Assert.Contains((byte)10, outputs);
        Assert.Contains((byte)11, outputs);
        Assert.Equal(10.4, outputs.Select(value => (double)value).Average(), 1);
    }

    [Fact]
    public void CarriesErrorBetweenSeparateEncodeCalls()
    {
        var encoder = new DitheredRgb24Encoder();
        var target = new LinearRgb(10.4f / 255f, 0f, 0f);

        var outputs = Enumerable.Range(0, 10)
            .Select(_ => encoder.Encode([target], Rgb24Encoding.Linear)[0])
            .ToArray();

        // If error were discarded between calls (a plain per-frame round),
        // every one of these would be the same byte. Carrying it forward
        // means a slow, unchanging fractional target still alternates.
        Assert.Contains(outputs, value => value != outputs[0]);
    }
}
