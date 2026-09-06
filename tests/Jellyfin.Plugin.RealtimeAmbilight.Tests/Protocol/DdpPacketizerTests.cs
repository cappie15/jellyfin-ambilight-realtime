using System.Buffers.Binary;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Protocol;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Protocol;

public class DdpPacketizerTests
{
    [Theory]
    [InlineData(100, 1)]
    [InlineData(480, 1)]
    [InlineData(490, 2)]
    [InlineData(491, 2)]
    [InlineData(831, 2)]
    [InlineData(832, 2)]
    [InlineData(1000, 3)]
    public void PacketizeUsesCompletePacketsWithoutTruncation(int ledCount, int expectedPacketCount)
    {
        var frame = CreateFrame(ledCount);

        var packets = DdpPacketizer.Packetize(frame, 14);

        Assert.Equal(expectedPacketCount, packets.Count);
        Assert.Equal(frame, packets.SelectMany(static packet => packet.Skip(DdpPacketizer.HeaderLength)).ToArray());
        Assert.All(packets.Take(packets.Count - 1), packet => Assert.Equal(DdpPacketizer.Version1, packet[0]));
        Assert.Equal((byte)(DdpPacketizer.Version1 | DdpPacketizer.Push), packets[^1][0]);
    }

    [Fact]
    public void PacketizeWritesBigEndianOffsetsLengthsAndSequences()
    {
        var packets = DdpPacketizer.Packetize(CreateFrame(491), 15);

        Assert.Equal((byte)15, packets[0][1]);
        Assert.Equal((byte)1, packets[1][1]);
        Assert.Equal((uint)0, BinaryPrimitives.ReadUInt32BigEndian(packets[0].AsSpan(4, 4)));
        Assert.Equal((uint)1440, BinaryPrimitives.ReadUInt32BigEndian(packets[1].AsSpan(4, 4)));
        Assert.Equal((ushort)1440, BinaryPrimitives.ReadUInt16BigEndian(packets[0].AsSpan(8, 2)));
        Assert.Equal((ushort)33, BinaryPrimitives.ReadUInt16BigEndian(packets[1].AsSpan(8, 2)));
    }

    [Fact]
    public void PacketizeRejectsMalformedRgb24Frame()
    {
        Assert.Throws<ArgumentException>(() => DdpPacketizer.Packetize(new byte[2]));
    }

    private static byte[] CreateFrame(int ledCount)
        => Enumerable.Range(0, ledCount * 3).Select(static value => (byte)(value % 251)).ToArray();
}
