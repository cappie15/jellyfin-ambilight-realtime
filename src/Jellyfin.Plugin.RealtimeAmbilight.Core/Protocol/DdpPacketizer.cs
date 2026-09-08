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
    public const byte Rgbw32 = 0x1B;
    public const byte DisplayDestination = 0x01;

    /// <summary>
    /// Packetizes a frame. <paramref name="firstSequence"/> is normalized to
    /// WLED's useful sequence range of 1 through 15 and advances per datagram.
    /// <paramref name="bytesPerLed"/> selects RGB24 (3, the default) or RGBW32
    /// (4) and is written into the DDP data-type byte so WLED decodes the
    /// fourth channel as white rather than as the next LED's red.
    /// </summary>
    public static IReadOnlyList<byte[]> Packetize(ReadOnlySpan<byte> rgbFrame, byte firstSequence = 1, int bytesPerLed = 3)
    {
        ValidateFrame(rgbFrame, bytesPerLed);

        var packetCount = (rgbFrame.Length + ChannelsPerPacket - 1) / ChannelsPerPacket;
        var packets = new byte[packetCount][];
        var offset = 0;
        var sequence = NormalizeSequence(firstSequence);
        var dataType = bytesPerLed == 4 ? Rgbw32 : Rgb24;

        for (var index = 0; index < packetCount; index++)
        {
            var payloadLength = Math.Min(ChannelsPerPacket, rgbFrame.Length - offset);
            var packet = new byte[HeaderLength + payloadLength];
            packet[0] = index == packetCount - 1 ? (byte)(Version1 | Push) : Version1;
            packet[1] = sequence;
            packet[2] = dataType;
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

    private static void ValidateFrame(ReadOnlySpan<byte> rgbFrame, int bytesPerLed)
    {
        if (bytesPerLed != 3 && bytesPerLed != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerLed), bytesPerLed, "DDP frames here are either RGB24 (3 bytes per LED) or RGBW32 (4).");
        }

        if (rgbFrame.IsEmpty)
        {
            throw new ArgumentException("A DDP frame must contain at least one LED.", nameof(rgbFrame));
        }

        if (rgbFrame.Length % bytesPerLed != 0)
        {
            throw new ArgumentException($"A frame with {bytesPerLed} bytes per LED must have a length that is a whole multiple of {bytesPerLed}.", nameof(rgbFrame));
        }
    }
}
