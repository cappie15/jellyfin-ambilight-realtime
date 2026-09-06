namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;

/// <summary>
/// Extrapolates the local playback position between authoritative client
/// updates. It deliberately knows nothing about Jellyfin event types.
/// </summary>
public sealed class PlaybackClock
{
    private readonly IMonotonicTime _time;
    private long _positionTicks;
    private TimeSpan _updatedAt;
    private bool _isRunning;

    public PlaybackClock(IMonotonicTime time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _updatedAt = time.Elapsed;
    }

    public bool IsRunning => _isRunning;

    public long PositionTicks
    {
        get
        {
            if (!_isRunning)
            {
                return _positionTicks;
            }

            return checked(_positionTicks + (_time.Elapsed - _updatedAt).Ticks);
        }
    }

    public void Start(long positionTicks)
    {
        _positionTicks = ValidatePosition(positionTicks);
        _updatedAt = _time.Elapsed;
        _isRunning = true;
    }

    public void Pause(long? reportedPositionTicks = null)
    {
        _positionTicks = reportedPositionTicks is { } position
            ? ValidatePosition(position)
            : PositionTicks;
        _updatedAt = _time.Elapsed;
        _isRunning = false;
    }

    public void Resynchronize(long positionTicks, bool isRunning)
    {
        _positionTicks = ValidatePosition(positionTicks);
        _updatedAt = _time.Elapsed;
        _isRunning = isRunning;
    }

    private static long ValidatePosition(long positionTicks)
    {
        if (positionTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(positionTicks), positionTicks, "Playback position cannot be negative.");
        }

        return positionTicks;
    }
}
