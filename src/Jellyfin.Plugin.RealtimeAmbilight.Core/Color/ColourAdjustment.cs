namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

/// <summary>
/// Brightness and saturation applied to the linear-light frame, before it is
/// encoded for the wire.
/// </summary>
/// <remarks>
/// Both belong here rather than in the controller. WLED's realtime path bypasses
/// its own master brightness when "force max brightness" is enabled, which is its
/// default, so an installation can otherwise have no way to dim the Ambilight
/// without dimming everything else it does. Working in linear light means
/// halving the brightness halves the light, which is not true of the encoded
/// values.
/// </remarks>
public readonly record struct ColourAdjustment(
    float Brightness,
    float Saturation,
    float RedGain = 1f,
    float GreenGain = 1f,
    float BlueGain = 1f)
{
    /// <summary>Leaves the frame untouched.</summary>
    public static ColourAdjustment None => new(1f, 1f);

    public bool IsIdentity => Brightness == 1f && Saturation == 1f
        && RedGain == 1f && GreenGain == 1f && BlueGain == 1f;

    /// <summary>Creates an adjustment from whole percentages.</summary>
    public static ColourAdjustment FromPercentages(
        int brightnessPercent,
        int saturationPercent,
        int redGainPercent = 100,
        int greenGainPercent = 100,
        int blueGainPercent = 100)
        => new(
            Math.Clamp(brightnessPercent, 1, 100) / 100f,
            Math.Clamp(saturationPercent, 50, 200) / 100f,
            Math.Clamp(redGainPercent, 50, 150) / 100f,
            Math.Clamp(greenGainPercent, 50, 150) / 100f,
            Math.Clamp(blueGainPercent, 50, 150) / 100f);

    /// <summary>
    /// Applies saturation about the colour's own luminance, then the per-channel
    /// gains that set white balance, then brightness.
    /// </summary>
    public LinearRgb Apply(LinearRgb colour)
    {
        if (IsIdentity)
        {
            return colour;
        }

        var red = colour.Red;
        var green = colour.Green;
        var blue = colour.Blue;

        if (Saturation != 1f)
        {
            // Rec.709 luminance is the grey the colour would be. Pushing away
            // from it saturates; pulling towards it desaturates.
            var luminance = (0.2126f * red) + (0.7152f * green) + (0.0722f * blue);
            red = luminance + ((red - luminance) * Saturation);
            green = luminance + ((green - luminance) * Saturation);
            blue = luminance + ((blue - luminance) * Saturation);
        }

        return new LinearRgb(
            Math.Clamp(red * RedGain * Brightness, 0f, 1f),
            Math.Clamp(green * GreenGain * Brightness, 0f, 1f),
            Math.Clamp(blue * BlueGain * Brightness, 0f, 1f));
    }
}
