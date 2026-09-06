namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

/// <summary>
/// An RGB triplet whose components are linear-light values. Values are not
/// clamped here because later colour-processing stages own that policy.
/// </summary>
public readonly record struct LinearRgb(float Red, float Green, float Blue)
{
    public static LinearRgb Lerp(LinearRgb from, LinearRgb to, float amount)
        => new(
            from.Red + ((to.Red - from.Red) * amount),
            from.Green + ((to.Green - from.Green) * amount),
            from.Blue + ((to.Blue - from.Blue) * amount));
}
