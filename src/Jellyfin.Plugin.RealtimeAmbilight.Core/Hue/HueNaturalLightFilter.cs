using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Hue;

/// <summary>
/// Turns one channel's raw mapped colour into a natural-feeling light: vivid,
/// but with short brightness peaks and abrupt colour swaps smoothed away.
/// </summary>
/// <remarks>
/// <para>
/// Brightness (Rec. 709 luma of the target) and colour (the target
/// normalized by its own brightness, i.e. its hue/chroma direction with
/// brightness factored out) are smoothed independently, each as a first-order
/// low-pass driven by an explicit elapsed-time parameter on a monotonic
/// clock -- never the wall clock and never a frame count, so behaviour does
/// not depend on output rate. Brightness additionally has its per-second rate
/// of change hard-clamped after smoothing, as a second, independent bound: a
/// smoothing time constant alone still lets a large single-frame jump produce
/// a large single-frame step; the rate clamp guarantees a floor on how long
/// any change takes regardless of the jump's size.
/// </para>
/// <para>
/// This is deliberately not <c>DwellFilter</c> or
/// <c>DitheredRgb24Encoder</c> reused or adapted: those exist to fix
/// WLED-specific problems (a single-frame flash snapping a physical LED
/// strip; 8-bit quantization banding) that do not apply to a 16-bit Hue
/// light, and reusing them would carry WLED-specific behaviour into a
/// deliberately separate, general-purpose "natural light" profile.
/// </para>
/// <para>
/// The 1% floor lives here, in the brightness calculation itself, applied
/// after the operator's own overall-brightness scale rather than before --
/// so the floor survives dimming instead of being dimmed away with
/// everything else -- and never by raising individual R/G/B components,
/// which would tint a true black rather than dimly showing the last colour
/// that was actually on screen.
/// </para>
/// </remarks>
public sealed class HueNaturalLightFilter
{
    /// <summary>
    /// Endpoints of the operator-facing 0-100 "response" scale (0 = very
    /// reactive/intense, 100 = beautifully smooth): the fastest and slowest
    /// this filter will ever move, in either direction. Colour and
    /// brightness each keep their own time constant within this range --
    /// colour always stays the slower of the two (see
    /// <see cref="ColourTimeConstantMilliseconds"/>) -- and the brightness
    /// rate clamp scales the opposite way (a smoother response means a
    /// *lower* maximum rate of change).
    /// </summary>
    private const double MinColourTimeConstantMilliseconds = 80d;
    private const double MaxColourTimeConstantMilliseconds = 3000d;
    private const double MinBrightnessTimeConstantMilliseconds = 30d;
    private const double MaxBrightnessTimeConstantMilliseconds = 1200d;
    private const double MaxBrightnessChangePerSecond = 15d;
    private const double MinBrightnessChangePerSecond = 0.4d;

    /// <summary>
    /// How quickly the smoothed hue/chroma direction follows a new target.
    /// Slower than brightness on purpose: an abrupt colour swap (a hard cut
    /// between two very differently lit shots) is exactly what "abrupte
    /// kleurwisselingen" asks to be suppressed, while brightness is allowed
    /// to move faster so the light still reads as reactive.
    /// </summary>
    private readonly double _colourTimeConstantMilliseconds;

    private readonly double _brightnessTimeConstantMilliseconds;

    /// <summary>
    /// Independent hard bound on brightness in units per second (brightness
    /// is normalized 0-1), applied after smoothing. This is what specifically
    /// suppresses a short, sharp peak (a strobe, a lens flare, one bright
    /// frame) that a low-pass alone would still let through as a brief but
    /// full-amplitude spike.
    /// </summary>
    private readonly double _maximumBrightnessChangePerSecond;

    /// <summary>
    /// Minimum background light while synchronisation is active, applied in
    /// the brightness calculation rather than by flooring R/G/B directly.
    /// </summary>
    public const double MinimumBrightnessFraction = 0.01d;

    /// <summary>
    /// Approximate warm-white (2700 K design target, per this project's
    /// documented end-of-session default) linear-light ratios, seeded as a
    /// channel's starting hue direction before any real target has been
    /// seen. Since the 1% floor is withheld until a real colour has actually
    /// been shown (see the "HasSeenRealColour" gate below), this direction
    /// is only ever multiplied by a brightness of zero before that point --
    /// it exists so the eventual first real frame eases in from a sane
    /// starting hue rather than an arbitrary one, not so it is ever itself
    /// visibly rendered.
    /// </summary>
    private static readonly LinearRgb WarmWhiteFallback = new(1f, 0.7f, 0.45f);

    private readonly Dictionary<int, ChannelState> _channels = [];

    /// <param name="responsePercent">
    /// The operator's "Ambilight response" slider, 0 (very reactive/intense)
    /// to 100 (beautifully smooth), clamped. Fixed for this filter's
    /// lifetime, like <c>AmbilightFrameProcessor</c>'s own hardware-shaped
    /// constructor parameters -- it is read once when the Hue connection is
    /// (re-)established, not resolved live per frame, since a smoothing
    /// filter's own time constants are a property of the desired feel, not
    /// something that needs to react to itself changing mid-stream.
    /// </param>
    public HueNaturalLightFilter(int responsePercent = 50)
    {
        var t = Math.Clamp(responsePercent, 0, 100) / 100d;
        _colourTimeConstantMilliseconds = Lerp(MinColourTimeConstantMilliseconds, MaxColourTimeConstantMilliseconds, t);
        _brightnessTimeConstantMilliseconds = Lerp(MinBrightnessTimeConstantMilliseconds, MaxBrightnessTimeConstantMilliseconds, t);
        _maximumBrightnessChangePerSecond = Lerp(MaxBrightnessChangePerSecond, MinBrightnessChangePerSecond, t);
    }

