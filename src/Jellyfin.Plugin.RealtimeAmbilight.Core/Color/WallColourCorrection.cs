namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

/// <summary>
/// Compensates, within the available LED headroom, for a wall that reflects one
/// primary more strongly than another. This is deliberately a gentle inverse
/// reflectance correction: no LED can create light a painted wall absorbs.
/// </summary>
public readonly record struct WallColourCorrection(float RedGain, float GreenGain, float BlueGain)
{
    public static WallColourCorrection None => new(1f, 1f, 1f);

    public static WallColourCorrection FromHtmlColour(string? htmlColour, int strengthPercent = 100)
    {
        if (string.IsNullOrWhiteSpace(htmlColour))
        {
            return None;
        }

        var value = htmlColour.Trim().TrimStart('#');
        if (value.Length != 6
            || !byte.TryParse(value.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var red)
            || !byte.TryParse(value.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var green)
            || !byte.TryParse(value.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var blue))
        {
            return None;
        }

        var reflectance = new LinearRgb(ToLinear(red), ToLinear(green), ToLinear(blue));
        // A colour picker can select black, but dividing by it would produce an
        // unhelpful infinity. A near-black painted wall cannot be corrected by
        // light anyway, so cap the correction at a useful twofold gain.
        var brightest = Math.Max(reflectance.Red, Math.Max(reflectance.Green, reflectance.Blue));
        if (brightest <= 0.0001f)
        {
            return None;
        }

        var amount = Math.Clamp(strengthPercent, 0, 100) / 100f;
        float Gain(float component)
            => Math.Clamp(1f + ((Math.Min(2f, brightest / Math.Max(component, 0.05f)) - 1f) * amount), 1f, 2f);

        return new WallColourCorrection(Gain(reflectance.Red), Gain(reflectance.Green), Gain(reflectance.Blue));
    }

    private static float ToLinear(byte component)
    {
        var encoded = component / 255f;
        // CSS/sRGB and BT.709 differ slightly near black. This transfer is used
        // only to interpret the manually chosen paint colour; the bounded gain
        // makes the distinction immaterial while sRGB is what the picker shows.
        return encoded <= 0.04045f
            ? encoded / 12.92f
            : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
    }
}
