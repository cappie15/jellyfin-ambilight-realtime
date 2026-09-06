namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;

/// <summary>
/// A one-element, thread-safe handoff. Publishing a new frame atomically drops
/// the old one, making accumulated analysis latency structurally impossible.
/// </summary>
/// <typeparam name="TFrame">Reference-type frame payload.</typeparam>
public sealed class LatestFrameBuffer<TFrame>
    where TFrame : class
{
    private TFrame? _latest;

    public void Publish(TFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Interlocked.Exchange(ref _latest, frame);
    }

    public bool TryTake(out TFrame? frame)
    {
        frame = Interlocked.Exchange(ref _latest, null);
        return frame is not null;
    }

    public void Clear() => Interlocked.Exchange(ref _latest, null);
}
