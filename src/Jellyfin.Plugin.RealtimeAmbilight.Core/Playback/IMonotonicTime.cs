using System.Diagnostics;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;

/// <summary>
/// A monotonic source of elapsed time. Implementations must not use wall-clock
/// time because NTP or manual clock changes must not move playback.
/// </summary>
public interface IMonotonicTime
{
    TimeSpan Elapsed { get; }
}

/// <summary>
/// Production monotonic time source backed by <see cref="Stopwatch"/>.
/// </summary>
public sealed class StopwatchMonotonicTime : IMonotonicTime
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public TimeSpan Elapsed => _stopwatch.Elapsed;
}
