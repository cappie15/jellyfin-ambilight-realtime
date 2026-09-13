namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

/// <summary>One grey-balance anchor: a per-channel gain, multiplied in on top of whatever the White step's own flat gain already provides.</summary>
public readonly record struct GreyBalancePoint(float RedGain, float GreenGain, float BlueGain)
{
    public static GreyBalancePoint Identity => new(1f, 1f, 1f);

    public bool IsIdentity => RedGain == 1f && GreenGain == 1f && BlueGain == 1f;
}

/// <summary>
/// A colour strip's white balance can drift with brightness (duty-cycle
/// dependent channel response), not just at maximum output -- the flat,
/// brightness-independent gain the White step sets is not always enough.
/// This layers an additional, brightness-dependent correction on top of it:
/// three anchors -- 20% grey, 40% grey, and White itself -- each an
/// independent per-channel gain, piecewise-linearly interpolated by the
/// pixel's own value (its largest channel, same measure
/// <see cref="HueCorrectionCurve"/> calls "value"). Flat outside the anchor
/// range: below the 20% anchor or above White, the nearest anchor's own
/// gain applies unchanged rather than extrapolating.
/// </summary>
/// <remarks>
/// The three anchor positions are not round numbers: they are the exact
/// linear-light value the calibration wizard's own 20%/40%/White swatches
/// decode to (sRGB bytes 51/102/153 -- see <see cref="CalibrationWizard.SwatchColours"/>),
/// so tuning while looking at the "20% grey" swatch lands exactly on that
/// anchor rather than somewhere off to the side of it.
/// </remarks>
public sealed class GreyBalanceCurve
{
    private const float Grey20Value = 0.033104766f;
    private const float Grey40Value = 0.132868322f;
    private const float WhiteValue = 0.318546778f;

    private readonly GreyBalancePoint _grey20;
    private readonly GreyBalancePoint _grey40;
    private readonly GreyBalancePoint _white;

    public GreyBalanceCurve(GreyBalancePoint grey20, GreyBalancePoint grey40, GreyBalancePoint white)
    {
        _grey20 = grey20;
        _grey40 = grey40;
        _white = white;
    }

    public static GreyBalanceCurve Identity { get; } = new(GreyBalancePoint.Identity, GreyBalancePoint.Identity, GreyBalancePoint.Identity);

    public bool IsIdentity => _grey20.IsIdentity && _grey40.IsIdentity && _white.IsIdentity;

    public GreyBalancePoint GainAt(float value)
    {
        if (value <= Grey20Value)
        {
            return _grey20;
        }

        if (value <= Grey40Value)
        {
            return Lerp(_grey20, _grey40, (value - Grey20Value) / (Grey40Value - Grey20Value));
        }

        if (value <= WhiteValue)
        {
            return Lerp(_grey40, _white, (value - Grey40Value) / (WhiteValue - Grey40Value));
        }

        return _white;
    }

    private static GreyBalancePoint Lerp(GreyBalancePoint a, GreyBalancePoint b, float t)
        => new(
            a.RedGain + ((b.RedGain - a.RedGain) * t),
            a.GreenGain + ((b.GreenGain - a.GreenGain) * t),
            a.BlueGain + ((b.BlueGain - a.BlueGain) * t));
}
