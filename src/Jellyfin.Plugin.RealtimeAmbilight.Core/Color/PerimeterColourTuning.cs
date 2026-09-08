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
    int MagentaIntensityPercent = 100)
{
    public static PerimeterColourTuning Default => new(
        100, 100, 100, 100, 100, 0, "#ffffff", 100,
        100, 100, 100, 100,
        100, 100, 100, 100,
        100, 100, 100, 100,
        100, 100, 100, 100);

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
            BuildCurve());
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
    /// Hue-shift clamped to +-21 degrees (70% of the previous +-30, per the
    /// operator's own hands-on finding: the extreme +-30 was never actually
    /// the correct colour, only ever too far off). Brightness/intensity
    /// clamped to 50-100%: a boost above the calibration photo's own value
    /// was never meaningful ("de uiterste waarden kunnen nooit de correcte
    /// kleur zijn"), only dimming down from it is.
    /// </summary>
    private static HueAnchor Anchor(int hueShiftDegrees, int brightnessPercent, int intensityPercent)
        => new(
            Math.Clamp(hueShiftDegrees, -21, 21),
            Percent(brightnessPercent, 50, 100),
            Percent(intensityPercent, 50, 100));

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
