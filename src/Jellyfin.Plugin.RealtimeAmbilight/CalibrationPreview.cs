namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>
/// The settings page's colour-tuning wizard: a fixed, logical order of steps
/// and the operator's own curated photography for each. Every step -- White
/// included -- is a real photo whose own sampled edges drive the LEDs through
/// the ordinary Ambilight pipeline; there is no synthetic reference colour.
/// </summary>
/// <remarks>
/// Two phases, one continuous sequence. The first seven ("tuning") cover
/// white plus every RGB primary and secondary in turn -- red, green, blue,
/// yellow, cyan, magenta -- one photo each except White, which offers three
/// to flip between via "try another photo". The remaining ten ("confirmation")
/// are varied, colour-rich real-world scenes with no single dominant hue,
/// meant to be walked through at the end so the operator can see the whole
/// result rather than trusting seven isolated steps to compose correctly;
/// the sliders stay live throughout both phases.
/// </remarks>
public static class CalibrationWizard
{
    public static readonly IReadOnlyList<string> TuningOrder =
        ["White", "Red", "Green", "Blue", "Yellow", "Cyan", "Magenta"];

    public static readonly IReadOnlyList<string> ConfirmationOrder =
    [
        "Blue-Green", "Cyan-Magenta", "Final test", "Orange-Red", "Purple",
        "Purple-Teal", "Blue-Red", "Blue-Yellow", "Pink-Grey", "Yellow-Pink",
    ];

    public static readonly IReadOnlyList<string> ColourOrder = [.. TuningOrder, .. ConfirmationOrder];

    /// <summary>Embedded JPEG file names (under <c>Configuration/CalibrationPhotos</c>) for each step.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Photos =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["White"] = ["s0_whitelevel1.jpg", "s0_whitelevel2.jpg", "s0_whitelevel3.jpg"],
            ["Red"] = ["s1_red.jpg"],
            ["Green"] = ["s1_green.jpg"],
            ["Blue"] = ["s1_blue.jpg"],
            ["Yellow"] = ["s2_yellow.jpg"],
            ["Cyan"] = ["s2_cyan.jpg"],
            ["Magenta"] = ["s2_magenta.jpg"],
            ["Blue-Green"] = ["s3_blue_green.jpg"],
            ["Cyan-Magenta"] = ["s3_cyan_magenta.jpg"],
            ["Final test"] = ["s3_finaltest.jpg"],
            ["Orange-Red"] = ["s3_orange_red.jpg"],
            ["Purple"] = ["s3_purple.jpg"],
            ["Purple-Teal"] = ["s3_purple_teal.jpg"],
            ["Blue-Red"] = ["s4_blue_red.jpg"],
            ["Blue-Yellow"] = ["s4_blue_yellow.jpg"],
            ["Pink-Grey"] = ["s4_pink_grey.jpg"],
            ["Yellow-Pink"] = ["s4_yellow_pink.jpg"],
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

    public int StepIndex => _stepIndex;

    public int PhotoIndex => _photoIndex;

    public string ColourName => CalibrationWizard.ColourOrder[_stepIndex];

    /// <summary>True while the TV page's own poll has landed within the last few seconds.</summary>
    public bool TvConnected => DateTimeOffset.UtcNow - _lastTvPollAt < TimeSpan.FromSeconds(5);

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
