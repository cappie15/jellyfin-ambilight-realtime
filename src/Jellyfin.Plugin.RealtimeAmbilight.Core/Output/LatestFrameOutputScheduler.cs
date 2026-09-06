using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Output;

/// <summary>
/// Pulls only the newest decoded frame on each output tick. A slow output can
/// therefore reduce visual update rate but cannot build latency into playback.
/// </summary>
public sealed class LatestFrameOutputScheduler
{
    private readonly LatestFrameBuffer<AnalysisFrame> _latestFrames;
    private readonly AmbilightFrameProcessor _processor;
    private readonly ILedFrameOutput _output;

    public LatestFrameOutputScheduler(
        LatestFrameBuffer<AnalysisFrame> latestFrames,
        AmbilightFrameProcessor processor,
        ILedFrameOutput output)
    {
        _latestFrames = latestFrames ?? throw new ArgumentNullException(nameof(latestFrames));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <returns>True only when a newly decoded frame was sent.</returns>
    public async Task<bool> SendLatestAsync(CancellationToken cancellationToken)
    {
        if (!TryTakeProcessedFrame(out var rgb24Frame, out _) || rgb24Frame is null)
        {
            return false;
        }

        await SendFrameAsync(rgb24Frame, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Processes one newest frame without sending it, reporting the media
    /// position it depicts so output can schedule it against playback rather
    /// than against the moment it happened to arrive.
    /// </summary>
    public bool TryTakeProcessedFrame(out byte[]? rgb24Frame, out long positionTicks)
    {
        if (!_latestFrames.TryTake(out var frame) || frame is null)
        {
            rgb24Frame = null;
            positionTicks = 0;
            return false;
        }

        rgb24Frame = _processor.Process(frame);
        positionTicks = frame.PositionTicks;
        return true;
    }

    public Task SendFrameAsync(ReadOnlyMemory<byte> rgb24Frame, CancellationToken cancellationToken)
        => _output.SendFrameAsync(rgb24Frame, cancellationToken);

    public async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await SendLatestAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
