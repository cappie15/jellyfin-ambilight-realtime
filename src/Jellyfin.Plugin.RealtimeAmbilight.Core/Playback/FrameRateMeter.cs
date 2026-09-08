namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;

/// <summary>
/// A cheap, allocation-free rate counter for a dashboard-facing metric. The
/// common case -- <see cref="Increment"/> called once per frame -- costs one
/// interlocked increment and a volatile read; the window rollover that turns
/// a count into a rate happens roughly once per <paramref name="window"/>
/// (default 1 s), behind a lock only that rare rollover ever takes. Built
/// specifically to sit on hot paths (frame decode, sampling, the WLED send
/// call) without measurably affecting them -- there is no per-frame
/// allocation, timestamp storage per frame, or lock in the common path.
/// </summary>
public sealed class FrameRateMeter
{
    private readonly IMonotonicTime _clock;
    private readonly long _windowTicks;
    private readonly object _rolloverLock = new();
    private long _countInWindow;
    private long _windowStartTicks;
    private double _lastRateHz;

    public FrameRateMeter(IMonotonicTime clock, TimeSpan? window = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _windowTicks = (window ?? TimeSpan.FromSeconds(1)).Ticks;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_windowTicks, 0);
        _windowStartTicks = clock.Elapsed.Ticks;
    }

    /// <summary>Records one frame. Call exactly once per frame counted.</summary>
    public void Increment()
    {
        Interlocked.Increment(ref _countInWindow);
        var nowTicks = _clock.Elapsed.Ticks;
        if (nowTicks - Volatile.Read(ref _windowStartTicks) >= _windowTicks)
        {
            RollOver(nowTicks);
        }
    }

    /// <summary>
    /// The most recently completed window's rate in Hz. Zero before the
    /// first window completes. Does not decay on its own once frames stop
    /// arriving -- nothing calls <see cref="Increment"/> to trigger a
    /// rollover, so a stalled stream keeps reporting its last real rate
    /// rather than trailing off to zero. This meter answers "how fast is it
    /// actually running right now", not "is it still running at all"; a
    /// caller that also needs staleness detection (e.g. hiding the value
    /// once playback stops) has that information already, from whether a
    /// session is active, and should use it instead of asking this class to
    /// guess from elapsed time.
    /// </summary>
    public double RateHz => Volatile.Read(ref _lastRateHz);

    private void RollOver(long nowTicks)
    {
        lock (_rolloverLock)
        {
            var elapsedTicks = nowTicks - _windowStartTicks;
            if (elapsedTicks < _windowTicks)
            {
                return; // another thread already rolled this window over
            }

            var count = Interlocked.Exchange(ref _countInWindow, 0);
            var elapsedSeconds = elapsedTicks / (double)TimeSpan.TicksPerSecond;
            _lastRateHz = elapsedSeconds > 0 ? count / elapsedSeconds : 0d;
            Volatile.Write(ref _windowStartTicks, nowTicks);
        }
    }
}
