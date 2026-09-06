using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Output;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Output;

public sealed class Rgb24EncodingTests
{
    [Fact]
    public void LinearEncodingIsProportionalToLightOutput()
    {
        var encoded = Rgb24Encoder.Encode([new LinearRgb(0.2f, 0.5f, 1f)], Rgb24Encoding.Linear);

        Assert.Equal(51, encoded[0]);
        Assert.Equal(128, encoded[1]);
        Assert.Equal(255, encoded[2]);
    }

    [Fact]
    public void Bt709EncodingLiftsShadowsAsADisplayExpects()
    {
        var encoded = Rgb24Encoder.Encode([new LinearRgb(0.2f, 0.5f, 1f)], Rgb24Encoding.Bt709);

        // The same 0.2 of light becomes 111 rather than 51. Sent to a controller
        // that does no gamma correction of its own, that is the difference
        // between a dim scene and a scene that stays stubbornly bright.
        Assert.Equal(111, encoded[0]);
        Assert.Equal(180, encoded[1]);
        Assert.Equal(255, encoded[2]);
    }

    [Fact]
    public void BlackAndWhiteAreIdenticalUnderBothEncodings()
    {
        var linear = Rgb24Encoder.Encode([new LinearRgb(0f, 0f, 0f), new LinearRgb(1f, 1f, 1f)], Rgb24Encoding.Linear);
        var bt709 = Rgb24Encoder.Encode([new LinearRgb(0f, 0f, 0f), new LinearRgb(1f, 1f, 1f)], Rgb24Encoding.Bt709);

        Assert.Equal(linear, bt709);
    }
}
