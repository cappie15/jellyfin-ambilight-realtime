namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;

/// <summary>
/// Fans one analysis pipeline's frames out to any number of independent
/// one-slot consumers.
/// </summary>
/// <remarks>
/// <see cref="LatestFrameBuffer{TFrame}.TryTake"/> atomically empties the
/// buffer for whichever caller wins the race to read it -- exactly right for
/// a single consumer, and exactly wrong for two independent output paths
/// (WLED and Hue) that must each see every latest frame without stealing it
/// from the other. Each <see cref="Subscribe"/> call hands back the caller's
/// own private <see cref="LatestFrameBuffer{TFrame}"/>; publishing here
/// writes the same immutable frame instance into every subscriber's slot, so
/// a slow or absent consumer only ever replaces its own stale frame, never
/// blocks or drops another consumer's.
/// </remarks>
/// <typeparam name="TFrame">Reference-type frame payload.</typeparam>
public sealed class FanOutFrameBuffer<TFrame>
    where TFrame : class
{
    private readonly object _sync = new();
    private readonly List<LatestFrameBuffer<TFrame>> _subscribers = [];
    private readonly FrameRateMeter _publishRate;

    public FanOutFrameBuffer(IMonotonicTime? clock = null)
    {
        _publishRate = new FrameRateMeter(clock ?? new StopwatchMonotonicTime());
    }

    /// <summary>
    /// How many frames per second are actually being published here, over
    /// the most recently completed one-second window -- the analysis
    /// decoder's real throughput, dashboard-facing. See
    /// <see cref="FrameRateMeter"/> for the cost (one interlocked increment
    /// per publish) and staleness caveat (does not decay to zero on its
    /// own once publishing stops).
    /// </summary>
    public double PublishRateHz => _publishRate.RateHz;

    /// <summary>Registers a new independent one-slot consumer.</summary>
    public LatestFrameBuffer<TFrame> Subscribe()
    {
        var buffer = new LatestFrameBuffer<TFrame>();
        lock (_sync)
        {
            _subscribers.Add(buffer);
        }

        return buffer;
    }

    /// <summary>Stops fanning frames to a buffer obtained from <see cref="Subscribe"/>.</summary>
    public void Unsubscribe(LatestFrameBuffer<TFrame> buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        lock (_sync)
        {
            _subscribers.Remove(buffer);
        }
    }

    public void Publish(TFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _publishRate.Increment();
        LatestFrameBuffer<TFrame>[] targets;
        lock (_sync)
        {
            targets = [.. _subscribers];
        }

        foreach (var target in targets)
        {
            target.Publish(frame);
        }
    }

    public void Clear()
    {
        LatestFrameBuffer<TFrame>[] targets;
        lock (_sync)
        {
            targets = [.. _subscribers];
        }

        foreach (var target in targets)
        {
            target.Clear();
        }
    }
}
