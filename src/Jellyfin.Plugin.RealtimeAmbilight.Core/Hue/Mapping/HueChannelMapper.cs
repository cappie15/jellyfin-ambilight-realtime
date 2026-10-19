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
/// convention, confirmed against the operator's real bridge data
/// (2026-09-15 for <c>y</c>, 2026-10-19 for <c>z</c>): every axis is
/// clamped to [-1, 1]. <c>x</c> is left/right; <c>z</c> is height, -1 at
/// the floor and +1 at the ceiling; <c>y</c> (depth) is treated
/// as the single axis this mapping blends across: <b>+1 is at/near the
/// screen, -1 is behind the viewer</b>, with 0 -- beside the viewer -- as
/// the mapping's explicit midpoint. (This is the mirror image of an earlier,
/// unverified assumption that <c>y</c> ran 0..1 with 0 at the screen; see
/// the 2026-09-15 finding below.)
/// </para>
/// <para>
/// Three anchors, blended smoothly rather than switched between:
/// </para>
/// <list type="number">
/// <item><b>At the screen (y = +1).</b> Blends the picture's own top and
/// bottom edge samples by height (<c>z</c>, floor to ceiling), then, the closer <c>x</c> sits
/// to either side, blends further toward that side's full edge sample --
/// directly above the screen centre is pure top edge; directly beside the
/// screen is close to the corresponding side edge.</item>
/// <item><b>Beside the viewer (y = 0).</b> The corresponding left or
/// right image edge outright (chosen by the sign of <c>x</c>), positioned
/// along that edge by height.</item>
/// <item><b>Behind the viewer (y = -1).</b> The whole scene's average
/// colour, uniform regardless of <c>x</c>/<c>z</c>.</item>
/// </list>
/// <para>
/// <b>2026-09-15 finding, confirmed against four independent real fixtures
/// on the operator's own bridge, not assumed:</b> two gradient lights were
/// both repositioned to sit physically against the TV wall and both then
/// reported <c>y = 1.0</c> -- if <c>y = 0</c> meant "at the screen" as
/// originally assumed, moving a light onto the screen wall should have
/// pushed its <c>y</c> toward 0, not to the opposite extreme. Two more
/// fixtures, described by the operator as "halfway from the TV" and "just
/// rear of the viewing position", reported <c>y</c> of roughly -0.25 and
/// -0.62 respectively -- consistent with a midpoint at 0 and increasingly
/// negative behind it, not with the 0..1/0-at-screen range. All four only
/// make sense on the corrected -1..+1/+1-at-screen scale now documented
/// above; the mapping formula was updated to match, not just this comment.
/// </para>
/// <para>
/// <b>2026-10-19: the same was true of <c>z</c>, which that note had
/// explicitly excused.</b> Both of the operator's entertainment areas report
/// negative <c>z</c> for half their channels (-0.4 for the gradient strips,
/// -0.235 and -0.645 for two lamps), impossible on the 0..1 range this
/// mapping assumed. Confirmed by what it produced in the room: with a test
/// pattern split into coloured quadrants, every light pinned to <c>z = 0</c>
/// showed the picture's bottom edge, so the boundary between the top and
/// bottom colours sat near the ceiling instead of halfway up each strip.
/// <c>z</c> is now read on the same -1..+1 scale as <c>x</c>.
/// </para>
/// </remarks>
public static class HueChannelMapper
{
    /// <param name="screen">
    /// How much of the room the television actually occupies. A light at the
    /// edge of the screen should read the edge of the picture; without this
    /// it reads only as far into the picture as it sits into the room. See
    /// <see cref="HueScreenExtent"/> for what that cost in a real room.
    /// </param>
    public static LinearRgb Map(HuePosition position, PerimeterSamples edgeSamples, LinearRgb sceneAverage, HueScreenExtent screen = default)
    {
        ArgumentNullException.ThrowIfNull(edgeSamples);

        var x = Math.Clamp(position.X / screen.EffectiveHalfWidth, -1d, 1d);
        // Z runs -1 (floor) to +1 (ceiling), the same -1..+1 scale as X, and
        // is converted here to this method's own internal 0 (bottom) to 1
        // (top) scale. It used to be clamped to [0, 1] on the strength of an
        // assumption nobody had checked against a real bridge -- the identical
        // mistake made about Y, found and fixed separately on 2026-09-15 while
        // the note below it still insisted "X and Z's own documented ranges
        // were never wrong". They were. The operator's own two entertainment
        // areas report Z of -0.4, -0.235, -0.645 and more, which simply cannot
        // occur on a 0..1 scale, and the old clamp pinned every one of them to
        // 0: the picture's bottom edge. Half this installation's ten channels
        // landed there, and every remaining channel sat far lower than its
        // real height, so the bottom edge's colour covered almost the whole
        // room and only a light at the literal ceiling ever showed the top of
        // the picture.
        var z = (Math.Clamp(position.Z / screen.EffectiveHalfHeight, -1d, 1d) + 1d) / 2d;
        // Y runs -1 (behind the viewer) to +1 (at the screen) -- confirmed
        // against the operator's own real bridge data (2026-09-15), not
        // assumed: two gradient lights both moved to sit physically against
        // the TV wall reported Y=1.0 afterward, and two single-position
        // lights described as "halfway from the TV" and "just rear of the
        // viewing position" reported Y of roughly -0.25 and -0.62
        // respectively -- all four only make sense on a -1..+1 scale with
        // +1 at the screen, the mirror image of the 0..1/0-at-screen range
        // this method used to assume (and X and Z's own documented -1..1/
        // 0..1 ranges were never wrong; only Y was). Converted here to this
        // method's own internal 0 (screen) to 1 (behind the viewer) scale,
        // which every threshold below still expects unchanged.
        var depth = (1d - Math.Clamp(position.Y, -1d, 1d)) / 2d;

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
