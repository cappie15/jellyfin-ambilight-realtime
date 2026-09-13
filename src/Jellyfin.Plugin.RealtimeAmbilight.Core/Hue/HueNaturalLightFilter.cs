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
    /// The operator's "Ambilight response" slider, re-read on every
    /// <see cref="Apply"/> call rather than fixed at construction time -- an
    /// operator dragging this while watching the light expects it to move
    /// the light, not to wait for the next reconnect. Colour and brightness
    /// each derive their own time constant from it within the ranges above
    /// (colour always the slower of the two, see
    /// <see cref="MinColourTimeConstantMilliseconds"/>), and the brightness
    /// rate clamp scales the opposite way (a smoother response means a
    /// *lower* maximum rate of change) -- cheap enough (a couple of
    /// interpolations) that recomputing it every frame costs nothing
    /// measurable, unlike recreating this whole filter would, which would
    /// also reset its per-channel smoothing state.
    /// </summary>
    private readonly Func<int> _responsePercentResolver;

    /// <summary>
    /// Approximate warm-white (2700 K design target, per this project's
    /// documented end-of-session default) linear-light ratios, seeded as a
    /// channel's starting hue direction before any real target has been
    /// seen. The floor (see <see cref="_floorPercentResolver"/>) is a
    /// threshold like WLED's own, not an always-on minimum, so this
    /// direction is only ever multiplied by a genuinely non-zero brightness
    /// once real content has actually pushed one through -- it exists so
    /// the eventual first real frame eases in from a sane starting hue
    /// rather than an arbitrary one, not so it is ever itself visibly
    /// rendered on its own.
    /// </summary>
    private static readonly LinearRgb WarmWhiteFallback = new(1f, 0.7f, 0.45f);

    /// <summary>
    /// The operator's Hue black-level floor, re-read on every
    /// <see cref="Apply"/> call for the same reason
    /// <see cref="_responsePercentResolver"/> is. Same meaning and same
    /// perceptual-percentage convention as WLED's own black level floor
    /// (<see cref="Color.PerimeterColourAdjustment"/>): below this fraction
    /// of perceptual brightness, the light goes to true black instead of
    /// holding a dim, always-on glow -- replacing an earlier fixed 1%
    /// minimum that could never actually reach black at all, which is
    /// exactly why Hue was reported as "never going completely dark".
    /// </summary>
    private readonly Func<int> _floorPercentResolver;

    private readonly Dictionary<int, ChannelState> _channels = [];

    /// <param name="responsePercent">
    /// The operator's "Ambilight response" slider, 0 (very reactive/intense)
    /// to 100 (beautifully smooth). Re-read on every <see cref="Apply"/>
    /// call -- see <see cref="_responsePercentResolver"/>'s own remarks.
    /// </param>
    public HueNaturalLightFilter(Func<int>? responsePercentResolver = null, Func<int>? floorPercentResolver = null)
    {
        _responsePercentResolver = responsePercentResolver ?? (static () => 50);
        _floorPercentResolver = floorPercentResolver ?? (static () => 1);
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
        var t = Math.Clamp(_responsePercentResolver(), 0, 100) / 100d;
        var colourTimeConstantMilliseconds = Lerp(MinColourTimeConstantMilliseconds, MaxColourTimeConstantMilliseconds, t);
        var brightnessTimeConstantMilliseconds = Lerp(MinBrightnessTimeConstantMilliseconds, MaxBrightnessTimeConstantMilliseconds, t);
        var maximumBrightnessChangePerSecond = Lerp(MaxBrightnessChangePerSecond, MinBrightnessChangePerSecond, t);

        var targetBrightness = Luma(target);
        var targetColour = targetBrightness > 1e-6f
            ? new LinearRgb(target.Red / targetBrightness, target.Green / targetBrightness, target.Blue / targetBrightness)
            : state.LastValidHueColour;

        if (targetBrightness > 1e-6f)
        {
            state.LastValidHueColour = targetColour;
        }

        var colourMix = hasPreviousState ? Mix(elapsedMilliseconds, colourTimeConstantMilliseconds) : 1d;
        state.SmoothedColour = hasPreviousState
            ? LinearRgb.Lerp(state.SmoothedColour, targetColour, (float)colourMix)
            : targetColour;

        var brightnessMix = hasPreviousState ? Mix(elapsedMilliseconds, brightnessTimeConstantMilliseconds) : 1d;
        var lowPassBrightness = hasPreviousState
            ? state.SmoothedBrightness + ((targetBrightness - state.SmoothedBrightness) * brightnessMix)
            : targetBrightness;

        var maxStep = hasPreviousState ? maximumBrightnessChangePerSecond * elapsedSeconds : double.MaxValue;
        var rateLimitedBrightness = Math.Clamp(
            lowPassBrightness,
            state.SmoothedBrightness - maxStep,
            state.SmoothedBrightness + maxStep);
        state.SmoothedBrightness = Math.Clamp(rateLimitedBrightness, 0d, 1d);
        state.Initialized = true;

        var dimmed = state.SmoothedBrightness * Math.Clamp(overallBrightnessFraction, 0d, 2d);
        // A genuine threshold, like WLED's own black-level floor, not an
        // always-on minimum: below it the light goes to true black, above
        // it the remaining headroom rescales smoothly back up. Naturally
        // stays at true black before any real colour has ever been seen for
        // this channel too (dimmed is already 0 then), so there is no
        // separate "held off until first frame" special case needed the way
        // an always-on floor would have required -- that also fixed a
        // visible warm-white flash right at connect an earlier revision had
        // (reported live as "gaat nog even naar geel" right after a film
        // starts), since a threshold floor never adds anything to a
        // genuinely-zero brightness.
        var linearFloor = Color.PerceptualBrightness.DecodePerceptualFraction(Math.Clamp(_floorPercentResolver(), 0, 20) / 100f);
        var floored = dimmed <= linearFloor ? 0d : (dimmed - linearFloor) / (1d - linearFloor);

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
    }
}
