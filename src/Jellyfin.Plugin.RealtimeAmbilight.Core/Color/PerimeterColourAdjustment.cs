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
    float BlackLevelFloor = 0f,
    HueCorrectionCurve? Curve = null,
    float WhiteChannelCeiling = 1f)
{
    public static PerimeterColourAdjustment None => new(
        ColourAdjustment.None,
        ColourAdjustment.None,
        ColourAdjustment.None,
        ColourAdjustment.None,
        ColourAdjustment.None,
        0f,
        null,
        1f);

    public bool IsIdentity => Global.IsIdentity && Top.IsIdentity && Right.IsIdentity && Bottom.IsIdentity && Left.IsIdentity
        && BlackLevelFloor <= 0f && (Curve is null || Curve.IsIdentity) && WhiteChannelCeiling >= 1f;

    /// <summary>
    /// How much of an RGBW strip's shared grey should still go to the
    /// physical white LED, 0-1, for <see cref="Output.DitheredRgbw32Encoder"/>.
    /// Derived from how far the White step's own colour-temperature control
    /// (<see cref="ColourAdjustment.RedGain"/>/<see cref="ColourAdjustment.BlueGain"/>
    /// on <see cref="Global"/>, clamped 40-160% i.e. +-0.6 around 1) currently
    /// sits from centre -- <c>1</c> (full extraction, today's exact
    /// behaviour) when centred, tapering toward <c>0</c> at either extreme.
    /// This is a single frame-wide value, not evaluated per pixel: it tracks
    /// the operator's own calibrated setting, never any one pixel's own
    /// saturation, so it never affects ordinary saturated video content on
    /// its own.
    /// </summary>
    public float WhiteExtractionFactor
    {
        get
        {
            var shift = Math.Max(MathF.Abs(Global.RedGain - 1f), MathF.Abs(Global.BlueGain - 1f));
            var fraction = Math.Clamp(shift / 0.6f, 0f, 1f);
            return 1f - SmoothStep(fraction);
        }
    }

    private static float SmoothStep(float t) => t * t * (3f - (2f * t));

    /// <remarks>
    /// The hue-correction curve (from the colour-tuning wizard's Red/Green/
    /// Blue/Yellow/Cyan/Magenta steps) runs between the black-level floor and
    /// the shared per-installation adjustment (brightness/saturation/white
    /// balance/wall-colour correction), which stays exactly where it always
    /// was: those remain general multipliers applied around the curve, not
    /// replaced by it.
    /// </remarks>
    public LinearRgb Apply(LinearRgb colour, int physicalLedIndex, LedLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var gated = BlackLevelFloor > 0f ? ApplyBlackLevelFloor(colour, BlackLevelFloor) : colour;
        var curved = Curve is { IsIdentity: false } curve ? curve.Apply(gated) : gated;
        var shared = Global.Apply(curved);
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
