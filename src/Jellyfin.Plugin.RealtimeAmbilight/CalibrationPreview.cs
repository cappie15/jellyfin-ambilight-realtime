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
            "yellow" => ("yellow", "#f0d040", new LinearRgb(0.88f, 0.65f, 0.04f)),
            "purple" => ("purple", "#a060e0", new LinearRgb(0.35f, 0.12f, 0.76f)),
            "orange" => ("orange", "#ff8a40", new LinearRgb(1f, 0.25f, 0.05f)),
            _ => default,
        };
        return name is not null;
    }
}

/// <summary>
/// A nature photograph credited to its Wallhaven uploader, shown centrally on
/// the calibration pattern page for context. It is decorative only: Ambilight
/// never samples it, so it is never part of what the operator is matching.
/// </summary>
public sealed record CalibrationPhoto(string ImageUrl, string UploaderName, string UploaderProfileUrl, string SourcePageUrl);

/// <summary>
/// The settings page's colour-tuning wizard: a fixed, logical order of colours
/// to walk through, and the curated photography shown for each. White has none
/// -- a photograph never renders a clean, camera-agnostic white the way a flat
/// colour plane does, so that step shows a plain white block instead.
/// </summary>
public static class CalibrationWizard
{
    public static readonly IReadOnlyList<string> ColourOrder = ["White", "Blue", "Red", "Green", "Yellow", "Purple", "Orange"];

    /// <summary>
    /// Two to three photographs per colour, each checked by hand for a clean,
    /// dominant patch of that hue and a still-active Wallhaven uploader to
    /// credit. Hotlinked at Wallhaven's "large" thumbnail size rather than the
    /// full original, which can exceed 10 MB and is unnecessary for a TV-sized
    /// central image.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<CalibrationPhoto>> Photos =
        new Dictionary<string, IReadOnlyList<CalibrationPhoto>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Blue"] = new List<CalibrationPhoto>
            {
                new("https://th.wallhaven.cc/lg/6q/6qoe7l.jpg", "KnightSabes", "https://wallhaven.cc/user/KnightSabes", "https://wallhaven.cc/w/6qoe7l"),
                new("https://th.wallhaven.cc/lg/lm/lmddky.jpg", "haluolibai", "https://wallhaven.cc/user/haluolibai", "https://wallhaven.cc/w/lmddky"),
                new("https://th.wallhaven.cc/lg/42/42qxzy.jpg", "wapconwap", "https://wallhaven.cc/user/wapconwap", "https://wallhaven.cc/w/42qxzy"),
            },
            ["Red"] = new List<CalibrationPhoto>
            {
                new("https://th.wallhaven.cc/lg/ox/ox7e39.jpg", "WallHaven4o", "https://wallhaven.cc/user/WallHaven4o", "https://wallhaven.cc/w/ox7e39"),
                new("https://th.wallhaven.cc/lg/3z/3z5dq6.jpg", "bhupsi", "https://wallhaven.cc/user/bhupsi", "https://wallhaven.cc/w/3z5dq6"),
            },
            ["Green"] = new List<CalibrationPhoto>
            {
                new("https://th.wallhaven.cc/lg/nm/nme6e8.jpg", "czort", "https://wallhaven.cc/user/czort", "https://wallhaven.cc/w/nme6e8"),
                new("https://th.wallhaven.cc/lg/j3/j3k2mw.jpg", "bhupsi", "https://wallhaven.cc/user/bhupsi", "https://wallhaven.cc/w/j3k2mw"),
                new("https://th.wallhaven.cc/lg/72/72wxvo.jpg", "bhupsi", "https://wallhaven.cc/user/bhupsi", "https://wallhaven.cc/w/72wxvo"),
            },
            ["Yellow"] = new List<CalibrationPhoto>
            {
                new("https://th.wallhaven.cc/lg/nz/nz1e7o.jpg", "vfgx", "https://wallhaven.cc/user/vfgx", "https://wallhaven.cc/w/nz1e7o"),
                new("https://th.wallhaven.cc/lg/nk/nkl3rd.jpg", "sergiucoj", "https://wallhaven.cc/user/sergiucoj", "https://wallhaven.cc/w/nkl3rd"),
                new("https://th.wallhaven.cc/lg/g8/g8xvmd.jpg", "WallHaven4o", "https://wallhaven.cc/user/WallHaven4o", "https://wallhaven.cc/w/g8xvmd"),
            },
            ["Purple"] = new List<CalibrationPhoto>
            {
                new("https://th.wallhaven.cc/lg/0j/0jr8j5.jpg", "hahoan9", "https://wallhaven.cc/user/hahoan9", "https://wallhaven.cc/w/0jr8j5"),
                new("https://th.wallhaven.cc/lg/dg/dgz67l.jpg", "funkymonk017", "https://wallhaven.cc/user/funkymonk017", "https://wallhaven.cc/w/dgz67l"),
                new("https://th.wallhaven.cc/lg/ym/ym9r27.jpg", "Pc7", "https://wallhaven.cc/user/Pc7", "https://wallhaven.cc/w/ym9r27"),
            },
            ["Orange"] = new List<CalibrationPhoto>
            {
                new("https://th.wallhaven.cc/lg/z8/z8owlj.jpg", "bhupsi", "https://wallhaven.cc/user/bhupsi", "https://wallhaven.cc/w/z8owlj"),
                new("https://th.wallhaven.cc/lg/95/958k2w.jpg", "UAman", "https://wallhaven.cc/user/UAman", "https://wallhaven.cc/w/958k2w"),
                new("https://th.wallhaven.cc/lg/72/72w8pv.jpg", "bhupsi", "https://wallhaven.cc/user/bhupsi", "https://wallhaven.cc/w/72w8pv"),
            },
        };

    public static CalibrationPhoto? PhotoAt(string colourName, int photoIndex)
    {
        if (!Photos.TryGetValue(colourName, out var photos) || photos.Count == 0)
        {
            return null;
        }

        return photos[((photoIndex % photos.Count) + photos.Count) % photos.Count];
    }
}

/// <summary>
/// The wizard's current position, shared between the settings page (which
/// drives it) and the anonymous TV pattern page (which polls it). In-memory
/// only: a restart returns to the first step, which is harmless since nothing
/// here is a saved setting.
/// </summary>
public sealed class CalibrationWizardState
{
    private int _stepIndex;
    private int _photoIndex;
    private CalibrationSide _side = CalibrationSide.Top;

    public int StepIndex => _stepIndex;

    public int PhotoIndex => _photoIndex;

    public CalibrationSide Side => _side;

    public string ColourName => CalibrationWizard.ColourOrder[_stepIndex];

    public void MoveTo(int stepIndex, int photoIndex, CalibrationSide side)
    {
        _stepIndex = Math.Clamp(stepIndex, 0, CalibrationWizard.ColourOrder.Count - 1);
        _photoIndex = Math.Max(0, photoIndex);
        _side = side;
    }
}
