using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
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

    /// <summary>Passthrough to the wrapped <see cref="AmbilightFrameProcessor"/>'s own rate, dashboard-facing.</summary>
    public double ProcessRateHz => _processor.ProcessRateHz;

    /// <returns>True only when a newly decoded frame was sent.</returns>
    public async Task<bool> SendLatestAsync(CancellationToken cancellationToken)
    {
        if (!TrySampleFrame(out var target, out _) || target is null)
        {
            return false;
        }

        await SendFrameAsync(EncodeTarget(target), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Samples one newest frame without encoding or sending it, reporting the
    /// media position it depicts so output can schedule it against playback
    /// rather than against the moment it happened to arrive. Deliberately
    /// stops short of dwell/smoothing/encoding -- see
    /// <see cref="AmbilightFrameProcessor.Sample"/>'s own remarks for why the
    /// analysis decoder's own lead makes sample time the wrong moment for that.
    /// </summary>
    public bool TrySampleFrame(out LinearRgb[]? target, out long positionTicks)
    {
        if (!_latestFrames.TryTake(out var frame) || frame is null)
        {
            target = null;
            positionTicks = 0;
            return false;
        }

        target = _processor.Sample(frame);
        positionTicks = frame.PositionTicks;
        return true;
    }

    /// <summary>
    /// Applies dwell/smoothing/encoding to a target sampled by
    /// <see cref="TrySampleFrame"/>, once it actually becomes due -- see
    /// <see cref="AmbilightFrameProcessor.EncodeTarget"/>.
    /// </summary>
    public byte[] EncodeTarget(LinearRgb[] target) => _processor.EncodeTarget(target);

    /// <summary>
    /// Re-encodes the last due target without a new one due yet -- see
    /// <see cref="AmbilightFrameProcessor.ProcessRepeat"/>. Lets smoothing
    /// and the encoder's own temporal dithering keep advancing every output
    /// tick even when the source video's own frame rate is lower than the
    /// configured output rate. Returns <see langword="null"/> before the first
    /// frame of a session has become due.
    /// </summary>
    public byte[]? TryRepeatProcessedFrame() => _processor.ProcessRepeat();

    /// <summary>Forgets the last due target -- see <see cref="AmbilightFrameProcessor.ClearTarget"/>.</summary>
    public void ClearTarget() => _processor.ClearTarget();

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
