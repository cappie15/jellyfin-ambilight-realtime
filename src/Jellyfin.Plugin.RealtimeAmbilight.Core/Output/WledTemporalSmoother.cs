using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Output;

/// <summary>
/// Eases each physical LED's colour toward its newly sampled target over
/// time, rather than jumping straight to it every frame.
/// </summary>
/// <remarks>
/// <para>
/// Researched against HyperHDR's own "Infinite Color Engine"
/// (<c>sources/infinite-color-engine/InfiniteSmoothing.cpp</c>, MIT,
/// <c>github.com/awawa-dev/HyperHDR</c>) before building this, rather than
/// guessing from the name: it is fundamentally an interpolator that eases
/// the *target* colour toward its new value over a configurable settling
/// time (its default is 200 ms at 25 Hz), decoupled from the incoming
/// analysis rate -- still driving perfectly ordinary 8-bit LED strips.
/// Reported "stapjes tussen de bron en doelkleur, vooral bij lage
/// brightness" is not fixed by higher bit depth: DDP (this project's own
/// realtime protocol, <see cref="Protocol.DdpPacketizer"/>) and WLED's own
/// realtime UDP inputs are 8 bits per channel with no higher-precision
/// realtime path at all, so sending more precision from this plugin has
/// nothing on the wire to carry it. <see cref="DitheredRgb24Encoder"/>'s
/// existing temporal dithering already fixes *static* banding (a slow fade
/// holding one wrong byte for many frames); this is a different problem --
/// a single sampled value visibly snapping frame to frame, most visible at
/// low brightness because differential brightness sensitivity is highest
/// near black. Smoothing the target this class eases toward, before it
/// ever reaches the ditherer, gives the ditherer a run of much smaller
/// per-frame deltas to represent, which is exactly what HyperHDR's own
/// engine does before any dithering/PWM step of its own.
/// </para>
/// <para>
/// Deliberately its own, simpler class rather than adapting
/// <see cref="Hue.HueNaturalLightFilter"/>: that filter's split
/// colour/brightness time constants, its independent brightness rate clamp
/// and its 1% floor all exist for a 16-bit networked light with its own
/// separate concerns (<c>HueNaturalLightFilter</c>'s own remarks explain
/// why colour and brightness need different time constants there). A
/// physical 8-bit LED strip has none of those needs -- a single per-channel
/// exponential low-pass is what HyperHDR's own simplest interpolator
/// ("Stepper"/basic exponential) already reduces to, and is enough here.
/// </para>
/// </remarks>
public sealed class WledTemporalSmoother
{
    private LinearRgb[]? _smoothed;

    /// <summary>
    /// Filters <paramref name="frame"/> in place. Disabled entirely (the
    /// default) when <paramref name="timeConstantMilliseconds"/> is zero or
    /// less, so an installation that has not opted in pays no cost and sees
    /// no behaviour change at all.
    /// </summary>
    /// <param name="elapsedMillisecondsSinceLastFrame">
    /// Time since this filter's previous call, on a monotonic clock. Zero,
    /// negative, or an implausibly large gap (the first frame, a pause, a
    /// decoder restart) snaps instead of easing in -- there is no meaningful
    /// previous state to ease from, and easing in across a multi-second gap
    /// would mean the LEDs visibly follow a scene that is no longer showing.
    /// </param>
    public void Apply(Span<LinearRgb> frame, int timeConstantMilliseconds, double elapsedMillisecondsSinceLastFrame)
    {
        if (timeConstantMilliseconds <= 0)
        {
            // Switching the setting off mid-session must not leave stale
            // smoothed state behind: forget it so the very next frame passes
            // through untouched, exactly as if this filter had never run.
            _smoothed = null;
            return;
        }

        if (_smoothed is null || _smoothed.Length != frame.Length)
        {
            _smoothed = frame.ToArray();
            return;
        }

        if (elapsedMillisecondsSinceLastFrame is <= 0 or > 1000)
        {
            for (var index = 0; index < frame.Length; index++)
            {
                _smoothed[index] = frame[index];
            }

            return;
        }

        var mix = 1f - MathF.Exp((float)(-elapsedMillisecondsSinceLastFrame / timeConstantMilliseconds));
        for (var index = 0; index < frame.Length; index++)
        {
            _smoothed[index] = LinearRgb.Lerp(_smoothed[index], frame[index], mix);
            frame[index] = _smoothed[index];
        }
    }
}
