namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Protocol;

/// <summary>
/// Validates the one-datagram Hyperion Raw RGB transport used by WLED on port
/// 19446. The transport has no framing or offset, so it cannot represent a
/// larger strip safely.
/// </summary>
public static class HyperionRawRgbPacketizer
{
    public const int BytesPerLed = 3;
    public const int MaximumDatagramPayload = 1470;
    public const int MaximumLedCount = MaximumDatagramPayload / BytesPerLed;

    public static byte[] Packetize(ReadOnlySpan<byte> rgbFrame)
    {
        if (rgbFrame.IsEmpty)
        {
            throw new ArgumentException("A Raw RGB frame must contain at least one RGB LED.", nameof(rgbFrame));
        }

        if (rgbFrame.Length % BytesPerLed != 0)
        {
            throw new ArgumentException("A Raw RGB frame must contain exactly three bytes per LED.", nameof(rgbFrame));
        }

        if (rgbFrame.Length > MaximumDatagramPayload)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rgbFrame),
                rgbFrame.Length,
                $"Hyperion Raw RGB can carry at most {MaximumLedCount} LEDs ({MaximumDatagramPayload} bytes) in one safe datagram. Use DDP for larger layouts.");
        }

        return rgbFrame.ToArray();
    }
}
