namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>
/// The settings page's colour-tuning wizard: a fixed, logical order of seven
/// steps (White plus every RGB primary and secondary). Every step's edges are
/// sampled through the ordinary Ambilight pipeline, same as real video, from
/// a rendered flat-colour swatch at that step's own canonical hue (see
/// <see cref="SwatchColours"/>) -- not a photo, so nothing about a photo's
/// own white balance or exposure can bias the reading, White included (a
/// real "white" photo is rarely perfectly neutral; a synthetic
/// (255,255,255) swatch always is). Builds the six-anchor
/// <see cref="Core.Color.HueCorrectionCurve"/> plus White's own red/blue
/// gain.
/// </summary>
/// <remarks>
/// An earlier revision replayed each anchor a second time against the
/// operator's own real photos ("finetuning"), for real-content confirmation
/// after the synthetic tuning pass. Retired along with those photos: real
/// video played through ordinary Jellyfin playback, with the always-visible
/// Live tuning panel open alongside it, now covers that same real-content
/// check without a second, photo-specific wizard phase.
/// </remarks>
public static class CalibrationWizard
{
    public static readonly IReadOnlyList<string> TuningOrder =
        ["White", "Red", "Green", "Blue", "Yellow", "Cyan", "Magenta"];

    /// <summary>
    /// Canonical sRGB byte triple for each step's own hue at full saturation
    /// and value (red 0°, yellow 60°, green 120°, cyan 180°, blue 240°,
    /// magenta 300° -- the same six angles <c>HueCorrectionCurve</c> anchors
    /// on), rendered flat and sampled through the real edge-sampling pipeline
    /// exactly as a photo would be. White is included too, at (255,255,255):
    /// a perfectly neutral synthetic swatch means the White step starts from
    /// an exact (r=g=b) reading every time, matching the RGBW extraction's
    /// own identity case exactly at centre.
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

    public string ColourName => CalibrationWizard.TuningOrder[_stepIndex];

    public bool IsLastStep => _stepIndex == CalibrationWizard.TuningOrder.Count - 1;

    /// <summary>True while the TV page's own poll has landed within the last few seconds.</summary>
    public bool TvConnected => _clock() - _lastTvPollAt < TimeSpan.FromSeconds(5);

    /// <summary>Opens the TV-facing surface and restarts at White.</summary>
    public void Arm()
    {
        _isArmed = true;
        _stepIndex = 0;
        _lastTvPollAt = DateTimeOffset.MinValue;
        _lastActivityAt = _clock();
    }

    /// <summary>Closes the TV-facing surface again; the position is left as-is, harmlessly, until the next Arm.</summary>
    public void Disarm() => _isArmed = false;

    public void MoveTo(int stepIndex)
    {
        _stepIndex = Math.Clamp(stepIndex, 0, CalibrationWizard.TuningOrder.Count - 1);
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
        }

        _lastTvPollAt = now;
        _lastActivityAt = now;
        return reconnected;
    }
}
