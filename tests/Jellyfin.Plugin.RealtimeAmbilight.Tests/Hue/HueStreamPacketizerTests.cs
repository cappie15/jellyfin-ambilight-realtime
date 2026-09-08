using System.Text;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Protocol;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Hue;

public class HueStreamPacketizerTests
{
    private static readonly Guid ConfigurationId = Guid.Parse("a1b2c3d4-e5f6-4789-abcd-ef0123456789");

    [Fact]
    public void PacketizeWritesTheFixedHeaderExactly()
    {
        var packets = HueStreamPacketizer.Packetize(
            ConfigurationId,
            [new HueStreamPacketizer.ChannelColour(0, new LinearRgb(1f, 0f, 0f))]);

        var packet = Assert.Single(packets);

        Assert.Equal("HueStream", Encoding.ASCII.GetString(packet, 0, 9));
        Assert.Equal(HueStreamPacketizer.VersionMajor, packet[9]);
        Assert.Equal(HueStreamPacketizer.VersionMinor, packet[10]);
        Assert.Equal((byte)1, packet[11]); // default sequence number
        Assert.Equal(0, packet[12]); // reserved
        Assert.Equal(0, packet[13]); // reserved
        Assert.Equal(HueStreamPacketizer.ColorSpaceRgb, packet[14]);
        Assert.Equal(0, packet[15]); // reserved
    }

    [Fact]
    public void PacketizeEncodesTheConfigurationIdAsLowercaseHyphenatedAscii()
    {
        var packets = HueStreamPacketizer.Packetize(
            ConfigurationId,
            [new HueStreamPacketizer.ChannelColour(0, new LinearRgb(0f, 0f, 0f))]);

        var idText = Encoding.ASCII.GetString(packets[0], 16, 36);

        Assert.Equal("a1b2c3d4-e5f6-4789-abcd-ef0123456789", idText);
        Assert.Equal(36, idText.Length);
    }

    [Fact]
    public void PacketizeWritesRealChannelIdsAndDuplicatedColourBytes()
    {
        var channels = new[]
        {
            new HueStreamPacketizer.ChannelColour(7, new LinearRgb(1f, 0.5f, 0f)),
            new HueStreamPacketizer.ChannelColour(2, new LinearRgb(0f, 0f, 1f)),
        };

        var packet = Assert.Single(HueStreamPacketizer.Packetize(ConfigurationId, channels));

        // Header (16) + config id (36) = 52 bytes before the first channel.
        const int channelsStart = 52;
        Assert.Equal((byte)7, packet[channelsStart]);
        Assert.Equal(255, packet[channelsStart + 1]); // R high byte
        Assert.Equal(255, packet[channelsStart + 2]); // R low byte
        // sRGB-encoded, not linear*255: 0.5 linear-light encodes to ~0.715,
        // i.e. ~182/255, well above the naive 128 a linear scaling would give.
        var srgbHalf = (1.055 * Math.Pow(0.5, 1d / 2.4)) - 0.055;
        var expectedGreen = (byte)Math.Round(srgbHalf * 255);
        Assert.Equal(expectedGreen, packet[channelsStart + 3]); // G high
        Assert.Equal(expectedGreen, packet[channelsStart + 4]); // G low
        Assert.Equal(0, packet[channelsStart + 5]); // B high
        Assert.Equal(0, packet[channelsStart + 6]); // B low

        const int secondChannelStart = channelsStart + 7;
        Assert.Equal((byte)2, packet[secondChannelStart]);
        Assert.Equal(0, packet[secondChannelStart + 1]);
        Assert.Equal(255, packet[secondChannelStart + 5]);
        Assert.Equal(255, packet[secondChannelStart + 6]);

        Assert.Equal(52 + (2 * 7), packet.Length);
    }

    [Fact]
    public void PacketizeChunksAtTwentyChannelsPerMessage()
    {
        var channels = Enumerable.Range(0, 25)
            .Select(id => new HueStreamPacketizer.ChannelColour((byte)id, new LinearRgb(0f, 0f, 0f)))
            .ToArray();

        var packets = HueStreamPacketizer.Packetize(ConfigurationId, channels);

        Assert.Equal(2, packets.Count);
        Assert.Equal(52 + (20 * 7), packets[0].Length);
        Assert.Equal(52 + (5 * 7), packets[1].Length);
        // Each chunk is a complete, independently valid message.
        Assert.Equal("HueStream", Encoding.ASCII.GetString(packets[1], 0, 9));
    }

    [Fact]
    public void PacketizeRejectsAnEmptyChannelList()
    {
        Assert.Throws<ArgumentException>(() => HueStreamPacketizer.Packetize(ConfigurationId, []));
    }

    [Fact]
    public void MidToneLinearLightEncodesBrighterThanNaiveLinearScaling()
    {
        // Regression test for a real bug: colour used to be sent as raw
        // linear*255, which HueApi.ColorConverters' own gamut math (its sRGB
        // decode, x/12.92 or ((x+0.055)/1.055)^2.4) proves is the wrong
        // convention -- this whole ecosystem expects gamma-encoded sRGB, so
        // every non-extreme colour arrived far darker than intended. A 0.5
        // linear-light component must now come out well above the naive
        // 128/255 a plain linear scaling would give.
        var packet = Assert.Single(HueStreamPacketizer.Packetize(
            ConfigurationId,
            [new HueStreamPacketizer.ChannelColour(0, new LinearRgb(0.5f, 0.5f, 0.5f))]));

        const int channelsStart = 52;
        Assert.True(packet[channelsStart + 1] > 128, $"expected sRGB-encoded byte above naive linear 128, was {packet[channelsStart + 1]}");
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 255)]
    public void PureBlackAndPureWhiteAreUnaffectedBySrgbEncoding(float linear, byte expectedByte)
    {
        var packet = Assert.Single(HueStreamPacketizer.Packetize(
            ConfigurationId,
            [new HueStreamPacketizer.ChannelColour(0, new LinearRgb(linear, linear, linear))]));

        const int channelsStart = 52;
        Assert.Equal(expectedByte, packet[channelsStart + 1]);
    }

    [Fact]
    public void EncodeConfigurationIdMatchesGuidsDefaultDFormat()
    {
        var idBytes = HueStreamPacketizer.EncodeConfigurationId(ConfigurationId);

        Assert.Equal(ConfigurationId.ToString("D"), Encoding.ASCII.GetString(idBytes));
        Assert.Equal(36, idBytes.Length);
    }
}
