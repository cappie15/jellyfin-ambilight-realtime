using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>The one side and reference colour currently being compared by eye.</summary>
public sealed record CalibrationPreview(
    CalibrationSide Side,
    LinearRgb Colour,
    PerimeterColourAdjustment Adjustment);

public enum CalibrationSide
{
    Top,
    Right,
    Bottom,
    Left,
}

/// <summary>Reference colours shared by the browser test pattern and WLED preview.</summary>
public static class CalibrationReferenceColour
{
    public static bool TryParse(string? value, out string name, out string htmlColour, out LinearRgb linearColour)
    {
        (name, htmlColour, linearColour) = value?.Trim().ToLowerInvariant() switch
        {
            "white" => ("white", "#d6d6d6", new LinearRgb(0.672f, 0.672f, 0.672f)),
            "red" => ("red", "#ff3030", new LinearRgb(1f, 0.03f, 0.03f)),
            "green" => ("green", "#30ff58", new LinearRgb(0.03f, 1f, 0.08f)),
            "blue" => ("blue", "#4070ff", new LinearRgb(0.05f, 0.16f, 1f)),
            _ => default,
        };
        return name is not null;
    }
}
