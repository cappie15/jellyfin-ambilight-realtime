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

    /// <remarks>
    /// <paramref name="whiteExtractionFactor"/> exists because a fixed-CCT
    /// white die cannot, by itself, be dialled warmer or cooler -- only a
    /// shifted mix of the colour LEDs can actually move the perceived
    /// temperature. At <c>1</c> (this class's long-standing default, and the
    /// White step centred), extraction is exactly <c>min(r,g,b)</c> as
    /// before, byte-identical to prior behaviour. As it is dialled toward
    /// <c>0</c> -- driven by how far the White step's colour-temperature
    /// slider currently sits from centre, not by any one pixel's own
    /// saturation, so ordinary saturated video content is never touched by
    /// this -- less of the shared grey is sent to the (unshiftable) white
    /// die and correspondingly more stays on the colour LEDs, which is what
    /// actually lets the perceived white reach the requested temperature
    /// instead of being pulled back toward the die's own fixed point.
    ///
    /// Reported live and confirmed mathematically: reducing extraction alone
    /// raises the *total* summed output across all four channels for the
    /// same input (full extraction is always the total-minimising choice),
    /// which is exactly "de witte led komt daar nog bij bovenop... het
    /// geheel is te fel". So after computing the tapered split, every
    /// channel is rescaled by a single compensation factor that pins the
    /// four channels' combined total back to what full extraction of the
    /// same pixel would already have produced -- shifting temperature this
    /// way changes which channels carry the light, not how much of it there
    /// is in total.
    /// </remarks>
    /// <remarks>
    /// <paramref name="whiteChannelCeiling"/> exists because "full
    /// extraction" itself was never actually total-preserving for a bright
    /// pixel -- it assumed one white die puts out as much light as red,
    /// green and blue lit together, which live flicker-photometry testing
    /// (alternating the same LEDs between a mixed-RGB white and the white
    /// channel alone, which cancels their considerable difference in colour
    /// temperature and isolates a genuine brightness gap) disproved: the gap
    /// held completely flat from white=100 through white=230 of 255, so no
    /// drive value closes it. Below the ceiling this changes nothing --
    /// those pixels were never bright enough for the die's own peak to be
    /// the limit. Above it, only the ceiling's worth is extracted and the
    /// rest of the shared grey is simply left on red, green and blue rather
    /// than being pulled onto a die that cannot reproduce it: unlike the
    /// colour-temperature compensation above, this deliberately does not
    /// rescale anything back up, since red, green and blue retaining more of
    /// their own original value is exactly the fix, not something to
    /// counteract.
    /// </remarks>
    public byte[] Encode(ReadOnlySpan<LinearRgb> linearFrame, Rgb24Encoding encoding, float whiteExtractionFactor = 1f, float whiteChannelCeiling = 1f)
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

        var extractionFactor = Math.Clamp(whiteExtractionFactor, 0f, 1f);
        var ceiling = Math.Clamp(whiteChannelCeiling, 0f, 1f);

        var rgbw32 = new byte[linearFrame.Length * 4];
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
            var errorOffset = index * 3;
            rgbw32[offset] = EncodeDitheredComponent((colour.Red - white) * compensation, encoding, errorOffset);
            rgbw32[offset + 1] = EncodeDitheredComponent((colour.Green - white) * compensation, encoding, errorOffset + 1);
            rgbw32[offset + 2] = EncodeDitheredComponent((colour.Blue - white) * compensation, encoding, errorOffset + 2);
            rgbw32[offset + 3] = EncodePlainComponent(white * compensation, encoding);
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
