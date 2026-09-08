using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Output;

/// <summary>
/// Holds each physical LED at its last committed colour until a genuinely new
/// colour has been proposed continuously for a minimum duration, so a single
/// bright frame -- a flash cut, a lens flare, one frame of a strobe -- cannot
/// snap the wall to a colour that was never really "in the picture" for long
/// enough to read as such.
/// </summary>
/// <remarks>
/// Deliberately per LED, not a single whole-frame timer: a scene change often
/// affects one edge before another (a character walks into frame from the
/// left), and gating the whole strip on the slowest edge to settle would make
/// genuine cuts feel sluggish everywhere just to protect against a flicker at
/// one edge. Time is supplied by the caller rather than read from the clock
/// here, so this stays a pure function of its inputs and needs no real delay
/// to unit test.
/// </remarks>
public sealed class DwellFilter
{
    /// <summary>
    /// Per-channel linear-light difference below which two colours count as
    /// "the same" for dwell purposes. Sampling noise between frames of an
    /// otherwise static scene is well under this; a real colour change is not.
    /// </summary>
    private const float ToleranceSquared = 0.02f * 0.02f;

    private LinearRgb[]? _committed;
    private LinearRgb[]? _candidate;
    private double[]? _candidateAgeMilliseconds;

    /// <summary>
    /// Filters <paramref name="frame"/> in place. Disabled entirely (the
    /// common case) when <paramref name="minimumHoldMilliseconds"/> is zero or
    /// less, so a default installation pays no cost for a feature it has not
    /// opted into.
    /// </summary>
    public void Apply(Span<LinearRgb> frame, int minimumHoldMilliseconds, double elapsedMillisecondsSinceLastFrame)
    {
        if (minimumHoldMilliseconds <= 0)
        {
            // Switching the setting off mid-session must not leave a stale
            // hold behind: forget everything so the very next frame passes
            // through untouched, exactly as if the filter had never run.
            _committed = null;
            _candidate = null;
            _candidateAgeMilliseconds = null;
            return;
        }

        if (_committed is null || _committed.Length != frame.Length)
        {
            _committed = frame.ToArray();
            _candidate = frame.ToArray();
            _candidateAgeMilliseconds = new double[frame.Length];
            return;
        }

        var elapsed = Math.Max(0, elapsedMillisecondsSinceLastFrame);
        for (var index = 0; index < frame.Length; index++)
        {
            if (IsClose(frame[index], _candidate![index]))
            {
                _candidateAgeMilliseconds![index] += elapsed;
                if (_candidateAgeMilliseconds[index] >= minimumHoldMilliseconds)
                {
                    _committed![index] = _candidate[index];
                }
            }
            else
            {
                _candidate![index] = frame[index];
                _candidateAgeMilliseconds![index] = 0;
            }

            frame[index] = _committed![index];
        }
    }

    private static bool IsClose(LinearRgb left, LinearRgb right)
    {
        var dr = left.Red - right.Red;
        var dg = left.Green - right.Green;
        var db = left.Blue - right.Blue;
        return ((dr * dr) + (dg * dg) + (db * db)) <= ToleranceSquared;
    }
}
