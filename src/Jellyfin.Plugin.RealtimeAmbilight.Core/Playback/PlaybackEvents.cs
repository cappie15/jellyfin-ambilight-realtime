using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;

/// <summary>
/// Jellyfin-neutral playback events consumed by <see cref="PlaybackEventCoordinator"/>.
/// The future Jellyfin adapter is responsible for mapping host event arguments.
/// </summary>
public abstract record PlaybackEvent(string SessionId);

public sealed record PlaybackSourceReference(string ItemId, string MediaSourceId, string? LiveStreamId = null);

public sealed record PlaybackStarted(string SessionId, long PositionTicks, PlaybackSourceReference? Source = null)
    : PlaybackEvent(SessionId);

public sealed record PlaybackProgressed(
    string SessionId,
    long PositionTicks,
    bool IsPaused,
    bool IsAutomated = false,
    PlaybackSourceReference? Source = null)
    : PlaybackEvent(SessionId);

public sealed record PlaybackStopped(string SessionId)
    : PlaybackEvent(SessionId);

/// <summary>
/// Input given to a session-owned analysis worker. A new request is made for a
/// start, resume, or settled seek; workers must stop when their token is cancelled.
/// </summary>
public sealed record PlaybackWorkerRequest(string SessionId, long PositionTicks, PlaybackSourceReference? Source = null);

/// <summary>
/// The future FFmpeg worker implements this interface. Its only shared queue is
/// the latest-frame handoff, so it can never back-pressure playback events.
/// </summary>
public interface IPlaybackAnalysisWorker
{
    Task RunAsync(
        PlaybackWorkerRequest request,
        LatestFrameBuffer<AnalysisFrame> latestFrames,
        CancellationToken cancellationToken);
}

public sealed class PlaybackCoordinatorOptions
{
    public TimeSpan DriftTolerance { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan SeekDiscontinuityThreshold { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan HardwareSeekDebounce { get; init; } = TimeSpan.FromSeconds(1.5);

    public TimeSpan SoftwareSeekDebounce { get; init; } = TimeSpan.FromSeconds(2);

    public bool UsesHardwareDecoder { get; init; }

    internal TimeSpan SeekDebounce => UsesHardwareDecoder ? HardwareSeekDebounce : SoftwareSeekDebounce;

    internal void Validate()
    {
        if (DriftTolerance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DriftTolerance));
        }

        if (SeekDiscontinuityThreshold <= DriftTolerance)
        {
            throw new ArgumentOutOfRangeException(nameof(SeekDiscontinuityThreshold), "The seek threshold must exceed the drift tolerance.");
        }

        if (HardwareSeekDebounce <= TimeSpan.Zero || SoftwareSeekDebounce <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(HardwareSeekDebounce), "Seek debounce windows must be positive.");
        }
    }
}
