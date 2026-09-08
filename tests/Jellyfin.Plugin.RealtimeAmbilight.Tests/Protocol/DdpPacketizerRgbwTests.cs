using Jellyfin.Plugin.RealtimeAmbilight.Core.Protocol;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Protocol;

public class DdpPacketizerRgbwTests
{
    [Fact]
    public void PacketizeWritesTheRgbw32DataTypeAndPreservesAllFourChannels()
    {
        var frame = Enumerable.Range(0, 10 * 4).Select(static value => (byte)(value % 251)).ToArray();

        var packets = DdpPacketizer.Packetize(frame, 1, bytesPerLed: 4);

        var packet = Assert.Single(packets);
        Assert.Equal(DdpPacketizer.Rgbw32, packet[2]);
        Assert.Equal(frame, packet.Skip(DdpPacketizer.HeaderLength).ToArray());
    }

    [Fact]
    public void SplitsAnRgbwFrameAcrossPacketsOnceItExceedsOnePacketsChannels()
    {
        // 1440 / 4 = 360 LEDs fit one packet exactly; one more forces a second.
        var frame = new byte[361 * 4];

        var packets = DdpPacketizer.Packetize(frame, 1, bytesPerLed: 4);

        Assert.Equal(2, packets.Count);
        Assert.All(packets, packet => Assert.Equal(DdpPacketizer.Rgbw32, packet[2]));
    }

    [Fact]
    public void RejectsAnRgbwFrameThatIsNotAWholeNumberOfQuadruplets()
    {
        Assert.Throws<ArgumentException>(() => DdpPacketizer.Packetize(new byte[6], 1, bytesPerLed: 4));
    }

    [Fact]
    public void RejectsAnUnsupportedChannelCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DdpPacketizer.Packetize(new byte[8], 1, bytesPerLed: 2));
    }
}
