using System.Buffers.Binary;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Protocol;

/// <summary>
/// Builds WLED-compatible DDP RGB24 datagrams. One call represents one complete
/// frame and marks exactly its final packet with PUSH.
/// </summary>
public static class DdpPacketizer
{
    public const int HeaderLength = 10;
    public const int ChannelsPerPacket = 1440;
    public const byte Version1 = 0x40;
    public const byte Push = 0x01;
    public const byte Rgb24 = 0x0B;
    public const byte DisplayDestination = 0x01;

    /// <summary>
    /// Packetizes an RGB24 frame. <paramref name="firstSequence"/> is normalized
    /// to WLED's useful sequence range of 1 through 15 and advances per datagram.
    /// </summary>
    public static IReadOnlyList<byte[]> Packetize(ReadOnlySpan<byte> rgbFrame, byte firstSequence = 1)
    {
        ValidateRgb24Frame(rgbFrame);

        var packetCount = (rgbFrame.Length + ChannelsPerPacket - 1) / ChannelsPerPacket;
        var packets = new byte[packetCount][];
        var offset = 0;
        var sequence = NormalizeSequence(firstSequence);

        for (var index = 0; index < packetCount; index++)
        {
            var payloadLength = Math.Min(ChannelsPerPacket, rgbFrame.Length - offset);
            var packet = new byte[HeaderLength + payloadLength];
            packet[0] = index == packetCount - 1 ? (byte)(Version1 | Push) : Version1;
            packet[1] = sequence;
            packet[2] = Rgb24;
            packet[3] = DisplayDestination;
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), checked((uint)offset));
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8, 2), checked((ushort)payloadLength));
            rgbFrame.Slice(offset, payloadLength).CopyTo(packet.AsSpan(HeaderLength));

            packets[index] = packet;
            offset += payloadLength;
            sequence = NextSequence(sequence);
        }

        return packets;
    }

    public static byte NextSequence(byte currentSequence)
        => currentSequence is >= 1 and < 15 ? (byte)(currentSequence + 1) : (byte)1;

    private static byte NormalizeSequence(byte sequence)
        => sequence is >= 1 and <= 15 ? sequence : (byte)1;

    private static void ValidateRgb24Frame(ReadOnlySpan<byte> rgbFrame)
    {
        if (rgbFrame.IsEmpty)
        {
            throw new ArgumentException("A DDP frame must contain at least one RGB LED.", nameof(rgbFrame));
        }

        if (rgbFrame.Length % 3 != 0)
        {
            throw new ArgumentException("An RGB24 frame must contain exactly three bytes per LED.", nameof(rgbFrame));
        }
    }
}
