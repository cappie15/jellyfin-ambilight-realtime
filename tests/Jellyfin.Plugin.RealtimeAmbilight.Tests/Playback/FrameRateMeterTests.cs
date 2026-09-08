using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Playback;

public sealed class FrameRateMeterTests
{
    [Fact]
    public void ReportsZeroBeforeTheFirstWindowCompletes()
    {
        var clock = new FakeMonotonicTime();
        var meter = new FrameRateMeter(clock, TimeSpan.FromSeconds(1));

        meter.Increment();
        meter.Increment();

        Assert.Equal(0d, meter.RateHz);
    }

    [Fact]
    public void ComputesRateFromCountOverTheCompletedWindow()
    {
        var clock = new FakeMonotonicTime();
        var meter = new FrameRateMeter(clock, TimeSpan.FromSeconds(1));

        for (var i = 0; i < 30; i++)
        {
            meter.Increment();
        }

        // The window has not elapsed yet -- still zero.
        Assert.Equal(0d, meter.RateHz);

        clock.Elapsed = TimeSpan.FromSeconds(1);
        meter.Increment(); // the rollover check runs inside Increment

        Assert.Equal(31d, meter.RateHz, 3);
    }

    [Fact]
    public void StaysAtItsLastRateOnceIncrementsStop()
    {
        // The meter has no way to know playback stopped; it only rolls a
        // window over when Increment is called. A caller wanting "nothing
        // playing" must track that separately (session state), not infer it
        // from this class -- documented behaviour, not a bug.
        var clock = new FakeMonotonicTime();
        var meter = new FrameRateMeter(clock, TimeSpan.FromSeconds(1));

        for (var i = 0; i < 10; i++)
        {
            meter.Increment();
        }

        clock.Elapsed = TimeSpan.FromSeconds(1);
        meter.Increment();
        var rateAfterFirstWindow = meter.RateHz;

        clock.Elapsed = TimeSpan.FromSeconds(60);

        Assert.Equal(rateAfterFirstWindow, meter.RateHz);
    }

    [Fact]
    public void ThrowsForANonPositiveWindow()
    {
        var clock = new FakeMonotonicTime();

        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRateMeter(clock, TimeSpan.Zero));
    }

    private sealed class FakeMonotonicTime : IMonotonicTime
    {
        public TimeSpan Elapsed { get; set; }
    }
}
