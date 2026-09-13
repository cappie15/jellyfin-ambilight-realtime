using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Color;

public sealed class GreyBalanceCurveTests
{
    [Fact]
    public void IdentityCurveLeavesEveryValueUntouched()
    {
        Assert.True(GreyBalanceCurve.Identity.IsIdentity);
        Assert.Equal(GreyBalancePoint.Identity, GreyBalanceCurve.Identity.GainAt(0.02f));
        Assert.Equal(GreyBalancePoint.Identity, GreyBalanceCurve.Identity.GainAt(0.5f));
    }

    [Fact]
    public void ValuesAtOrBelowThe20PercentAnchorUseItExactly()
    {
        var grey20 = new GreyBalancePoint(1.2f, 1f, 0.8f);
        var curve = new GreyBalanceCurve(grey20, GreyBalancePoint.Identity, GreyBalancePoint.Identity);

        Assert.Equal(grey20, curve.GainAt(0f));
        Assert.Equal(grey20, curve.GainAt(0.033104766f));
    }

    [Fact]
    public void ValuesAtOrAboveWhiteUseItExactly()
    {
        var white = new GreyBalancePoint(1.2f, 1f, 0.8f);
        var curve = new GreyBalanceCurve(GreyBalancePoint.Identity, GreyBalancePoint.Identity, white);

        Assert.Equal(white, curve.GainAt(0.318546778f));
        Assert.Equal(white, curve.GainAt(1f));
    }

    [Fact]
    public void MidwayBetweenTwoAnchorsBlendsThemEvenly()
    {
        var grey20 = new GreyBalancePoint(1.2f, 1f, 0.8f);
        var grey40 = new GreyBalancePoint(1f, 1f, 1f);
        var curve = new GreyBalanceCurve(grey20, grey40, GreyBalancePoint.Identity);

        var midpoint = (0.033104766f + 0.132868322f) / 2f;
        var gain = curve.GainAt(midpoint);

        Assert.Equal(1.1f, gain.RedGain, 4);
        Assert.Equal(0.9f, gain.BlueGain, 4);
    }
}
