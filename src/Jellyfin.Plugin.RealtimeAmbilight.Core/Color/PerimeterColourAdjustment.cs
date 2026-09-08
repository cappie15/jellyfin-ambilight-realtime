using Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

/// <summary>
/// Applies a shared colour treatment and a small final trim for every physical
/// side of the television. The latter accounts for strips with unequal bins,
/// corners and differently coloured sections of wall.
/// </summary>
public readonly record struct PerimeterColourAdjustment(
    ColourAdjustment Global,
    ColourAdjustment Top,
    ColourAdjustment Right,
    ColourAdjustment Bottom,
    ColourAdjustment Left,
    float BlackLevelFloor = 0f)
{
    public static PerimeterColourAdjustment None => new(
        ColourAdjustment.None,
        ColourAdjustment.None,
        ColourAdjustment.None,
        ColourAdjustment.None,
        ColourAdjustment.None,
        0f);

    public bool IsIdentity => Global.IsIdentity && Top.IsIdentity && Right.IsIdentity && Bottom.IsIdentity && Left.IsIdentity
        && BlackLevelFloor <= 0f;

    public LinearRgb Apply(LinearRgb colour, int physicalLedIndex, LedLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var gated = BlackLevelFloor > 0f ? ApplyBlackLevelFloor(colour, BlackLevelFloor) : colour;
        var shared = Global.Apply(gated);
        var side = physicalLedIndex < layout.TopLedCount
            ? Top
            : physicalLedIndex < layout.TopLedCount + layout.RightLedCount
                ? Right
                : physicalLedIndex < layout.TopLedCount + layout.RightLedCount + layout.BottomLedCount
                    ? Bottom
                    : Left;
        return side.Apply(shared);
    }

    /// <summary>
    /// Below the floor the LEDs go fully off; above it, the remaining headroom
    /// is rescaled back up to full range so there is no jump at the boundary.
    /// Scaling by luminance rather than gating each channel separately keeps
    /// the colour's hue and saturation intact as it fades to black.
    /// </summary>
    private static LinearRgb ApplyBlackLevelFloor(LinearRgb colour, float floor)
    {
        var luminance = (0.2126f * colour.Red) + (0.7152f * colour.Green) + (0.0722f * colour.Blue);
        if (luminance <= floor)
        {
            return default;
        }

        var scale = (luminance - floor) / (luminance * (1f - floor));
        return new LinearRgb(colour.Red * scale, colour.Green * scale, colour.Blue * scale);
    }
}
