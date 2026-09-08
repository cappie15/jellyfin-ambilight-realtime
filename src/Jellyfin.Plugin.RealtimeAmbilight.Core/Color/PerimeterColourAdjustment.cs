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
    ColourAdjustment Left)
{
    public static PerimeterColourAdjustment None => new(
        ColourAdjustment.None,
        ColourAdjustment.None,
        ColourAdjustment.None,
        ColourAdjustment.None,
        ColourAdjustment.None);

    public bool IsIdentity => Global.IsIdentity && Top.IsIdentity && Right.IsIdentity && Bottom.IsIdentity && Left.IsIdentity;

    public LinearRgb Apply(LinearRgb colour, int physicalLedIndex, LedLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var shared = Global.Apply(colour);
        var side = physicalLedIndex < layout.TopLedCount
            ? Top
            : physicalLedIndex < layout.TopLedCount + layout.RightLedCount
                ? Right
                : physicalLedIndex < layout.TopLedCount + layout.RightLedCount + layout.BottomLedCount
                    ? Bottom
                    : Left;
        return side.Apply(shared);
    }
}
