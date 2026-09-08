namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

/// <summary>
/// One primary/secondary calibration point: a hue shift away from the
/// colour's own canonical position on the wheel, plus its own brightness and
/// saturation multipliers.
/// </summary>
public readonly record struct HueAnchor(float HueShiftDegrees, float BrightnessMultiplier, float SaturationMultiplier)
{
    public static HueAnchor Identity => new(0f, 1f, 1f);

    public bool IsIdentity => HueShiftDegrees == 0f && BrightnessMultiplier == 1f && SaturationMultiplier == 1f;
}

/// <summary>
/// A smooth, periodic colour correction over the full hue wheel, defined by
/// six anchors at the primary/secondary hues -- red 0&#176;, yellow 60&#176;,
/// green 120&#176;, cyan 180&#176;, blue 240&#176;, magenta 300&#176; -- each carrying its own
/// hue shift, brightness multiplier and saturation multiplier.
/// </summary>
/// <remarks>
/// Replaces the flat Red/Green/Blue gain sliders as the wizard's own
/// tuning-step mechanism: three flat gains can only ever describe a shift
/// along their own axis, never "make red a touch more orange without moving
/// green or blue at all", which is what a per-colour hue-shift slider asks
/// for -- and what let an operator's calibration actually mean "the red
/// this strip shows is a little pink; nudge it toward orange" instead of a
/// number with no intuitive direction.
/// <para>
/// Interpolated with a uniform Catmull-Rom spline over the six anchors,
/// treated as a closed loop (each anchor's neighbours wrap around the
/// circle) -- evaluated independently for hue shift, brightness and
/// saturation, each its own periodic curve sharing the same six sample
/// angles. Catmull-Rom rather than piecewise-linear specifically because a
/// linear blend has a visible kink in slope at every anchor; the request
/// was explicitly for a "curve", and Catmull-Rom's C1-continuous blend
/// removes exactly that seam, at negligible extra cost for six points
/// evaluated once per physical LED per frame.
/// </para>
/// <para>
/// A true grey (near-zero saturation) has no dominant hue to correct by;
/// rather than arbitrarily favour one anchor (say, always red), its
/// brightness/saturation multipliers are the plain average of all six
/// anchors, and its hue shift is left at zero -- a fade through grey should
/// not visibly favour whichever anchor a naive "default to hue 0" pick
/// would land on.
/// </para>
/// </remarks>
public sealed class HueCorrectionCurve
{
    /// <summary>Canonical hue angle, in degrees, of each anchor in <see cref="_anchors"/>'s order.</summary>
    private static readonly float[] AnchorHueDegrees = [0f, 60f, 120f, 180f, 240f, 300f];

    private readonly HueAnchor[] _anchors;

    public HueCorrectionCurve(HueAnchor red, HueAnchor yellow, HueAnchor green, HueAnchor cyan, HueAnchor blue, HueAnchor magenta)
    {
        _anchors = [red, yellow, green, cyan, blue, magenta];
    }

    public static HueCorrectionCurve Identity { get; } = new(
        HueAnchor.Identity, HueAnchor.Identity, HueAnchor.Identity,
        HueAnchor.Identity, HueAnchor.Identity, HueAnchor.Identity);

    public bool IsIdentity => Array.TrueForAll(_anchors, static a => a.IsIdentity);

    public LinearRgb Apply(LinearRgb colour)
    {
        if (IsIdentity)
        {
            return colour;
        }

        var (hue, saturation, value) = ToHsv(colour);

        float hueShift;
        float brightnessMultiplier;
        float saturationMultiplier;
        if (saturation <= 1e-6f)
        {
            hueShift = 0f;
            brightnessMultiplier = Average(static a => a.BrightnessMultiplier);
            saturationMultiplier = Average(static a => a.SaturationMultiplier);
        }
        else
        {
            hueShift = SampleAt(hue, static a => a.HueShiftDegrees);
            brightnessMultiplier = SampleAt(hue, static a => a.BrightnessMultiplier);
            saturationMultiplier = SampleAt(hue, static a => a.SaturationMultiplier);
        }

        var newHue = Wrap360(hue + hueShift);
        var newSaturation = Math.Clamp(saturation * saturationMultiplier, 0f, 1f);
        var newValue = Math.Clamp(value * brightnessMultiplier, 0f, 1f);
        return FromHsv(newHue, newSaturation, newValue);
    }

    private float Average(Func<HueAnchor, float> select)
    {
        var total = 0f;
        foreach (var anchor in _anchors)
        {
            total += select(anchor);
        }

        return total / _anchors.Length;
    }

    /// <summary>
    /// Uniform Catmull-Rom over the six anchors on a closed 360&#176; loop, sampled
    /// as a scalar-valued periodic function of <paramref name="hueDegrees"/>.
    /// </summary>
    private float SampleAt(float hueDegrees, Func<HueAnchor, float> select)
    {
        const int count = 6;
        const float segmentDegrees = 60f;
        var segment = (int)MathF.Floor(hueDegrees / segmentDegrees) % count;
        if (segment < 0)
        {
            segment += count;
        }

        var t = (hueDegrees - (segment * segmentDegrees)) / segmentDegrees;

        var p0 = select(_anchors[Mod(segment - 1, count)]);
        var p1 = select(_anchors[segment]);
        var p2 = select(_anchors[Mod(segment + 1, count)]);
        var p3 = select(_anchors[Mod(segment + 2, count)]);

        return CatmullRom(p0, p1, p2, p3, t);
    }

    private static float CatmullRom(float p0, float p1, float p2, float p3, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return 0.5f * (
            (2f * p1)
            + ((-p0 + p2) * t)
            + (((2f * p0) - (5f * p1) + (4f * p2) - p3) * t2)
            + ((-p0 + (3f * p1) - (3f * p2) + p3) * t3));
    }

    private static int Mod(int value, int modulus) => ((value % modulus) + modulus) % modulus;

    private static float Wrap360(float degrees)
    {
        var wrapped = degrees % 360f;
        return wrapped < 0f ? wrapped + 360f : wrapped;
    }

    private static (float Hue, float Saturation, float Value) ToHsv(LinearRgb colour)
    {
        var r = Math.Clamp(colour.Red, 0f, 1f);
        var g = Math.Clamp(colour.Green, 0f, 1f);
        var b = Math.Clamp(colour.Blue, 0f, 1f);
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        var delta = max - min;

        float hue;
        if (delta <= 1e-6f)
        {
            hue = 0f;
        }
        else if (max == r)
        {
            hue = 60f * (((g - b) / delta) % 6f);
        }
        else if (max == g)
        {
            hue = 60f * (((b - r) / delta) + 2f);
        }
        else
        {
            hue = 60f * (((r - g) / delta) + 4f);
        }

        if (hue < 0f)
        {
            hue += 360f;
        }

        var saturation = max <= 1e-6f ? 0f : delta / max;
        return (hue, saturation, max);
    }

    private static LinearRgb FromHsv(float hue, float saturation, float value)
    {
        var c = value * saturation;
        var x = c * (1f - MathF.Abs(((hue / 60f) % 2f) - 1f));
        var m = value - c;

        var (r, g, b) = hue switch
        {
            < 60f => (c, x, 0f),
            < 120f => (x, c, 0f),
            < 180f => (0f, c, x),
            < 240f => (0f, x, c),
            < 300f => (x, 0f, c),
            _ => (c, 0f, x),
        };

        return new LinearRgb(r + m, g + m, b + m);
    }
}
