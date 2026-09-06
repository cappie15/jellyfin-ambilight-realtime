using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Output;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Output;

/// <summary>
/// Output schedules frames against the playback timeline, so the media position
/// a frame depicts has to survive the whole handoff from decoder to sender.
/// Losing it silently degrades to the old behaviour: correct-looking output that
/// drifts away from the picture.
/// </summary>
public sealed class FrameSchedulingTests
{
    [Fact]
    public void AnalysisFrameCarriesTheMediaPositionItDepicts()
    {
        var frame = new AnalysisFrame(new byte[16 * 16 * 4], 16, 16, TimeSpan.FromSeconds(90).Ticks);

        Assert.Equal(TimeSpan.FromSeconds(90).Ticks, frame.PositionTicks);
    }

    [Fact]
    public void TakingAProcessedFrameReportsItsMediaPosition()
    {
        var buffer = new LatestFrameBuffer<AnalysisFrame>();
        var layout = new LedLayout(4, 2, 4, 2);
        var scheduler = new LatestFrameOutputScheduler(
            buffer,
            new AmbilightFrameProcessor(layout, LogicalSamplingLayout.FromPhysicalLayout(layout), _ => default),
            new NullOutput());

        Assert.False(scheduler.TryTakeProcessedFrame(out _, out _));

        buffer.Publish(new AnalysisFrame(new byte[16 * 16 * 4], 16, 16, TimeSpan.FromMinutes(3).Ticks));

        Assert.True(scheduler.TryTakeProcessedFrame(out var rgb24Frame, out var positionTicks));
        Assert.NotNull(rgb24Frame);
        Assert.Equal(TimeSpan.FromMinutes(3).Ticks, positionTicks);
    }

    [Fact]
    public void TheDecoderLeadExceedsTheMeasuredHdrStartUpCost()
    {
        // The HDR graph needed about 1.1 s to yield its first frame on the
        // reference host. A lead below that leaves every restart permanently late.
        Assert.True(FfmpegAnalysisWorker.DecoderLead >= TimeSpan.FromMilliseconds(1500));
    }

    private sealed class NullOutput : ILedFrameOutput
    {
        public Task SendFrameAsync(ReadOnlyMemory<byte> rgb24Frame, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
