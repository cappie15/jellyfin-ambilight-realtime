namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>
/// The settings page's colour-tuning wizard: a fixed, logical order of steps.
/// Every step's edges are sampled through the ordinary Ambilight pipeline,
/// same as real video -- White from the operator's own photos, the six
/// primary/secondary steps from a rendered flat-colour swatch (see
/// <see cref="SwatchColours"/>), and the finetuning steps from the
/// operator's own two-colour photos.
/// </summary>
/// <remarks>
/// <para>
/// Three phases, one continuous sequence. The first ("tuning", White plus
/// every RGB primary and secondary) builds the six-anchor
/// <see cref="Core.Color.HueCorrectionCurve"/> from a synthetic swatch at
/// each colour's own canonical hue -- not a photo, so nothing about the
/// photo's own white balance or exposure can bias the reading -- while White
/// keeps its own three real photos, cycled via "try another photo", exactly
/// as before. The second ("finetuning") walks the same six anchors again
/// through the operator's real two-colour photos, offering both colours'
/// sliders together so the anchors can be refined with real-photo context
/// instead of an isolated swatch, per <see cref="FinetuningColourPairs"/>.
/// </para>
/// <para>
/// <c>s3_finaltest.jpg</c> was dropped rather than kept as a seventh
/// finetuning step: it is a genuinely multi-hue sunset/mountain scene (pink
/// sky, blue haze, green ridge) with no clean two-colour pair to attach
/// sliders to, and every other step here exists specifically to refine one
/// pair of the six anchors -- a step that cannot name which two would not
/// have added anything to the calibration itself.
/// </para>
/// </remarks>
public static class CalibrationWizard
{
    public static readonly IReadOnlyList<string> TuningOrder =
        ["White", "Red", "Green", "Blue", "Yellow", "Cyan", "Magenta"];

    /// <summary>
    /// Two-colour real photos, each refining one specific pair of the six
    /// anchors built during tuning. Chosen from the operator's own existing
    /// photo set by what each photo actually, visibly contains -- not by
    /// filename alone.
    /// </summary>
    public static readonly IReadOnlyList<string> ConfirmationOrder =
        ["Blue-Green", "Orange-Red", "Purple-Teal", "Yellow-Pink"];

    public static readonly IReadOnlyList<string> ColourOrder = [.. TuningOrder, .. ConfirmationOrder];

    /// <summary>Embedded JPEG file names (under <c>Configuration/CalibrationPhotos</c>) for each step that uses one.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Photos =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["White"] = ["s0_whitelevel1.jpg", "s0_whitelevel2.jpg", "s0_whitelevel3.jpg"],
            ["Blue-Green"] = ["s3_blue_green.jpg"],
            ["Orange-Red"] = ["s3_orange_red.jpg"],
            ["Purple-Teal"] = ["s3_purple_teal.jpg"],
            ["Yellow-Pink"] = ["s4_yellow_pink.jpg"],
        };

    /// <summary>
    /// Canonical sRGB byte triple for each swatch step's own hue at full
    /// saturation and value (red 0°, yellow 60°, green 120°, cyan 180°, blue
    /// 240°, magenta 300° -- the same six angles <c>HueCorrectionCurve</c>
    /// anchors on), rendered flat and sampled through the real edge-sampling
    /// pipeline exactly as a photo would be.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (byte Red, byte Green, byte Blue)> SwatchColours =
        new Dictionary<string, (byte, byte, byte)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Red"] = (255, 0, 0),
            ["Yellow"] = (255, 255, 0),
            ["Green"] = (0, 255, 0),
            ["Cyan"] = (0, 255, 255),
            ["Blue"] = (0, 0, 255),
            ["Magenta"] = (255, 0, 255),
        };

    /// <summary>
    /// The two anchor colour names each finetuning step's photo actually
    /// shows and should offer sliders for.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string First, string Second)> FinetuningColourPairs =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Blue-Green"] = ("Blue", "Green"),
            ["Orange-Red"] = ("Red", "Yellow"),
            ["Purple-Teal"] = ("Blue", "Cyan"),
            ["Yellow-Pink"] = ("Yellow", "Magenta"),
        };

    public static bool IsConfirmationStep(int stepIndex) => stepIndex >= TuningOrder.Count;

    public static string? PhotoAt(string colourName, int photoIndex)
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
/// <remarks>
/// Unarmed by default. The entire anonymous TV-facing surface --
/// <c>Pattern</c>, <c>WizardState</c> (GET), <c>Photo</c>, <c>PhotoFrame</c> --
/// checks <see cref="IsArmed"/> and answers 404 while it is false, so an
/// internet-facing Jellyfin does not carry a permanently reachable, unauthenticated
/// page. Only the admin-only <c>Start</c> action can arm it, and only
/// <c>Finish</c> (or the admin page itself, on request) disarms it again.
/// </remarks>
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
    private DateTimeOffset _lastTvPollAt = DateTimeOffset.MinValue;

    public bool IsArmed { get; private set; }

    public int StepIndex => _stepIndex;

    public int PhotoIndex => _photoIndex;

    public string ColourName => CalibrationWizard.ColourOrder[_stepIndex];

    public bool IsLastStep => _stepIndex == CalibrationWizard.ColourOrder.Count - 1;

    /// <summary>True while the TV page's own poll has landed within the last few seconds.</summary>
    public bool TvConnected => DateTimeOffset.UtcNow - _lastTvPollAt < TimeSpan.FromSeconds(5);

    /// <summary>Opens the TV-facing surface and restarts at White.</summary>
    public void Arm()
    {
        IsArmed = true;
        _stepIndex = 0;
        _photoIndex = 0;
        _lastTvPollAt = DateTimeOffset.MinValue;
    }

    /// <summary>Closes the TV-facing surface again; the position is left as-is, harmlessly, until the next Arm.</summary>
    public void Disarm() => IsArmed = false;

    public void MoveTo(int stepIndex, int photoIndex)
    {
        _stepIndex = Math.Clamp(stepIndex, 0, CalibrationWizard.ColourOrder.Count - 1);
        _photoIndex = Math.Max(0, photoIndex);
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
