namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

/// <summary>
/// Serializable-friendly values for the shared and per-side calibration UI.
/// Kept separate from host configuration so the same exact maths can be used
/// for a temporary calibration preview without saving it first.
/// </summary>
/// <remarks>
/// <see cref="RedGainPercent"/>/<see cref="GreenGainPercent"/>/
/// <see cref="BlueGainPercent"/> remain here for the White step's own
/// colour-temperature control (a plain red/blue push-pull, unchanged) and
/// for backward compatibility with an existing saved configuration, but no
/// longer drive the wizard's Red/Green/Blue/Yellow/Cyan/Magenta steps or the
/// general WLED colour correction those steps produce -- that is now the six
/// <c>Hue*</c>/<c>Brightness*</c>/<c>Intensity*</c> anchor triples below, via
/// <see cref="HueCorrectionCurve"/>.
/// </remarks>
public readonly record struct PerimeterColourTuning(
    int BrightnessPercent,
    int SaturationPercent,
    int RedGainPercent,
    int GreenGainPercent,
    int BlueGainPercent,
    int BlackLevelFloorPercent,
    string? WallColourHex,
    int WallColourCorrectionPercent,
    int TopBrightnessPercent,
    int TopRedGainPercent,
    int TopGreenGainPercent,
    int TopBlueGainPercent,
    int RightBrightnessPercent,
    int RightRedGainPercent,
    int RightGreenGainPercent,
    int RightBlueGainPercent,
    int BottomBrightnessPercent,
    int BottomRedGainPercent,
    int BottomGreenGainPercent,
    int BottomBlueGainPercent,
    int LeftBrightnessPercent,
    int LeftRedGainPercent,
    int LeftGreenGainPercent,
    int LeftBlueGainPercent,
    int RedHueShiftDegrees = 0,
    int RedBrightnessPercent = 100,
    int RedIntensityPercent = 100,
    int GreenHueShiftDegrees = 0,
    int GreenBrightnessPercent = 100,
    int GreenIntensityPercent = 100,
    int BlueHueShiftDegrees = 0,
    int BlueBrightnessPercent = 100,
    int BlueIntensityPercent = 100,
    int YellowHueShiftDegrees = 0,
    int YellowBrightnessPercent = 100,
    int YellowIntensityPercent = 100,
    int CyanHueShiftDegrees = 0,
    int CyanBrightnessPercent = 100,
    int CyanIntensityPercent = 100,
    int MagentaHueShiftDegrees = 0,
    int MagentaBrightnessPercent = 100,
    int MagentaIntensityPercent = 100,
    int WhiteChannelStrengthPercent = 100,
    int Grey20TemperatureDelta = 0,
    int Grey20TintDelta = 0,
    int Grey40TemperatureDelta = 0,
    int Grey40TintDelta = 0)
{
    public static PerimeterColourTuning Default => new(
        100, 100, 100, 100, 100, 0, "#ffffff", 100,
        100, 100, 100, 100,
        100, 100, 100, 100,
        100, 100, 100, 100,
        100, 100, 100, 100);

    /// <summary>
    /// A copy with every general/shared multiplier reset to neutral (100%,
    /// or the wall colour's own "no correction" white) -- the six hue
    /// anchors, <see cref="BlackLevelFloorPercent"/> and
    /// <see cref="WhiteChannelStrengthPercent"/> untouched.
    /// </summary>
    /// <remarks>
    /// Used for the wizard's own live preview while auditioning one of the
    /// six hue-anchor steps, and (with <paramref name="preserveWhiteBalanceGain"/>)
    /// while auditioning White itself.
    /// An aggressively tuned overall Saturation/Brightness applies *after*
    /// the curve (see <see cref="ToAdjustment"/>'s own remark) and clamps to
    /// 0-1, so it can silently swallow a small anchor correction before it
    /// ever reaches the LEDs -- measured directly against an operator's own
    /// real 180% saturation: the Intensity anchor's effect was clipped away
    /// entirely above roughly 85%, and one hue-shift direction disappeared
    /// while the other kept working, purely because of which side of the
    /// clamp the post-curve stretch landed on. Neutralizing the general
    /// multipliers for this one preview removes that interaction so every
    /// step of every anchor slider has a real, visible effect regardless of
    /// how the operator has tuned the rest of the picture.
    /// </remarks>
    /// <param name="preserveWhiteBalanceGain">
    /// True for the White step itself: <see cref="RedGainPercent"/>/
    /// <see cref="GreenGainPercent"/>/<see cref="BlueGainPercent"/> are what
    /// its own slider directly tests, so those three are left as sent
    /// rather than reset to 100 -- but <see cref="WallColourHex"/>/
    /// <see cref="WallColourCorrectionPercent"/> are still neutralized here
    /// too, since they multiply into those same three gains
    /// (<see cref="ToAdjustment"/>) without the White slider touching them:
    /// measured directly against an operator's own real warm-toned wall
    /// (<c>#cabfaf</c>, correcting green ~13% and blue ~38% brighter to
    /// compensate for what the wall itself absorbs), pushing the White
    /// slider toward "warmer" (which only lowers the raw blue gain) left
    /// the wall's own untouched green correction increasingly dominant over
    /// the shrinking blue channel, so white read as drifting green instead
    /// of orange. Real playback always applies the true wall correction
    /// regardless of this preview-only neutralization.
    /// </param>
    public PerimeterColourTuning WithGeneralAdjustmentsNeutralized(bool preserveWhiteBalanceGain = false)
        => this with
        {
            BrightnessPercent = 100,
            SaturationPercent = 100,
            RedGainPercent = preserveWhiteBalanceGain ? RedGainPercent : 100,
            GreenGainPercent = preserveWhiteBalanceGain ? GreenGainPercent : 100,
            BlueGainPercent = preserveWhiteBalanceGain ? BlueGainPercent : 100,
            WallColourHex = "#ffffff",
            WallColourCorrectionPercent = 100,
            TopBrightnessPercent = 100,
            TopRedGainPercent = 100,
            TopGreenGainPercent = 100,
            TopBlueGainPercent = 100,
            RightBrightnessPercent = 100,
            RightRedGainPercent = 100,
            RightGreenGainPercent = 100,
            RightBlueGainPercent = 100,
            BottomBrightnessPercent = 100,
            BottomRedGainPercent = 100,
            BottomGreenGainPercent = 100,
            BottomBlueGainPercent = 100,
            LeftBrightnessPercent = 100,
            LeftRedGainPercent = 100,
            LeftGreenGainPercent = 100,
            LeftBlueGainPercent = 100,
        };

    public PerimeterColourAdjustment ToAdjustment()
    {
        var wall = WallColourCorrection.FromHtmlColour(WallColourHex, WallColourCorrectionPercent);
        var global = new ColourAdjustment(
            Percent(BrightnessPercent, 1, 200),
            Percent(SaturationPercent, 50, 200),
            // 40-160%, not the 50-150% every other gain here uses: the
            // White step's colour-temperature slider was widened by 20%
            // (its own range, ±50 to ±60) on the operator's explicit
            // request, so its underlying gain clamp widens to match --
            // narrower would silently cap the slider before it reaches its
            // own extremes.
            Percent(RedGainPercent, 40, 160) * wall.RedGain,
            Percent(GreenGainPercent, 50, 150) * wall.GreenGain,
            Percent(BlueGainPercent, 40, 160) * wall.BlueGain);

        return new PerimeterColourAdjustment(
            global,
            Side(TopBrightnessPercent, TopRedGainPercent, TopGreenGainPercent, TopBlueGainPercent),
            Side(RightBrightnessPercent, RightRedGainPercent, RightGreenGainPercent, RightBlueGainPercent),
            Side(BottomBrightnessPercent, BottomRedGainPercent, BottomGreenGainPercent, BottomBlueGainPercent),
            Side(LeftBrightnessPercent, LeftRedGainPercent, LeftGreenGainPercent, LeftBlueGainPercent),
            Math.Clamp(BlackLevelFloorPercent, 0, 20) / 100f,
            BuildCurve(),
            Percent(WhiteChannelStrengthPercent, 20, 100),
            BuildGreyBalanceCurve());
    }

    /// <summary>
    /// White itself is always the identity anchor here: its own colour
    /// temperature is the flat <see cref="RedGainPercent"/>/
    /// <see cref="BlueGainPercent"/> gain above, not this curve: this curve
    /// only carries how much the 20%/40% grey steps deviate from that.
    /// </summary>
    private GreyBalanceCurve BuildGreyBalanceCurve()
        => new(
            GreyPoint(Grey20TemperatureDelta, Grey20TintDelta),
            GreyPoint(Grey40TemperatureDelta, Grey40TintDelta),
            GreyBalancePoint.Identity);

    /// <summary>
    /// Same push-pull mechanic as the White step's own colour-temperature
    /// slider (temperature moves red against blue), plus a second,
    /// independent green-against-magenta axis the White step does not need
    /// since these two grey steps are the only ones asking for it. Both
    /// clamped to +-60, the same range the White step's own slider uses.
    /// </summary>
    private static GreyBalancePoint GreyPoint(int temperatureDelta, int tintDelta)
    {
        var temperature = Math.Clamp(temperatureDelta, -60, 60) / 100f;
        var tint = Math.Clamp(tintDelta, -60, 60) / 100f;
        return new GreyBalancePoint(1f + temperature, 1f + tint, 1f - temperature);
    }

    private HueCorrectionCurve BuildCurve()
        => new(
            Anchor(RedHueShiftDegrees, RedBrightnessPercent, RedIntensityPercent),
            Anchor(YellowHueShiftDegrees, YellowBrightnessPercent, YellowIntensityPercent),
            Anchor(GreenHueShiftDegrees, GreenBrightnessPercent, GreenIntensityPercent),
            Anchor(CyanHueShiftDegrees, CyanBrightnessPercent, CyanIntensityPercent),
            Anchor(BlueHueShiftDegrees, BlueBrightnessPercent, BlueIntensityPercent),
            Anchor(MagentaHueShiftDegrees, MagentaBrightnessPercent, MagentaIntensityPercent));

    /// <summary>
    /// Hue-shift clamped to +-23 degrees (widened from an earlier +-21 once
    /// real-room tuning against actual HDR10 content, 2026-09-14, pushed
    /// Magenta's own shift to exactly that +-21 boundary -- the same reason
    /// Intensity's own ceiling below was widened -- itself down from a +-30
    /// this project started with, per the operator's own earlier hands-on
    /// finding that the extreme +-30 was never actually the correct colour,
    /// only ever too far off). Brightness stays capped at 100%: a boost
    /// above the calibration photo's own value was never meaningful ("de
    /// uiterste waarden kunnen nooit de correcte kleur zijn"), only dimming
    /// down from it is. Its floor was widened from 50% to 20% on the
    /// operator's own finding that a secondary colour (Yellow/Cyan/Magenta)
    /// lights two physical LED channels at once rather than one, so it can
    /// run measurably brighter than a primary to begin with and 50% was not
    /// always enough headroom to dim it back down to match.
    /// <para>
    /// Intensity's ceiling, unlike Brightness's, was widened past 100% (to
    /// 220%, 10% past the range's own previous 200% ceiling, once real-room
    /// tuning against actual HDR10 content pushed Cyan to exactly 200% --
    /// the swatch's own 100% saturation is a mathematical ceiling, not a
    /// physical one: a real strip's gamut at a given hue (magenta especially,
    /// which needs simultaneous red and blue channel drive) can render
    /// measurably less vivid than that ideal regardless of what the anchor
    /// asks for, so an operator needs to be able to push past "the swatch's
    /// own value" for the same reason Brightness (and the shared, global
    /// Brightness/Saturation controls) already can.
    /// </para>
    /// </summary>
    private static HueAnchor Anchor(int hueShiftDegrees, int brightnessPercent, int intensityPercent)
        => new(
            Math.Clamp(hueShiftDegrees, -23, 23),
            Percent(brightnessPercent, 20, 100),
            Percent(intensityPercent, 50, 220));

    private static ColourAdjustment Side(int brightness, int red, int green, int blue)
        => new(
            Percent(brightness, 1, 200),
            1f,
            Percent(red, 50, 150),
            Percent(green, 50, 150),
            Percent(blue, 50, 150));

    private static float Percent(int value, int minimum, int maximum)
        => Math.Clamp(value, minimum, maximum) / 100f;
}
