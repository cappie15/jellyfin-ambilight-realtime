using System.Text;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Protocol;

/// <summary>
/// Builds HueStream v2.0 datagrams for the Entertainment DTLS channel.
/// </summary>
/// <remarks>
/// Written from the wire format independently confirmed in
/// <c>HueApi.Entertainment</c>'s own packet builder
/// (<c>Models/StreamingGroup.GetCurrentStateAsByteArray</c> and
/// <c>Models/StreamingState.ToByteArray</c>, <c>michielpost/Q42.HueApi</c>
/// tag <c>v3.3.0</c>) rather than against a written specification -- the
/// primary Hue reference for this is behind a developer-portal login this
/// environment cannot reach, so this is the same document ADR-0NN records as
/// the actual evidence, not a paraphrase of it. See the ADR for the exact
/// byte layout table and the independently-derived test vectors this is
/// checked against, per this project's standing practice of not trusting a
/// wire format without testing it directly (see ADR-004 for why).
/// </remarks>
public static class HueStreamPacketizer
{
    /// <summary>The fixed 9-byte protocol name every HueStream message starts with.</summary>
    public static readonly IReadOnlyList<byte> ProtocolName = Encoding.ASCII.GetBytes("HueStream");

    public const byte VersionMajor = 0x02;
    public const byte VersionMinor = 0x00;
    public const byte ColorSpaceRgb = 0x00;

    /// <summary>
    /// HueStream messages carry at most this many channel updates each; a
    /// configuration with more channels is sent as consecutive datagrams,
    /// each independently a complete, valid message.
    /// </summary>
    public const int MaximumChannelsPerMessage = 20;

    private const int HeaderLength = 16; // 9 (protocol name) + 2 (version) + 1 (sequence) + 2 (reserved) + 1 (colour space) + 1 (reserved)
    private const int EntertainmentConfigurationIdLength = 36; // lowercase GUID, hyphenated
    private const int BytesPerChannel = 7; // 1 id + 2 (R) + 2 (G) + 2 (B)

    /// <summary>One channel's colour for one HueStream message.</summary>
    public readonly record struct ChannelColour(byte ChannelId, LinearRgb Colour);

    /// <summary>
    /// Builds one or more complete HueStream datagrams for one frame.
    /// <paramref name="sequenceNumber"/> is written as-is into every chunk of
    /// this call; HueApi.Entertainment always sends 1 and the bridge does not
    /// appear to require it to advance, so this plugin does not either --
    /// kept as a parameter rather than hardcoded so that can change without
    /// touching every call site if it turns out to matter.
    /// </summary>
    public static IReadOnlyList<byte[]> Packetize(
        Guid entertainmentConfigurationId,
        IReadOnlyList<ChannelColour> channels,
        byte sequenceNumber = 1)
    {
        if (channels is null || channels.Count == 0)
        {
            throw new ArgumentException("A HueStream message must contain at least one channel.", nameof(channels));
        }

        var idBytes = EncodeConfigurationId(entertainmentConfigurationId);
        var chunkCount = (channels.Count + MaximumChannelsPerMessage - 1) / MaximumChannelsPerMessage;
        var packets = new byte[chunkCount][];

        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var offset = chunkIndex * MaximumChannelsPerMessage;
            var count = Math.Min(MaximumChannelsPerMessage, channels.Count - offset);
            var packet = new byte[HeaderLength + EntertainmentConfigurationIdLength + (count * BytesPerChannel)];

            var cursor = 0;
            foreach (var protocolByte in ProtocolName)
            {
                packet[cursor++] = protocolByte;
            }

            packet[cursor++] = VersionMajor;
            packet[cursor++] = VersionMinor;
            packet[cursor++] = sequenceNumber;
            packet[cursor++] = 0x00; // reserved
            packet[cursor++] = 0x00; // reserved
            packet[cursor++] = ColorSpaceRgb;
            packet[cursor++] = 0x00; // reserved

            idBytes.CopyTo(packet.AsSpan(cursor, EntertainmentConfigurationIdLength));
            cursor += EntertainmentConfigurationIdLength;

            for (var index = 0; index < count; index++)
            {
                var channel = channels[offset + index];
                packet[cursor++] = channel.ChannelId;
                WriteDuplicatedByte(packet, ref cursor, channel.Colour.Red);
                WriteDuplicatedByte(packet, ref cursor, channel.Colour.Green);
                WriteDuplicatedByte(packet, ref cursor, channel.Colour.Blue);
            }

            packets[chunkIndex] = packet;
        }

        return packets;
    }

    /// <summary>
    /// The 36-byte lowercase, hyphenated ASCII encoding HueApi.Entertainment
    /// sends (<c>entertainmentAreaId.ToString().ToLowerInvariant()</c>), i.e.
    /// .NET's default <see cref="Guid.ToString()"/> format ("D").
    /// </summary>
    public static byte[] EncodeConfigurationId(Guid entertainmentConfigurationId)
        => Encoding.ASCII.GetBytes(entertainmentConfigurationId.ToString("D"));

    /// <summary>
    /// A linear-light component in [0, 1], sRGB-encoded and written as an
    /// 8-bit value duplicated into both bytes of a 16-bit big-endian field --
    /// the same byte-duplication <c>StreamingState.ToByteArray</c> uses, not
    /// true 16-bit precision.
    /// </summary>
    /// <remarks>
    /// Colour arrives from <see cref="HueNaturalLightFilter"/> already fully
    /// processed but still linear-light -- this project's shared "physical
    /// LED colour" representation, the same one <c>Rgb24Encoder</c> uses for
    /// WLED. Sending it straight onto the wire was wrong: this plugin's own
    /// <c>HueApi.ColorConverters</c> dependency's own gamut math
    /// (<c>HueColorConverter.XyFromColor</c>, the standard sRGB decode --
    /// <c>x &gt; 0.04045 ? ((x+0.055)/1.055)^2.4 : x/12.92</c>) proves the
    /// RGB convention this whole ecosystem uses is gamma-encoded sRGB, not
    /// linear light, and the official Entertainment reference is unreachable
    /// from this session to confirm any other way. Sending unconverted
    /// linear values makes every non-extreme colour come out darker than
    /// intended -- for a mid-grey pixel, roughly 55/255 sent instead of the
    /// correct ~128/255, a real and large difference, not a rounding error --
    /// which is exactly the "Hue does not shine as bright" symptom reported
    /// live against the real bridge, brightness set to its own maximum, with
    /// direct app control visibly brighter than this plugin's own streamed
    /// output. Applying the standard sRGB OETF here, the mathematical
    /// inverse of that same decode, is what actually fixes it.
    /// </remarks>
    private static void WriteDuplicatedByte(byte[] packet, ref int cursor, float linearComponent)
    {
        var encoded = ToSrgb(linearComponent);
        var value = (byte)Math.Clamp(MathF.Round(encoded * byte.MaxValue), 0f, byte.MaxValue);
        packet[cursor++] = value;
        packet[cursor++] = value;
    }

    /// <summary>Standard sRGB OETF (linear to gamma-encoded), the exact inverse of the sRGB EOTF Hue's own gamut converter decodes with.</summary>
    private static float ToSrgb(float linear)
    {
        var clamped = Math.Clamp(linear, 0f, 1f);
        return clamped <= 0.0031308f
            ? clamped * 12.92f
            : (1.055f * MathF.Pow(clamped, 1f / 2.4f)) - 0.055f;
    }
}
