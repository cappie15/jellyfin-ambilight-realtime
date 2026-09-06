using Jellyfin.Plugin.RealtimeAmbilight.Core.Protocol;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Protocol;

public class HyperionRawRgbPacketizerTests
{
    [Fact]
    public void PacketizeAcceptsThe490LedBoundary()
    {
        var frame = new byte[HyperionRawRgbPacketizer.MaximumLedCount * HyperionRawRgbPacketizer.BytesPerLed];

        var packet = HyperionRawRgbPacketizer.Packetize(frame);

        Assert.Equal(frame, packet);
    }

    [Fact]
    public void PacketizeRejects491LedsRatherThanTruncating()
    {
        var frame = new byte[(HyperionRawRgbPacketizer.MaximumLedCount + 1) * HyperionRawRgbPacketizer.BytesPerLed];

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => HyperionRawRgbPacketizer.Packetize(frame));

        Assert.Contains("Use DDP", exception.Message, StringComparison.Ordinal);
    }
}
