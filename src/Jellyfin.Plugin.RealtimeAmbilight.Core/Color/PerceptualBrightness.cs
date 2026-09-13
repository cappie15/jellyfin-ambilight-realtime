namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

/// <summary>
/// Converts a percentage an operator types in (what "4%" means on a
/// waveform monitor or any ordinary video tool) to the linear-light fraction
/// it actually corresponds to, for any control whose number is meant to read
/// perceptually even though the pipeline underneath operates in linear
/// light. Gamma compresses shadows, so a perceptual percentage and its
/// linear-light equivalent are not close near black -- shared by
/// <see cref="PerimeterColourAdjustment"/>'s black-level floor (WLED) and
/// <see cref="Hue.HueNaturalLightFilter"/>'s own black-level floor (Hue), so
/// "4%" means the same thing on both outputs.
/// </summary>
public static class PerceptualBrightness
{
    /// <summary>The exact inverse of <see cref="Output.Rgb24Encoder.ToTransferValue"/>'s BT.709 branch -- not part of the actual output encoding, which is chosen independently per output and may even be linear.</summary>
    public static float DecodePerceptualFraction(float perceptual)
    {
        var value = Math.Clamp(perceptual, 0f, 1f);
        return value < 0.081f
            ? value / 4.5f
            : MathF.Pow((value + 0.099f) / 1.099f, 1f / 0.45f);
    }
}
