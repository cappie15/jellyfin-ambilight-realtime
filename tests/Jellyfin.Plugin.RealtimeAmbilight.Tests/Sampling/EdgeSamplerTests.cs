using Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Sampling;

public class EdgeSamplerTests
{
    [Fact]
    public void ReferenceLetterboxGeometryUsesTheActivePictureForGuardAndDepth()
    {
        var zones = EdgeSampler.CreateZones(
            frameWidth: 960,
            frameHeight: 540,
            new CropInsets(Top: 69, Right: 0, Bottom: 70, Left: 0),
            new LogicalSamplingLayout(100, 56, 100, 56));

        Assert.Equal(new SamplingZone(2, 71, 11, 91), zones.Top[0]);
        Assert.Equal(new SamplingZone(938, 71, 958, 78), zones.Right[0]);
        Assert.Equal(new SamplingZone(949, 448, 958, 468), zones.Bottom[0]);
        Assert.Equal(new SamplingZone(2, 461, 22, 468), zones.Left[0]);
    }

    [Fact]
    public void RunsProceedClockwiseAndCornersOverlap()
    {
        var zones = EdgeSampler.CreateZones(
            frameWidth: 100,
            frameHeight: 100,
            new CropInsets(0, 0, 0, 0),
            new LogicalSamplingLayout(2, 2, 2, 2));

        Assert.True(zones.Top[0].Left < zones.Top[1].Left);
        Assert.True(zones.Right[0].Top < zones.Right[1].Top);
        Assert.True(zones.Bottom[0].Left > zones.Bottom[1].Left);
        Assert.True(zones.Left[0].Top > zones.Left[1].Top);
        Assert.True(Overlaps(zones.Top[1], zones.Right[0]));
    }

    [Fact]
    public void SampleBgraAveragesInLinearLight()
    {
        const int width = 20;
        const int height = 20;
        var frame = new byte[width * height * 4];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var offset = pixel * 4;
            frame[offset] = 16;
            frame[offset + 1] = 16;
            frame[offset + 2] = 235;
            frame[offset + 3] = 255;
        }

        var samples = EdgeSampler.SampleBgra(frame, width, height, new CropInsets(), new LogicalSamplingLayout(2, 2, 2, 2));

        Assert.All(samples.Top, sample =>
        {
            Assert.InRange(sample.Red, 0.999f, 1.001f);
            Assert.InRange(sample.Green, -0.001f, 0.001f);
            Assert.InRange(sample.Blue, -0.001f, 0.001f);
        });
    }

    private static bool Overlaps(SamplingZone first, SamplingZone second)
        => first.Left < second.Right && second.Left < first.Right && first.Top < second.Bottom && second.Top < first.Bottom;
}
