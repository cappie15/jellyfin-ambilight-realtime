using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Output;

/// <summary>
/// Encodes the linear-light physical frame for RGB LED transports. Clamping and
/// BT.709 transfer encoding are owned here, after sampling and interpolation.
/// </summary>
public static class Rgb24Encoder
{
    public static byte[] Encode(ReadOnlySpan<LinearRgb> linearFrame)
    {
        if (linearFrame.IsEmpty)
        {
            throw new ArgumentException("At least one physical LED colour is required.", nameof(linearFrame));
        }

        var rgb24 = new byte[checked(linearFrame.Length * 3)];
        for (var index = 0; index < linearFrame.Length; index++)
        {
            var offset = index * 3;
            rgb24[offset] = EncodeComponent(linearFrame[index].Red);
            rgb24[offset + 1] = EncodeComponent(linearFrame[index].Green);
            rgb24[offset + 2] = EncodeComponent(linearFrame[index].Blue);
        }

        return rgb24;
    }

    private static byte EncodeComponent(float linearComponent)
    {
        var linear = Math.Clamp(linearComponent, 0f, 1f);
        var nonlinear = linear < 0.018f
            ? linear * 4.5f
            : (1.099f * MathF.Pow(linear, 0.45f)) - 0.099f;
        return checked((byte)Math.Clamp((int)MathF.Round(nonlinear * byte.MaxValue), byte.MinValue, byte.MaxValue));
    }
}
