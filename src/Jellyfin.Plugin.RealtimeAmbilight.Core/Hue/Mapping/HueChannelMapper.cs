using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Model;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Mapping;

/// <summary>
/// Maps one Hue entertainment channel's reported <see cref="HuePosition"/> to
/// a colour, from the same edge samples and scene average every channel of
/// one frame is derived from.
/// </summary>
/// <remarks>
/// <para>
/// Coordinate handling, per <see cref="HuePosition"/>'s own documented
/// (and explicitly unverified against the official reference) convention:
/// <c>x</c> is clamped to [-1, 1] (left/right); <c>z</c> is clamped to [0, 1]
/// (floor/ceiling) -- a light mounted above a notionally one-unit-tall room
/// still reads as "near the ceiling" rather than extrapolating past it;
/// <c>y</c> (depth) is clamped to [0, 1] and treated as the single axis this
/// mapping blends across: 0 is at/near the screen, 1 is behind the viewer,
/// with 0.5 -- beside the viewer -- as the mapping's explicit midpoint.
/// </para>
/// <para>
/// Three anchors, blended smoothly rather than switched between:
/// </para>
/// <list type="number">
/// <item><b>At the screen (depth 0).</b> Blends the picture's own top and
/// bottom edge samples by height (<c>z</c>), then, the closer <c>x</c> sits
/// to either side, blends further toward that side's full edge sample --
/// directly above the screen centre is pure top edge; directly beside the
/// screen is close to the corresponding side edge.</item>
/// <item><b>Beside the viewer (depth 0.5).</b> The corresponding left or
/// right image edge outright (chosen by the sign of <c>x</c>), positioned
/// along that edge by height.</item>
/// <item><b>Behind the viewer (depth 1).</b> The whole scene's average
/// colour, uniform regardless of <c>x</c>/<c>z</c>.</item>
/// </list>
/// <para>
/// This is a reproducible function of a channel's reported position, not a
/// measurement of the real room: Hue positions are a coarse spatial layout
/// the operator places lights into via the Hue app, not a ruler distance from
/// the screen. The viewing-position assumption this rests on -- that "beside
/// the viewer" is a meaningful, distinct depth from "at the screen" -- should
/// be sanity-checked against how the operator actually placed their lights.
/// </para>
/// </remarks>
public static class HueChannelMapper
{
    public static LinearRgb Map(HuePosition position, PerimeterSamples edgeSamples, LinearRgb sceneAverage)
    {
        ArgumentNullException.ThrowIfNull(edgeSamples);

        var x = Math.Clamp(position.X, -1d, 1d);
        var z = Math.Clamp(position.Z, 0d, 1d);
        var depth = Math.Clamp(position.Y, 0d, 1d);

        var screenColour = MapScreenRegion(x, z, edgeSamples);
        var sideColour = MapSideEdge(x, z, edgeSamples);

        return depth <= 0.5d
            ? LinearRgb.Lerp(screenColour, sideColour, (float)(depth / 0.5d))
            : LinearRgb.Lerp(sideColour, sceneAverage, (float)((depth - 0.5d) / 0.5d));
    }

    private static LinearRgb MapScreenRegion(double x, double z, PerimeterSamples edgeSamples)
    {
        var topAtX = SampleAlongRun(edgeSamples.Top, TopFraction(x));
        var bottomAtX = SampleAlongRun(edgeSamples.Bottom, BottomFraction(x));
        var verticalBlend = LinearRgb.Lerp(bottomAtX, topAtX, (float)z);

        var sideExtremity = Math.Clamp((Math.Abs(x) - 0.5d) / 0.5d, 0d, 1d);
        if (sideExtremity <= 0d)
        {
            return verticalBlend;
        }

        var side = MapSideEdge(x, z, edgeSamples);
        return LinearRgb.Lerp(verticalBlend, side, (float)sideExtremity);
    }

    private static LinearRgb MapSideEdge(double x, double z, PerimeterSamples edgeSamples)
        => x >= 0d
            ? SampleAlongRun(edgeSamples.Right, RightFraction(z))
            : SampleAlongRun(edgeSamples.Left, LeftFraction(z));

    // Fractions follow EdgeSampler's own clockwise sample order for each run:
    // Top is left-to-right, Right is top-to-bottom, Bottom is right-to-left
    // (built reversed), Left is bottom-to-top (built reversed).
    private static double TopFraction(double x) => Math.Clamp((x + 1d) / 2d, 0d, 1d);

    private static double BottomFraction(double x) => Math.Clamp((1d - x) / 2d, 0d, 1d);

    private static double RightFraction(double z) => Math.Clamp(1d - z, 0d, 1d);

    private static double LeftFraction(double z) => Math.Clamp(z, 0d, 1d);

    /// <summary>Continuous interpolation between a run's discrete samples.</summary>
    private static LinearRgb SampleAlongRun(LinearRgb[] run, double fraction)
    {
        if (run.Length == 1)
        {
            return run[0];
        }

        var position = fraction * (run.Length - 1);
        var lowerIndex = (int)Math.Floor(position);
        var upperIndex = Math.Min(lowerIndex + 1, run.Length - 1);
        var amount = position - lowerIndex;
        return LinearRgb.Lerp(run[lowerIndex], run[upperIndex], (float)amount);
    }
}