    /// <param name="target">This frame's raw mapped colour for the channel.</param>
    /// <param name="elapsedMilliseconds">
    /// Time since this channel was last filtered, on a monotonic clock. Zero,
    /// negative, or an implausibly large gap (a fresh channel, a reconnect,
    /// a long pause) snaps instead of easing in -- there is no meaningful
    /// previous state to ease from.
    /// </param>
    /// <param name="overallBrightnessFraction">
    /// The operator's own overall-brightness control, 0-2 (1 = unchanged,
    /// above 1 boosts). Applied before the floor, so the floor is what
    /// survives dimming, not what gets dimmed. The boost half of this range
    /// exists for the same reason WLED's own Brightness control was widened
    /// past 100%: nothing upstream of this can push a pixel brighter than
    /// its own tonemapped source value, so content that never reaches peak
    /// brightness in the source reads as dim on the light regardless of the
    /// bulb's own real output -- confirmed as the same root cause on Hue as
    /// on WLED, reported live for both.
    /// </param>
    public LinearRgb Apply(int channelId, LinearRgb target, double elapsedMilliseconds, double overallBrightnessFraction = 1d)
    {
        if (!_channels.TryGetValue(channelId, out var state))
        {
            state = new ChannelState();
            _channels[channelId] = state;
        }

        var hasPreviousState = state.Initialized && elapsedMilliseconds is > 0 and < 2000;
        var elapsedSeconds = Math.Max(0d, elapsedMilliseconds) / 1000d;

        var targetBrightness = Luma(target);
        var targetColour = targetBrightness > 1e-6f
            ? new LinearRgb(target.Red / targetBrightness, target.Green / targetBrightness, target.Blue / targetBrightness)
            : state.LastValidHueColour;

        if (targetBrightness > 1e-6f)
        {
            state.LastValidHueColour = targetColour;
            state.HasSeenRealColour = true;
        }

        var colourMix = hasPreviousState ? Mix(elapsedMilliseconds, _colourTimeConstantMilliseconds) : 1d;
        state.SmoothedColour = hasPreviousState
            ? LinearRgb.Lerp(state.SmoothedColour, targetColour, (float)colourMix)
            : targetColour;

        var brightnessMix = hasPreviousState ? Mix(elapsedMilliseconds, _brightnessTimeConstantMilliseconds) : 1d;
        var lowPassBrightness = hasPreviousState
            ? state.SmoothedBrightness + ((targetBrightness - state.SmoothedBrightness) * brightnessMix)
            : targetBrightness;

        var maxStep = hasPreviousState ? _maximumBrightnessChangePerSecond * elapsedSeconds : double.MaxValue;
        var rateLimitedBrightness = Math.Clamp(
            lowPassBrightness,
            state.SmoothedBrightness - maxStep,
            state.SmoothedBrightness + maxStep);
        state.SmoothedBrightness = Math.Clamp(rateLimitedBrightness, 0d, 1d);
        state.Initialized = true;

        var dimmed = state.SmoothedBrightness * Math.Clamp(overallBrightnessFraction, 0d, 2d);
        // The 1% floor (and the warm-white fallback it would be tinted by,
        // via state.LastValidHueColour/state.SmoothedColour above) exists so
        // a black cut *mid-session* -- after real colour has actually been
        // seen -- reads as a dim, calm glow rather than flickering to an
        // undefined hue. Applying that same floor before any real colour has
        // ever been seen for this channel is a different situation: it means
        // a session has just (re)connected and genuinely has nothing to show
        // yet, and lighting up warm-white at that exact moment reads as a
        // visible flash rather than a floor -- reported live as "gaat nog
        // even naar geel" right after a film starts. Held at true off until
        // the first real (non-black) frame actually arrives instead.
        var floored = state.HasSeenRealColour
            ? MinimumBrightnessFraction + (dimmed * (1d - MinimumBrightnessFraction))
            : dimmed;

        return new LinearRgb(
            Math.Clamp(state.SmoothedColour.Red * (float)floored, 0f, 1f),
            Math.Clamp(state.SmoothedColour.Green * (float)floored, 0f, 1f),
            Math.Clamp(state.SmoothedColour.Blue * (float)floored, 0f, 1f));
    }

    /// <summary>Forgets a channel's smoothing state, e.g. once it leaves the paired configuration.</summary>
    public void Reset(int channelId) => _channels.Remove(channelId);

    /// <summary>Forgets every channel's state, e.g. across a reconnect after a long gap.</summary>
    public void ResetAll() => _channels.Clear();

    private static double Mix(double elapsedMilliseconds, double timeConstantMilliseconds)
        => 1d - Math.Exp(-elapsedMilliseconds / timeConstantMilliseconds);

    private static double Lerp(double from, double to, double t) => from + ((to - from) * t);

    private static float Luma(LinearRgb colour)
        => Math.Clamp((0.2126f * colour.Red) + (0.7152f * colour.Green) + (0.0722f * colour.Blue), 0f, 1f);

    private sealed class ChannelState
    {
        public LinearRgb SmoothedColour;
        public LinearRgb LastValidHueColour = WarmWhiteFallback;
        public double SmoothedBrightness;
        public bool Initialized;

        /// <summary>
        /// True once at least one genuinely non-black frame has been seen for
        /// this channel. Gates the 1% floor/warm-white fallback: they exist
        /// for a black cut *after* real colour has been shown, not for the
        /// moment a session first connects and has nothing to show yet.
        /// </summary>
        public bool HasSeenRealColour;
    }
}
