using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Output;

/// <summary>
/// Encodes RGB24 exactly as <see cref="Rgb24Encoder"/> does, but carries each
/// channel's rounding error forward into the next frame instead of discarding
/// it.
/// </summary>
/// <remarks>
/// WLED drives its LEDs straight from the byte in <see cref="Rgb24Encoding.Linear"/>,
/// and linear light gives the darkest tones the fewest of the 256 available
/// steps even though the eye resolves the most detail there. A plain per-frame
/// round therefore holds one byte value for many frames of a slow fade and
/// then jumps to the next -- the visible "steps" of a hobby LED strip rather
/// than a smooth fade. Diffusing the rounding error forward makes the strip
/// alternate between two adjacent bytes in the right proportion instead, which
/// averages to the true value within a handful of frames: fast enough, at any
/// output rate this plugin uses, to sit well above flicker fusion and read as
/// nothing but a smooth fade.
/// </remarks>
public sealed class DitheredRgb24Encoder : IDitheredChannelEncoder
{
    private float[] _carriedError = [];

    public byte[] Encode(ReadOnlySpan<LinearRgb> linearFrame, Rgb24Encoding encoding, float whiteExtractionFactor = 1f)
    {
        if (linearFrame.IsEmpty)
        {
            throw new ArgumentException("At least one physical LED colour is required.", nameof(linearFrame));
        }

        if (_carriedError.Length != linearFrame.Length * 3)
        {
            // A layout change starts dithering over with a clean slate rather
            // than reusing error values addressed to a different LED count.
            _carriedError = new float[linearFrame.Length * 3];
        }

        var rgb24 = new byte[linearFrame.Length * 3];
        for (var index = 0; index < linearFrame.Length; index++)
        {
            var offset = index * 3;
            rgb24[offset] = EncodeComponent(linearFrame[index].Red, encoding, offset);
            rgb24[offset + 1] = EncodeComponent(linearFrame[index].Green, encoding, offset + 1);
            rgb24[offset + 2] = EncodeComponent(linearFrame[index].Blue, encoding, offset + 2);
        }

        return rgb24;
    }

    private byte EncodeComponent(float linearComponent, Rgb24Encoding encoding, int errorIndex)
    {
        var target = (Rgb24Encoder.ToTransferValue(linearComponent, encoding) * byte.MaxValue) + _carriedError[errorIndex];
        var quantized = MathF.Round(Math.Clamp(target, 0f, byte.MaxValue));
        _carriedError[errorIndex] = target - quantized;
        return (byte)quantized;
    }
}
