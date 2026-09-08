using System.Buffers.Binary;
using System.IO.Compression;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

/// <summary>
/// Builds a minimal, flat single-colour PNG -- the calibration wizard's
/// Red/Green/Blue/Yellow/Cyan/Magenta steps no longer show one of the
/// operator's own photos, but a rendered swatch at that colour's canonical
/// hue, sampled through the exact same edge-sampling pipeline a photo (or
/// real video) already goes through. No imaging library is referenced for
/// this: a solid-colour PNG is a handful of fixed chunks, and .NET's own
/// <see cref="ZLibStream"/> is all the compression it needs.
/// </summary>
public static class SolidColourImage
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] CreateFlatPng(byte red, byte green, byte blue, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type: truecolor RGB
        header[10] = 0; // compression method
        header[11] = 0; // filter method
        header[12] = 0; // interlace method
        WriteChunk(output, "IHDR"u8, header);

        WriteChunk(output, "IDAT"u8, Compress(BuildScanlines(red, green, blue, width, height)));
        WriteChunk(output, "IEND"u8, []);

        return output.ToArray();
    }

    private static byte[] BuildScanlines(byte red, byte green, byte blue, int width, int height)
    {
        var stride = 1 + (width * 3); // filter-type byte + RGB per pixel
        var raw = new byte[stride * height];
        for (var row = 0; row < height; row++)
        {
            var rowStart = row * stride;
            raw[rowStart] = 0; // filter type "None"
            for (var column = 0; column < width; column++)
            {
                var pixelStart = rowStart + 1 + (column * 3);
                raw[pixelStart] = red;
                raw[pixelStart + 1] = green;
                raw[pixelStart + 2] = blue;
            }
        }

        return raw;
    }

    private static byte[] Compress(byte[] raw)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, data.Length);
        output.Write(lengthBytes);

        var typeAndData = new byte[type.Length + data.Length];
        type.CopyTo(typeAndData);
        data.CopyTo(typeAndData.AsSpan(type.Length));
        output.Write(typeAndData);

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, Crc32.Compute(typeAndData));
        output.Write(crcBytes);
    }

    /// <summary>The CRC32 (IEEE 802.3, polynomial 0xEDB88320) PNG chunk checksums use.</summary>
    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }

                table[i] = c;
            }

            return table;
        }
    }
}
