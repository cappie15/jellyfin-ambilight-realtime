using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Output;

/// <summary>How the linear-light frame is mapped onto the wire bytes.</summary>
public enum Rgb24Encoding
{
    /// <summary>
    /// Byte value proportional to light output. Correct for a controller that
    /// drives its LEDs straight from the byte, which is what WLED does for
    /// realtime data unless gamma correction is explicitly enabled for it.
    /// </summary>
    Linear,

    /// <summary>
    /// BT.709 transfer encoding, the same encoding a display expects. Correct
    /// only for a controller that applies gamma correction of its own.
    /// </summary>
    Bt709,
}

/// <summary>
/// Encodes the linear-light physical frame for RGB LED transports. Clamping and
/// transfer encoding are owned here, after sampling and interpolation.
/// </summary>
/// <remarks>
/// Choosing the wrong encoding is not subtle. Sending BT.709 to a controller
/// that does no gamma correction drives a dark scene about thirteen times
/// brighter than intended and lifts the weaker channels of every colour, which
/// looks exactly like an Ambilight that ignores brightness and washes colours
/// out.
/// </remarks>
public static class Rgb24Encoder
{
    public static byte[] Encode(ReadOnlySpan<LinearRgb> linearFrame, Rgb24Encoding encoding = Rgb24Encoding.Bt709)
    {
        if (linearFrame.IsEmpty)
        {
            throw new ArgumentException("At least one physical LED colour is required.", nameof(linearFrame));
        }

        var rgb24 = new byte[checked(linearFrame.Length * 3)];
        for (var index = 0; index < linearFrame.Length; index++)
        {
            var offset = index * 3;
            rgb24[offset] = EncodeComponent(linearFrame[index].Red, encoding);
            rgb24[offset + 1] = EncodeComponent(linearFrame[index].Green, encoding);
            rgb24[offset + 2] = EncodeComponent(linearFrame[index].Blue, encoding);
        }

        return rgb24;
    }

    /// <summary>
    /// The transfer function alone, as a fraction of full scale. Shared with
    /// <see cref="DitheredRgb24Encoder"/>, which needs the pre-quantization
    /// value rather than the rounded byte.
    /// </summary>
    public static float ToTransferValue(float linearComponent, Rgb24Encoding encoding)
    {
        var linear = Math.Clamp(linearComponent, 0f, 1f);
        return encoding == Rgb24Encoding.Linear
            ? linear
            : linear < 0.018f
                ? linear * 4.5f
                : (1.099f * MathF.Pow(linear, 0.45f)) - 0.099f;
    }

    private static byte EncodeComponent(float linearComponent, Rgb24Encoding encoding)
    {
        var value = ToTransferValue(linearComponent, encoding);
        return checked((byte)Math.Clamp((int)MathF.Round(value * byte.MaxValue), byte.MinValue, byte.MaxValue));
    }
}
