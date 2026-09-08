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

    /// <summary>
    /// The whole perimeter at once. Most installations run one continuous strip
    /// of the same bin around the entire TV, so this -- not one side at a time
    /// -- is the wizard's default: there is rarely a reason to tune sides
    /// separately until the whole-screen result already looks close.
    /// </summary>
    All,
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
    /// credit. Linked at Wallhaven's full original resolution -- proxied
    /// same-origin by <c>Calibration/Photo</c>, both so the TV's canvas can
    /// read its pixels at all (a cross-origin image with no CORS header taints
    /// the canvas) and so it fills a 4K panel without visibly upscaling.
    /// Every photo here is at least 3840 px on its long edge except Yellow's:
    /// Wallhaven's yellow swatch, searched every way tried this session,
    /// mostly returns near-black astrophotography with a small yellow star or
    /// moon rather than a true yellow scene: worth another pass later.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<CalibrationPhoto>> Photos =
        new Dictionary<string, IReadOnlyList<CalibrationPhoto>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Blue"] = new List<CalibrationPhoto>
            {
                new("https://w.wallhaven.cc/full/lm/wallhaven-lmyjep.jpg", "Todd", "https://wallhaven.cc/user/Todd", "https://wallhaven.cc/w/lmyjep"),
                new("https://w.wallhaven.cc/full/qd/wallhaven-qd8dkq.jpg", "Jase", "https://wallhaven.cc/user/Jase", "https://wallhaven.cc/w/qd8dkq"),
            },
            ["Red"] = new List<CalibrationPhoto>
            {
                new("https://w.wallhaven.cc/full/ox/wallhaven-ox7e39.jpg", "WallHaven4o", "https://wallhaven.cc/user/WallHaven4o", "https://wallhaven.cc/w/ox7e39"),
                new("https://w.wallhaven.cc/full/96/wallhaven-96kyk1.jpg", "WallHaven4o", "https://wallhaven.cc/user/WallHaven4o", "https://wallhaven.cc/w/96kyk1"),
            },
            ["Green"] = new List<CalibrationPhoto>
            {
                new("https://w.wallhaven.cc/full/jx/wallhaven-jxevl5.jpg", "IDromaI", "https://wallhaven.cc/user/IDromaI", "https://wallhaven.cc/w/jxevl5"),
                new("https://w.wallhaven.cc/full/7p/wallhaven-7pemgy.jpg", "唉幺魏", "https://wallhaven.cc/user/%E5%94%89%E5%B9%BA%E9%AD%8F", "https://wallhaven.cc/w/7pemgy"),
            },
            ["Yellow"] = new List<CalibrationPhoto>
            {
                new("https://w.wallhaven.cc/full/nz/wallhaven-nz1e7o.jpg", "vfgx", "https://wallhaven.cc/user/vfgx", "https://wallhaven.cc/w/nz1e7o"),
                new("https://w.wallhaven.cc/full/nk/wallhaven-nkl3rd.jpg", "sergiucoj", "https://wallhaven.cc/user/sergiucoj", "https://wallhaven.cc/w/nkl3rd"),
                new("https://w.wallhaven.cc/full/g8/wallhaven-g8xvmd.jpg", "WallHaven4o", "https://wallhaven.cc/user/WallHaven4o", "https://wallhaven.cc/w/g8xvmd"),
            },
            ["Purple"] = new List<CalibrationPhoto>
            {
                new("https://w.wallhaven.cc/full/vg/wallhaven-vgeqmm.jpg", "XLighninRodX", "https://wallhaven.cc/user/XLighninRodX", "https://wallhaven.cc/w/vgeqmm"),
                new("https://w.wallhaven.cc/full/w8/wallhaven-w8od2q.jpg", "microcosmos", "https://wallhaven.cc/user/microcosmos", "https://wallhaven.cc/w/w8od2q"),
                new("https://w.wallhaven.cc/full/ym/wallhaven-ym9r27.jpg", "Pc7", "https://wallhaven.cc/user/Pc7", "https://wallhaven.cc/w/ym9r27"),
            },
            ["Orange"] = new List<CalibrationPhoto>
            {
                new("https://w.wallhaven.cc/full/n6/wallhaven-n6mmql.jpg", "DayWalk3r1988", "https://wallhaven.cc/user/DayWalk3r1988", "https://wallhaven.cc/w/n6mmql"),
                new("https://w.wallhaven.cc/full/8o/wallhaven-8ooye2.jpg", "Wosh", "https://wallhaven.cc/user/Wosh", "https://wallhaven.cc/w/8ooye2"),
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
    /// <summary>
    /// A poll gap wider than this means a browser just (re)opened the TV page
    /// rather than continuing an existing session's steady 1.5 s polling, so
    /// it is treated as a fresh calibration starting over.
    /// </summary>
    private static readonly TimeSpan TvReconnectGap = TimeSpan.FromSeconds(20);

    private int _stepIndex;
    private int _photoIndex;
    private CalibrationSide _side = CalibrationSide.All;
    private DateTimeOffset _lastTvPollAt = DateTimeOffset.MinValue;

    public int StepIndex => _stepIndex;

    public int PhotoIndex => _photoIndex;

    public CalibrationSide Side => _side;

    public string ColourName => CalibrationWizard.ColourOrder[_stepIndex];

    /// <summary>True while the TV page's own poll has landed within the last few seconds.</summary>
    public bool TvConnected => DateTimeOffset.UtcNow - _lastTvPollAt < TimeSpan.FromSeconds(5);

    public void MoveTo(int stepIndex, int photoIndex, CalibrationSide side)
    {
        _stepIndex = Math.Clamp(stepIndex, 0, CalibrationWizard.ColourOrder.Count - 1);
        _photoIndex = Math.Max(0, photoIndex);
        _side = side;
    }

    /// <summary>
    /// Called on every TV-page poll. Restarts the wizard at White when the gap
    /// since the previous poll shows this is a new TV session, not a
    /// continuation -- opening the link is then all it takes to begin, and the
    /// settings page lands on step 1 the moment it next asks for the state.
    /// </summary>
    /// <returns><see langword="true"/> when this poll just (re)started the wizard.</returns>
    public bool NoteTvPoll()
    {
        var now = DateTimeOffset.UtcNow;
        var reconnected = now - _lastTvPollAt > TvReconnectGap;
        if (reconnected)
        {
            _stepIndex = 0;
            _photoIndex = 0;
        }

        _lastTvPollAt = now;
        return reconnected;
    }
}
