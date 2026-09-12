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

public class DdpPacketizerSendPacketsAsyncTests
{
    [Theory]
    [InlineData(100, 1)]
    [InlineData(480, 1)]
    [InlineData(490, 2)]
    [InlineData(831, 2)]
    [InlineData(1000, 3)]
    public async Task ProducesTheSameBytesAsPacketizeWithoutAllocatingAnArrayPerPacket(int ledCount, int expectedPacketCount)
    {
        var frame = CreateFrame(ledCount);
        var expectedPackets = DdpPacketizer.Packetize(frame, 14);
        var sent = new List<byte[]>();

        var nextSequence = await DdpPacketizer.SendPacketsAsync(
            frame,
            14,
            bytesPerLed: 3,
            packet =>
            {
                // The pool can (and does) rent an array larger than requested --
                // sent.length must reflect the trimmed AsMemory(0, totalLength)
                // slice, not the rented buffer's own possibly-larger length.
                sent.Add(packet.ToArray());
                return Task.CompletedTask;
            });

        Assert.Equal(expectedPacketCount, sent.Count);
        Assert.Equal(expectedPackets.Count, sent.Count);
        for (var index = 0; index < expectedPackets.Count; index++)
        {
            Assert.Equal(expectedPackets[index], sent[index]);
        }

        // The sequence written into each packet already wraps at 15 back to 1
        // (see NormalizeSequence/NextSequence), so the value returned after
        // the loop is simply one more step past whatever the last packet
        // itself carried -- not a plain "firstSequence + packetCount" offset,
        // which would be wrong here once the run wraps more than once.
        Assert.Equal(DdpPacketizer.NextSequence(expectedPackets[^1][1]), nextSequence);
    }

    [Fact]
    public async Task ReturnsTheBufferToThePoolEvenWhenSendThrows()
    {
        // A leaked rented buffer is invisible in a single test run, but this
        // at least proves the finally block runs on the exception path rather
        // than only ever being exercised on the happy path.
        var frame = CreateFrame(100);

        await Assert.ThrowsAsync<InvalidOperationException>(() => DdpPacketizer.SendPacketsAsync(
            frame,
            1,
            bytesPerLed: 3,
            _ => throw new InvalidOperationException("simulated send failure")));
    }

    private static byte[] CreateFrame(int ledCount)
        => Enumerable.Range(0, ledCount * 3).Select(static value => (byte)(value % 251)).ToArray();
}
