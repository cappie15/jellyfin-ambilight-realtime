using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Output;

/// <summary>
/// Encodes RGBW32 for a strip with a physical white LED: the colour residual
/// (red, green, blue after white is extracted) diffuses its rounding error
/// forward exactly as <see cref="DitheredRgb24Encoder"/> does for RGB24, but
/// white itself is plainly rounded, carrying nothing forward.
/// </summary>
/// <remarks>
/// The white LED on an RGBW strip is a dedicated emitter, not a fourth colour
/// to mix from red, green and blue: reproducing grey by driving all three
/// colour LEDs together always costs more current, and looks a shade less
/// neutral, than the strip's own white die. Extracting
/// <c>w = min(r, g, b)</c> and subtracting it from each colour channel sends
/// exactly the grey the RGB LEDs would otherwise have reproduced redundantly
/// onto the one channel built for it, while a saturated pixel -- where the
/// minimum is small or zero -- is left almost untouched. This is applied last,
/// after every other adjustment (brightness, saturation, gain, the White
/// step's colour-temperature push-pull), so those already-tuned RGB values are
/// simply what gets split between the colour LEDs and the white one.
///
/// White is deliberately not temporally dithered like the colour channels
/// are. A held or near-still fractional target is exactly what error
/// diffusion must alternate between two adjacent byte values to represent on
/// average, and once a pixel's whole grey component sits on one physically
/// brighter channel instead of being spread across three independently
/// (and out-of-phase) dithered colour channels whose combined ripple
/// partially cancels, that single channel's own alternation reads as a
/// distinct, regular brightness flicker rather than the same-size ripple on
/// a colour channel -- confirmed against the physical strip as a visible
/// blink on white with colours unaffected. A short low-pass filter on the
/// pre-dither white value was tried first and measured, with the exact
/// algorithm below, to make no improvement (occasionally a slightly *more*
/// regular flip pattern): smoothing the input does not change that a
/// held near-constant fractional target still needs the ditherer's own
/// alternation to represent it. Plain rounding removes that alternation
/// entirely for a genuinely static target, and measured against randomised
/// frame-to-frame sampling noise (uniform jitter around a fixed mean, matching
/// how a real sampled edge behaves even in a mostly static scene) it cut the
/// byte-to-byte flip rate roughly in half compared with dithering white the
/// same way as the colour channels. The trade-off is real and accepted
/// deliberately: a slow fade through a white-heavy tone can show the
/// original 8-bit "stepping" near black that <see cref="DitheredRgb24Encoder"/>
/// was built to fix, now specifically on the white channel. A held or
/// blinking light is judged worse than a barely visible step, but this is a
/// real trade-off, not a free fix, and is worth re-checking once the strip is
/// actually watched.
/// </remarks>
public sealed class DitheredRgbw32Encoder : IDitheredChannelEncoder
{
    private float[] _carriedError = [];

    public byte[] Encode(ReadOnlySpan<LinearRgb> linearFrame, Rgb24Encoding encoding)
    {
        if (linearFrame.IsEmpty)
        {
            throw new ArgumentException("At least one physical LED colour is required.", nameof(linearFrame));
        }

        if (_carriedError.Length != linearFrame.Length * 3)
        {
            // A layout change starts dithering over with a clean slate rather
            // than reusing error values addressed to a different LED count.
            // Only the three colour channels carry error; white has none.
            _carriedError = new float[linearFrame.Length * 3];
        }

        var rgbw32 = new byte[linearFrame.Length * 4];
        for (var index = 0; index < linearFrame.Length; index++)
        {
            var colour = linearFrame[index];
            var white = MathF.Min(colour.Red, MathF.Min(colour.Green, colour.Blue));
            var offset = index * 4;
            var errorOffset = index * 3;
            rgbw32[offset] = EncodeDitheredComponent(colour.Red - white, encoding, errorOffset);
            rgbw32[offset + 1] = EncodeDitheredComponent(colour.Green - white, encoding, errorOffset + 1);
            rgbw32[offset + 2] = EncodeDitheredComponent(colour.Blue - white, encoding, errorOffset + 2);
            rgbw32[offset + 3] = EncodePlainComponent(white, encoding);
        }

        return rgbw32;
    }

    private byte EncodeDitheredComponent(float linearComponent, Rgb24Encoding encoding, int errorIndex)
    {
        var target = (Rgb24Encoder.ToTransferValue(linearComponent, encoding) * byte.MaxValue) + _carriedError[errorIndex];
        var quantized = MathF.Round(Math.Clamp(target, 0f, byte.MaxValue));
        _carriedError[errorIndex] = target - quantized;
        return (byte)quantized;
    }

    private static byte EncodePlainComponent(float linearComponent, Rgb24Encoding encoding)
    {
        var value = Rgb24Encoder.ToTransferValue(linearComponent, encoding) * byte.MaxValue;
        return (byte)MathF.Round(Math.Clamp(value, 0f, byte.MaxValue));
    }
}
