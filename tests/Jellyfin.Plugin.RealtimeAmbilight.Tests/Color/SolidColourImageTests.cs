using System.Buffers.Binary;
using System.IO.Compression;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Color;

public sealed class SolidColourImageTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void StartsWithTheStandardPngSignature()
    {
        var png = SolidColourImage.CreateFlatPng(255, 0, 0, 4, 4);

        Assert.Equal(PngSignature, png[..8]);
    }

    [Fact]
    public void IhdrChunkReportsTheRequestedDimensionsAndTrueColourWithNoAlpha()
    {
        var png = SolidColourImage.CreateFlatPng(0, 255, 128, 32, 18);

        // IHDR is always the very next chunk after the signature: length(4) + "IHDR"(4) + 13 bytes of data.
        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        var bitDepth = png[24];
        var colourType = png[25];

        Assert.Equal(32, width);
        Assert.Equal(18, height);
        Assert.Equal(8, bitDepth);
        Assert.Equal(2, colourType); // truecolor RGB, no alpha channel
    }

    [Theory]
    [InlineData((byte)255, (byte)0, (byte)0, 1, 1)]
    [InlineData((byte)10, (byte)200, (byte)90, 3, 2)]
    [InlineData((byte)0, (byte)0, (byte)255, 8, 5)]
    public void EveryPixelDecodesBackToTheExactRequestedColour(byte red, byte green, byte blue, int width, int height)
    {
        var png = SolidColourImage.CreateFlatPng(red, green, blue, width, height);
        var raw = DecompressIdat(png);

        var stride = 1 + (width * 3);
        Assert.Equal(stride * height, raw.Length);
        for (var row = 0; row < height; row++)
        {
            var rowStart = row * stride;
            Assert.Equal(0, raw[rowStart]); // filter type "None"
            for (var column = 0; column < width; column++)
            {
                var pixelStart = rowStart + 1 + (column * 3);
                Assert.Equal(red, raw[pixelStart]);
                Assert.Equal(green, raw[pixelStart + 1]);
                Assert.Equal(blue, raw[pixelStart + 2]);
            }
        }
    }

    private static byte[] DecompressIdat(byte[] png)
    {
        var offset = 8; // past the signature
        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IDAT")
            {
                using var compressed = new MemoryStream(png, offset + 8, length);
                using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
                using var decompressed = new MemoryStream();
                zlib.CopyTo(decompressed);
                return decompressed.ToArray();
            }

            offset += 4 + 4 + length + 4; // length + type + data + crc
        }

        throw new InvalidOperationException("No IDAT chunk found.");
    }
}
