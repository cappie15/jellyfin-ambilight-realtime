using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Output;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Output;

public sealed class DwellFilterTests
{
    [Fact]
    public void DisabledByDefaultPassesFramesThroughUnchanged()
    {
        var filter = new DwellFilter();
        var frame = new[] { new LinearRgb(1f, 0f, 0f) };

        filter.Apply(frame, minimumHoldMilliseconds: 0, elapsedMillisecondsSinceLastFrame: 1000);

        Assert.Equal(1f, frame[0].Red);
    }

    [Fact]
    public void ABriefFlashIsIgnoredUntilItHasHeldLongEnough()
    {
        var filter = new DwellFilter();
        var red = new[] { new LinearRgb(1f, 0f, 0f) };
        var blue = new[] { new LinearRgb(0f, 0f, 1f) };

        // First call establishes the committed colour with no history.
        var frame = (LinearRgb[])red.Clone();
        filter.Apply(frame, minimumHoldMilliseconds: 200, elapsedMillisecondsSinceLastFrame: 0);
        Assert.Equal(1f, frame[0].Red);

        // A one-frame flash to blue, gone well before the 200 ms threshold.
        frame = (LinearRgb[])blue.Clone();
        filter.Apply(frame, minimumHoldMilliseconds: 200, elapsedMillisecondsSinceLastFrame: 33);
        Assert.Equal(1f, frame[0].Red); // still red: the flash has not held

        // The scene reverts to red before blue ever held long enough.
        frame = (LinearRgb[])red.Clone();
        filter.Apply(frame, minimumHoldMilliseconds: 200, elapsedMillisecondsSinceLastFrame: 33);
        Assert.Equal(1f, frame[0].Red);
    }

    [Fact]
    public void AColourThatPersistsLongEnoughIsCommitted()
    {
        var filter = new DwellFilter();
        var red = new[] { new LinearRgb(1f, 0f, 0f) };
        var blue = new[] { new LinearRgb(0f, 0f, 1f) };

        var frame = (LinearRgb[])red.Clone();
        filter.Apply(frame, minimumHoldMilliseconds: 200, elapsedMillisecondsSinceLastFrame: 0);

        // Blue proposed across several frames totalling more than 200 ms.
        for (var i = 0; i < 5; i++)
        {
            frame = (LinearRgb[])blue.Clone();
            filter.Apply(frame, minimumHoldMilliseconds: 200, elapsedMillisecondsSinceLastFrame: 50);
        }

        Assert.Equal(1f, frame[0].Blue);
        Assert.Equal(0f, frame[0].Red);
    }

    [Fact]
    public void TurningTheThresholdOffMidSessionForgetsAnyPendingHold()
    {
        var filter = new DwellFilter();
        var red = new[] { new LinearRgb(1f, 0f, 0f) };
        var blue = new[] { new LinearRgb(0f, 0f, 1f) };

        var frame = (LinearRgb[])red.Clone();
        filter.Apply(frame, minimumHoldMilliseconds: 5000, elapsedMillisecondsSinceLastFrame: 0);

        // Disable mid-session: the very next frame must pass through as-is,
        // not wait out whatever hold was already accumulating.
        frame = (LinearRgb[])blue.Clone();
        filter.Apply(frame, minimumHoldMilliseconds: 0, elapsedMillisecondsSinceLastFrame: 16);

        Assert.Equal(1f, frame[0].Blue);
        Assert.Equal(0f, frame[0].Red);
    }
}
