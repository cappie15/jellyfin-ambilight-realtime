using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Color;

public class LinearLightInterpolatorTests
{
    [Fact]
    public void ResamplePreservesEndpointsAndUsesTheLinearLightMidpoint()
    {
        var frame = LinearLightInterpolator.Resample(
            [new LinearRgb(0f, 0f, 0f), new LinearRgb(1f, 0.5f, 0.25f)],
            physicalLedCount: 3);

        Assert.Equal(new LinearRgb(0f, 0f, 0f), frame[0]);
        Assert.Equal(new LinearRgb(0.5f, 0.25f, 0.125f), frame[1]);
        Assert.Equal(new LinearRgb(1f, 0.5f, 0.25f), frame[2]);
    }

    [Fact]
    public void ResampleDoesNotClampLinearLightValues()
    {
        var frame = LinearLightInterpolator.Resample(
            [new LinearRgb(-1f, 0f, 2f), new LinearRgb(1f, 2f, 4f)],
            physicalLedCount: 3);

        Assert.Equal(new LinearRgb(0f, 1f, 3f), frame[1]);
    }

    [Fact]
    public void InterpolatePerimeterKeepsSidesSeparateAndUsesClockwiseFrameOrder()
    {
        var frame = LinearLightInterpolator.InterpolatePerimeter(
            new LedLayout(2, 3, 2, 1),
            new LogicalSamplingLayout(2, 2, 2, 2),
            [new LinearRgb(1f, 0f, 0f), new LinearRgb(2f, 0f, 0f)],
            [new LinearRgb(0f, 1f, 0f), new LinearRgb(0f, 2f, 0f)],
            [new LinearRgb(0f, 0f, 1f), new LinearRgb(0f, 0f, 2f)],
            [new LinearRgb(1f, 1f, 0f), new LinearRgb(2f, 2f, 0f)]);

        Assert.Equal(
        [
            new LinearRgb(1f, 0f, 0f), new LinearRgb(2f, 0f, 0f),
            new LinearRgb(0f, 1f, 0f), new LinearRgb(0f, 1.5f, 0f), new LinearRgb(0f, 2f, 0f),
            new LinearRgb(0f, 0f, 1f), new LinearRgb(0f, 0f, 2f),
            new LinearRgb(1f, 1f, 0f),
        ],
        frame);
    }

    [Fact]
    public void InterpolatePerimeterRejectsASampleCountThatDoesNotMatchTheLayout()
    {
        Assert.Throws<ArgumentException>(() => LinearLightInterpolator.InterpolatePerimeter(
            new LedLayout(1, 1, 1, 1),
            new LogicalSamplingLayout(2, 2, 2, 2),
            [new LinearRgb(0f, 0f, 0f)],
            [new LinearRgb(0f, 0f, 0f), new LinearRgb(0f, 0f, 0f)],
            [new LinearRgb(0f, 0f, 0f), new LinearRgb(0f, 0f, 0f)],
            [new LinearRgb(0f, 0f, 0f), new LinearRgb(0f, 0f, 0f)]));
    }
}
