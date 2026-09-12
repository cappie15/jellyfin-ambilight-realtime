namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>
/// The settings page's colour-tuning wizard: a fixed, logical order of steps.
/// Every step's edges are sampled through the ordinary Ambilight pipeline,
/// same as real video -- White and the six primary/secondary steps from a
/// rendered flat-colour swatch (see <see cref="SwatchColours"/>), and every
/// finetuning step from one of the operator's own real photos.
/// </summary>
/// <remarks>
/// <para>
/// Three phases, one continuous sequence. The first ("tuning", White plus
/// every RGB primary and secondary) builds the six-anchor
/// <see cref="Core.Color.HueCorrectionCurve"/> plus White's own red/blue
/// gain from a synthetic swatch at each colour's own canonical hue -- not a
/// photo, so nothing about a photo's own white balance or exposure can bias
/// the reading, White included (a real "white" photo is rarely perfectly
/// neutral; a synthetic (255,255,255) swatch always is). The second
/// ("finetuning") replays every one of those against the operator's own
/// real photos instead: the six hue anchors two at a time, from a two-colour
/// photo showing both (per <see cref="FinetuningColourPairs"/>), and White
/// on its own from the three original white-level photos ("White level"),
/// now here instead of at the tuning step.
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
        ["White level", "Blue-Green", "Orange-Red", "Purple-Teal", "Yellow-Pink"];

    public static readonly IReadOnlyList<string> ColourOrder = [.. TuningOrder, .. ConfirmationOrder];

    /// <summary>
    /// Embedded JPEG file names (under <c>Configuration/CalibrationPhotos</c>)
    /// for each step that uses one. White's own three real photos moved here
    /// (as "White level") from the White tuning step, which now uses a
    /// synthetic swatch like every other primary/secondary -- consistent
    /// with how each of those gets a swatch for tuning and, where a suitable
    /// real photo exists, a finetuning replay against real content.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Photos =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["White level"] = ["s0_whitelevel1.jpg", "s0_whitelevel2.jpg", "s0_whitelevel3.jpg"],
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
    /// pipeline exactly as a photo would be. White is included too, at
    /// (255,255,255): the White step's colour-temperature control no longer
    /// samples a real photo (whose own white balance and exposure could bias
    /// the reading) -- a perfectly neutral synthetic swatch means the White
    /// step starts from an exact (r=g=b) reading every time, matching the
    /// RGBW extraction's own identity case exactly at centre.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (byte Red, byte Green, byte Blue)> SwatchColours =
        new Dictionary<string, (byte, byte, byte)>(StringComparer.OrdinalIgnoreCase)
        {
            ["White"] = (255, 255, 255),
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
/// <c>Finish</c> (or the admin page itself, on request) disarms it again --
/// plus <see cref="IdleTimeout"/> disarms it on its own after a stretch with
/// neither a TV poll nor a step move, covering an admin who never comes back
/// to click Finish.
/// </remarks>
public sealed class CalibrationWizardState
{
    /// <summary>
    /// A poll gap wider than this means a browser just (re)opened the TV page
    /// rather than continuing an existing session's steady 1.5 s polling, so
    /// it is treated as a fresh calibration starting over.
    /// </summary>
    private static readonly TimeSpan TvReconnectGap = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long the anonymous TV-facing surface stays reachable with no
    /// activity at all -- no TV poll, no step move from the settings page --
    /// before it closes itself. Covers the admin closing the settings tab (or
    /// a browser crash) mid-calibration without ever reaching Finish/Cancel:
    /// without this, that anonymous surface -- including the unauthenticated
    /// photo upload -- would otherwise stay reachable on the network forever.
    /// </summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(20);

    private readonly Func<DateTimeOffset> _clock;
    private int _stepIndex;
    private int _photoIndex;
    private bool _isArmed;
    private DateTimeOffset _lastTvPollAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastActivityAt = DateTimeOffset.MinValue;

    /// <param name="clock">Defaults to <see cref="DateTimeOffset.UtcNow"/>; overridable so <see cref="IdleTimeout"/> is deterministically testable.</param>
    public CalibrationWizardState(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// True only while actually armed AND not yet idle for <see cref="IdleTimeout"/>;
    /// crossing the timeout disarms as a side effect of the check itself, so
    /// every caller of this property (the whole anonymous surface, and the
    /// settings page's own state poll) enforces the timeout for free.
    /// </summary>
    public bool IsArmed
    {
        get
        {
            if (_isArmed && _clock() - _lastActivityAt > IdleTimeout)
            {
                _isArmed = false;
            }

            return _isArmed;
        }
    }

    public int StepIndex => _stepIndex;

    public int PhotoIndex => _photoIndex;

    public string ColourName => CalibrationWizard.ColourOrder[_stepIndex];

    public bool IsLastStep => _stepIndex == CalibrationWizard.ColourOrder.Count - 1;

    /// <summary>True while the TV page's own poll has landed within the last few seconds.</summary>
    public bool TvConnected => _clock() - _lastTvPollAt < TimeSpan.FromSeconds(5);

    /// <summary>Opens the TV-facing surface and restarts at White.</summary>
    public void Arm()
    {
        _isArmed = true;
        _stepIndex = 0;
        _photoIndex = 0;
        _lastTvPollAt = DateTimeOffset.MinValue;
        _lastActivityAt = _clock();
    }

    /// <summary>Closes the TV-facing surface again; the position is left as-is, harmlessly, until the next Arm.</summary>
    public void Disarm() => _isArmed = false;

    public void MoveTo(int stepIndex, int photoIndex)
    {
        _stepIndex = Math.Clamp(stepIndex, 0, CalibrationWizard.ColourOrder.Count - 1);
        _photoIndex = Math.Max(0, photoIndex);
        _lastActivityAt = _clock();
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
        var now = _clock();
        var reconnected = now - _lastTvPollAt > TvReconnectGap;
        if (reconnected)
        {
            _stepIndex = 0;
            _photoIndex = 0;
        }

        _lastTvPollAt = now;
        _lastActivityAt = now;
        return reconnected;
    }
}
