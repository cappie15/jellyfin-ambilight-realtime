using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Playback;

public class LatestFrameBufferTests
{
    [Fact]
    public void PublishDropsTheOldestFrameAndRetainsOnlyTheLatest()
    {
        var buffer = new LatestFrameBuffer<byte[]>();
        var first = new byte[] { 1 };
        var latest = new byte[] { 2 };

        buffer.Publish(first);
        buffer.Publish(latest);

        Assert.True(buffer.TryTake(out var frame));
        Assert.Same(latest, frame);
        Assert.False(buffer.TryTake(out _));
    }
}
