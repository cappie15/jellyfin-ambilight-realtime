using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Output;

/// <summary>
/// Encodes RGBW32 exactly as <see cref="DitheredRgbw32Encoder"/> does --
/// same white extraction and total-preserving compensation maths -- but
/// every channel is plainly rounded, carrying no quantization error forward.
/// The stateless sibling of <see cref="Rgb24Encoder"/> for a strip with a
/// physical white LED.
/// </summary>
/// <remarks>
/// For a genuinely static target (the calibration wizard's own held swatch,
/// not moving video), <see cref="DitheredRgbw32Encoder"/>'s own doc comment
/// already explains why carrying error forward is the wrong choice: a held
/// fractional target is exactly what error diffusion must alternate between
/// two adjacent byte values to represent on average, which is invisible
/// blended across many frames of changing video content but reads as a
/// distinct, regular flicker on something the operator is deliberately
/// holding still to judge. Reported live: nudging a wizard slider by one
/// step, then again, made the preview appear to move and then "fly back" --
/// exactly the ordinary dithering ripple on a target that was not actually
/// still changing between polls, made visible because nothing about the
/// calibration preview needs -- or benefits from -- temporal averaging.
/// </remarks>
public static class Rgbw32Encoder
{
    public static byte[] Encode(ReadOnlySpan<LinearRgb> linearFrame, Rgb24Encoding encoding, float whiteExtractionFactor = 1f, float whiteChannelCeiling = 1f)
    {
        if (linearFrame.IsEmpty)
        {
            throw new ArgumentException("At least one physical LED colour is required.", nameof(linearFrame));
        }

        var extractionFactor = Math.Clamp(whiteExtractionFactor, 0f, 1f);
        var ceiling = Math.Clamp(whiteChannelCeiling, 0f, 1f);

        var rgbw32 = new byte[checked(linearFrame.Length * 4)];
        for (var index = 0; index < linearFrame.Length; index++)
        {
            var colour = linearFrame[index];
            var minComponent = MathF.Min(colour.Red, MathF.Min(colour.Green, colour.Blue));
            var desiredWhite = extractionFactor * minComponent;
            var white = MathF.Min(desiredWhite, ceiling);

            var compensation = 1f;
            if (extractionFactor < 1f)
            {
                var sum = colour.Red + colour.Green + colour.Blue;
                var fullExtractionWhite = MathF.Min(minComponent, ceiling);
                var totalAtFullExtraction = sum - (2f * fullExtractionWhite);
                var totalAtCurrentExtraction = sum - (2f * white);
                if (totalAtCurrentExtraction > 1e-6f)
                {
                    compensation = totalAtFullExtraction / totalAtCurrentExtraction;
                }
            }

            var offset = index * 4;
            rgbw32[offset] = EncodeComponent((colour.Red - white) * compensation, encoding);
            rgbw32[offset + 1] = EncodeComponent((colour.Green - white) * compensation, encoding);
            rgbw32[offset + 2] = EncodeComponent((colour.Blue - white) * compensation, encoding);
            rgbw32[offset + 3] = EncodeComponent(white * compensation, encoding);
        }

        return rgbw32;
    }

    private static byte EncodeComponent(float linearComponent, Rgb24Encoding encoding)
    {
        var value = Rgb24Encoder.ToTransferValue(linearComponent, encoding) * byte.MaxValue;
        return (byte)MathF.Round(Math.Clamp(value, 0f, byte.MaxValue));
    }
}
