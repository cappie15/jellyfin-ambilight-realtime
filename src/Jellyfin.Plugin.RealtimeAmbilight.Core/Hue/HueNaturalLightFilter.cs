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
    /// How quickly the smoothed hue/chroma direction follows a new target.
    /// Slower than brightness on purpose: an abrupt colour swap (a hard cut
    /// between two very differently lit shots) is exactly what "abrupte
    /// kleurwisselingen" asks to be suppressed, while brightness is allowed
    /// to move faster so the light still reads as reactive.
    /// </summary>
    private const double ColourTimeConstantMilliseconds = 500d;

    private const double BrightnessTimeConstantMilliseconds = 180d;

    /// <summary>
    /// Independent hard bound on brightness in units per second (brightness
    /// is normalized 0-1), applied after smoothing. A full swing from 0 to 1
    /// therefore never takes less than 400 ms, regardless of how large the
    /// jump in the raw target was on a single frame -- this is what
    /// specifically suppresses a short, sharp peak (a strobe, a lens flare,
    /// one bright frame) that a low-pass alone would still let through as a
    /// brief but full-amplitude spike.
    /// </summary>
    private const double MaximumBrightnessChangePerSecond = 2.5d;

    /// <summary>
    /// Minimum background light while synchronisation is active, applied in
    /// the brightness calculation rather than by flooring R/G/B directly.
    /// </summary>
    public const double MinimumBrightnessFraction = 0.01d;

    /// <summary>
    /// Approximate warm-white (2700 K design target, per this project's
    /// documented end-of-session default) linear-light ratios, used as the
    /// very first frame's colour before any real target has ever been seen.
    /// </summary>
    private static readonly LinearRgb WarmWhiteFallback = new(1f, 0.7f, 0.45f);

    private readonly Dictionary<int, ChannelState> _channels = [];

    /// <param name="target">This frame's raw mapped colour for the channel.</param>
    /// <param name="elapsedMilliseconds">
    /// Time since this channel was last filtered, on a monotonic clock. Zero,
    /// negative, or an implausibly large gap (a fresh channel, a reconnect,
    /// a long pause) snaps instead of easing in -- there is no meaningful
    /// previous state to ease from.
    /// </param>
    /// <param name="overallBrightnessFraction">
    /// The operator's own overall-brightness control, 0-1. Applied before the
    /// floor, so the floor is what survives dimming, not what gets dimmed.
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
        }

        var colourMix = hasPreviousState ? Mix(elapsedMilliseconds, ColourTimeConstantMilliseconds) : 1d;
        state.SmoothedColour = hasPreviousState
            ? LinearRgb.Lerp(state.SmoothedColour, targetColour, (float)colourMix)
            : targetColour;

        var brightnessMix = hasPreviousState ? Mix(elapsedMilliseconds, BrightnessTimeConstantMilliseconds) : 1d;
        var lowPassBrightness = hasPreviousState
            ? state.SmoothedBrightness + ((targetBrightness - state.SmoothedBrightness) * brightnessMix)
            : targetBrightness;

        var maxStep = hasPreviousState ? MaximumBrightnessChangePerSecond * elapsedSeconds : double.MaxValue;
        var rateLimitedBrightness = Math.Clamp(
            lowPassBrightness,
            state.SmoothedBrightness - maxStep,
            state.SmoothedBrightness + maxStep);
        state.SmoothedBrightness = Math.Clamp(rateLimitedBrightness, 0d, 1d);
        state.Initialized = true;

        var dimmed = state.SmoothedBrightness * Math.Clamp(overallBrightnessFraction, 0d, 1d);
        var floored = MinimumBrightnessFraction + (dimmed * (1d - MinimumBrightnessFraction));

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
