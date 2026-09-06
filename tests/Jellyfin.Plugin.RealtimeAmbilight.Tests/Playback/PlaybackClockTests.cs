using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Playback;

public class PlaybackClockTests
{
    [Fact]
    public void ClockAdvancesOnlyFromMonotonicTimeAndHoldsWhilePaused()
    {
        var time = new ManualMonotonicTime();
        var clock = new PlaybackClock(time);

        clock.Start(TimeSpan.FromMinutes(2).Ticks);
        time.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromMinutes(2).Ticks + TimeSpan.FromSeconds(3).Ticks, clock.PositionTicks);

        clock.Pause();
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromMinutes(2).Ticks + TimeSpan.FromSeconds(3).Ticks, clock.PositionTicks);
    }

    [Fact]
    public void ResynchronizeCanCorrectPositionWithoutLosingRunningState()
    {
        var time = new ManualMonotonicTime();
        var clock = new PlaybackClock(time);

        clock.Start(0);
        time.Advance(TimeSpan.FromSeconds(1));
        clock.Resynchronize(TimeSpan.FromSeconds(10).Ticks, isRunning: true);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(11).Ticks, clock.PositionTicks);
        Assert.True(clock.IsRunning);
    }

    private sealed class ManualMonotonicTime : IMonotonicTime
    {
        public TimeSpan Elapsed { get; private set; }

        public void Advance(TimeSpan duration) => Elapsed += duration;
    }
}
