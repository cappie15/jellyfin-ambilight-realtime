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

        Assert.False(scheduler.TrySampleFrame(out _, out _));

        buffer.Publish(new AnalysisFrame(new byte[16 * 16 * 4], 16, 16, TimeSpan.FromMinutes(3).Ticks));

        Assert.True(scheduler.TrySampleFrame(out var target, out var positionTicks));
        Assert.NotNull(target);
        Assert.Equal(TimeSpan.FromMinutes(3).Ticks, positionTicks);
    }

    [Fact]
    public void EncodingASampledTargetProducesRgb24AndMakesItTheRepeatTarget()
    {
        var buffer = new LatestFrameBuffer<AnalysisFrame>();
        var layout = new LedLayout(4, 2, 4, 2);
        var scheduler = new LatestFrameOutputScheduler(
            buffer,
            new AmbilightFrameProcessor(layout, LogicalSamplingLayout.FromPhysicalLayout(layout), _ => default),
            new NullOutput());

        buffer.Publish(new AnalysisFrame(new byte[16 * 16 * 4], 16, 16));
        Assert.True(scheduler.TrySampleFrame(out var target, out _));

        var encoded = scheduler.EncodeTarget(target!);
        Assert.NotEmpty(encoded);
    }

    [Fact]
    public void RepeatingAProcessedFrameKeepsSendingTheLastDueTargetBetweenNewFrames()
    {
        var buffer = new LatestFrameBuffer<AnalysisFrame>();
        var layout = new LedLayout(4, 2, 4, 2);
        var scheduler = new LatestFrameOutputScheduler(
            buffer,
            new AmbilightFrameProcessor(layout, LogicalSamplingLayout.FromPhysicalLayout(layout), _ => default),
            new NullOutput());

        Assert.Null(scheduler.TryRepeatProcessedFrame());

        buffer.Publish(new AnalysisFrame(new byte[16 * 16 * 4], 16, 16));
        Assert.True(scheduler.TrySampleFrame(out var target, out _));

        // Sampling alone must not make a target repeatable -- only actually
        // encoding it (i.e. it having become due) may.
        Assert.Null(scheduler.TryRepeatProcessedFrame());

        scheduler.EncodeTarget(target!);
        var repeated = scheduler.TryRepeatProcessedFrame();
        Assert.NotNull(repeated);

        scheduler.ClearTarget();
        Assert.Null(scheduler.TryRepeatProcessedFrame());
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
