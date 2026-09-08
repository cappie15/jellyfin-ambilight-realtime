namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

/// <summary>
/// Serializable-friendly values for the shared and per-side calibration UI.
/// Kept separate from host configuration so the same exact maths can be used
/// for a temporary calibration preview without saving it first.
/// </summary>
public readonly record struct PerimeterColourTuning(
    int BrightnessPercent,
    int SaturationPercent,
    int RedGainPercent,
    int GreenGainPercent,
    int BlueGainPercent,
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
    int LeftBlueGainPercent)
{
    public static PerimeterColourTuning Default => new(
        100, 100, 100, 100, 100, "#ffffff", 100,
        100, 100, 100, 100,
        100, 100, 100, 100,
        100, 100, 100, 100,
        100, 100, 100, 100);

    public PerimeterColourAdjustment ToAdjustment()
    {
        var wall = WallColourCorrection.FromHtmlColour(WallColourHex, WallColourCorrectionPercent);
        var global = new ColourAdjustment(
            Percent(BrightnessPercent, 1, 100),
            Percent(SaturationPercent, 50, 200),
            Percent(RedGainPercent, 50, 150) * wall.RedGain,
            Percent(GreenGainPercent, 50, 150) * wall.GreenGain,
            Percent(BlueGainPercent, 50, 150) * wall.BlueGain);

        return new PerimeterColourAdjustment(
            global,
            Side(TopBrightnessPercent, TopRedGainPercent, TopGreenGainPercent, TopBlueGainPercent),
            Side(RightBrightnessPercent, RightRedGainPercent, RightGreenGainPercent, RightBlueGainPercent),
            Side(BottomBrightnessPercent, BottomRedGainPercent, BottomGreenGainPercent, BottomBlueGainPercent),
            Side(LeftBrightnessPercent, LeftRedGainPercent, LeftGreenGainPercent, LeftBlueGainPercent));
    }

    private static ColourAdjustment Side(int brightness, int red, int green, int blue)
        => new(
            Percent(brightness, 1, 100),
            1f,
            Percent(red, 50, 150),
            Percent(green, 50, 150),
            Percent(blue, 50, 150));

    private static float Percent(int value, int minimum, int maximum)
        => Math.Clamp(value, minimum, maximum) / 100f;
}
