using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;

/// <summary>
/// Implements the candidate ADR-010 geometry against packed BT.709 limited-range
/// BGRA frames. Each zone is a linearly weighted mean in linear light --
/// heaviest at the picture's true edge, lightest at the sampled band's inner
/// boundary -- and adjacent edge runs deliberately overlap at corners.
/// </summary>
public static class EdgeSampler
{
    /// <summary>
    /// The sampled band's innermost row/column counts this much against the
    /// full weight of 1.0 at the picture's true edge. Never zero: a shallow
    /// band (low sampling depth, or a small analysis frame) should still use
    /// every pixel it has, just not equally.
    /// </summary>
    private const double EdgeWeightFloor = 0.3;

    private enum EdgeSide
    {
        Top,
        Right,
        Bottom,
        Left,
    }

    /// <summary>Fraction of each axis sampled per side, as a percentage.</summary>
    public const int DefaultDepthPercent = 10;

    public const int MinimumDepthPercent = 1;

    /// <summary>
    /// Beyond roughly a third of an axis an edge no longer reflects the part of
    /// the picture nearest it, but an average of the whole scene, so every side
    /// converges on the same colour.
    /// </summary>
    public const int MaximumDepthPercent = 30;

    public static PerimeterSamples SampleBgra(
        ReadOnlySpan<byte> bgraFrame,
        int frameWidth,
        int frameHeight,
        CropInsets crop,
        LogicalSamplingLayout layout,
        int depthPercent = DefaultDepthPercent)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ValidateFrame(bgraFrame, frameWidth, frameHeight);
        var zones = CreateZones(frameWidth, frameHeight, crop, layout, depthPercent);
        return new PerimeterSamples(
            SampleRun(bgraFrame, frameWidth, zones.Top, EdgeSide.Top),
            SampleRun(bgraFrame, frameWidth, zones.Right, EdgeSide.Right),
            SampleRun(bgraFrame, frameWidth, zones.Bottom, EdgeSide.Bottom),
            SampleRun(bgraFrame, frameWidth, zones.Left, EdgeSide.Left));
    }

    /// <summary>
    /// Same geometry as <see cref="SampleBgra"/>, but for a full-range sRGB RGBA
    /// buffer -- what a browser's <c>canvas</c> hands back, not what a decoded
    /// video frame is. A calibration photo is never broadcast-encoded, so
    /// decoding it as BT.709 limited range would subtract a black level and
    /// stretch the range that is not actually there, and get every colour wrong.
    /// </summary>
    public static PerimeterSamples SampleSrgb(
        ReadOnlySpan<byte> rgbaFrame,
        int frameWidth,
        int frameHeight,
        CropInsets crop,
        LogicalSamplingLayout layout,
        int depthPercent = DefaultDepthPercent)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ValidateFrame(rgbaFrame, frameWidth, frameHeight);
        var zones = CreateZones(frameWidth, frameHeight, crop, layout, depthPercent);
        return new PerimeterSamples(
            SampleRunSrgb(rgbaFrame, frameWidth, zones.Top, EdgeSide.Top),
            SampleRunSrgb(rgbaFrame, frameWidth, zones.Right, EdgeSide.Right),
            SampleRunSrgb(rgbaFrame, frameWidth, zones.Bottom, EdgeSide.Bottom),
            SampleRunSrgb(rgbaFrame, frameWidth, zones.Left, EdgeSide.Left));
    }

    public static EdgeSamplingZones CreateZones(
        int frameWidth,
        int frameHeight,
        CropInsets crop,
        LogicalSamplingLayout layout,
        int depthPercent = DefaultDepthPercent)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfLessThan(depthPercent, MinimumDepthPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(depthPercent, MaximumDepthPercent);
        if (frameWidth < 16 || frameHeight < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(frameWidth), "Sampling frames must be at least 16 by 16 pixels.");
        }

        crop.Validate(frameWidth, frameHeight);
        var activeLeft = crop.Left;
        var activeTop = crop.Top;
        var activeRight = frameWidth - crop.Right;
        var activeBottom = frameHeight - crop.Bottom;
        var activeShortEdge = Math.Min(activeRight - activeLeft, activeBottom - activeTop);
        var guard = Math.Clamp((int)Math.Round(activeShortEdge * 0.005, MidpointRounding.AwayFromZero), 1, 4);

        // Sample the outer tenth of the picture. Depth is taken per axis, not off
        // the short edge: a band measured against the short edge is a different
        // fraction of the frame on the top and bottom than on the left and right,
        // so the sides would weigh differently on the same scene.
        var horizontalDepth = SamplingDepth(activeBottom - activeTop, depthPercent);
        var verticalDepth = SamplingDepth(activeRight - activeLeft, depthPercent);
        var pictureLeft = activeLeft + guard;
        var pictureTop = activeTop + guard;
        var pictureRight = activeRight - guard;
        var pictureBottom = activeBottom - guard;
        var pictureWidth = pictureRight - pictureLeft;
        var pictureHeight = pictureBottom - pictureTop;
        if (pictureWidth < 3 || pictureHeight < 3 || verticalDepth > pictureWidth || horizontalDepth > pictureHeight)
        {
            throw new ArgumentException("The active picture is too small for the ADR-010 guard band and sampling depth.", nameof(crop));
        }

        return new EdgeSamplingZones(
            BuildHorizontalRun(pictureLeft, pictureRight, pictureTop, pictureTop + horizontalDepth, layout.TopSampleCount, leftToRight: true),
            BuildVerticalRun(pictureRight - verticalDepth, pictureRight, pictureTop, pictureBottom, layout.RightSampleCount, topToBottom: true),
            BuildHorizontalRun(pictureLeft, pictureRight, pictureBottom - horizontalDepth, pictureBottom, layout.BottomSampleCount, leftToRight: false),
            BuildVerticalRun(pictureLeft, pictureLeft + verticalDepth, pictureTop, pictureBottom, layout.LeftSampleCount, topToBottom: false));
    }

    /// <summary>The configured outer fraction of one axis, floored so a tiny frame still works.</summary>
    private static int SamplingDepth(int extent, int depthPercent)
        => Math.Clamp((int)Math.Round(extent * (depthPercent / 100d), MidpointRounding.AwayFromZero), 2, 192);

    private static SamplingZone[] BuildHorizontalRun(int left, int right, int top, int bottom, int count, bool leftToRight)
    {
        var zones = new SamplingZone[count];
        for (var index = 0; index < zones.Length; index++)
        {
            var (start, end) = Segment(left, right, count, index, reverse: !leftToRight);
            zones[index] = new SamplingZone(start, top, end, bottom);
        }

        return zones;
    }

    private static SamplingZone[] BuildVerticalRun(int left, int right, int top, int bottom, int count, bool topToBottom)
    {
        var zones = new SamplingZone[count];
        for (var index = 0; index < zones.Length; index++)
        {
            var (start, end) = Segment(top, bottom, count, index, reverse: !topToBottom);
            zones[index] = new SamplingZone(left, start, right, end);
        }

        return zones;
    }

    private static (int Start, int End) Segment(int start, int end, int count, int index, bool reverse)
    {
        var extent = end - start;
        var segmentStart = reverse
            ? end - (int)((long)(index + 1) * extent / count)
            : start + (int)((long)index * extent / count);
        var segmentEnd = reverse
            ? end - (int)((long)index * extent / count)
            : start + (int)((long)(index + 1) * extent / count);
        if (segmentEnd - segmentStart >= 3)
        {
            return (segmentStart, segmentEnd);
        }

        // At very low analysis resolutions zones overlap by design instead of
        // becoming empty. This is the ADR-010 degenerate-case smoothing fallback.
        var centre = start + ((index + 0.5) * extent / count);
        var widenedStart = Math.Clamp((int)Math.Floor(centre - 1.5), start, end - 3);
        return (widenedStart, widenedStart + 3);
    }

    private static LinearRgb[] SampleRun(ReadOnlySpan<byte> bgraFrame, int frameWidth, SamplingZone[] zones, EdgeSide side)
    {
        var samples = new LinearRgb[zones.Length];
        for (var index = 0; index < zones.Length; index++)
        {
            var zone = zones[index];
            double red = 0;
            double green = 0;
            double blue = 0;
            double weightSum = 0;
            for (var y = zone.Top; y < zone.Bottom; y++)
            {
                for (var x = zone.Left; x < zone.Right; x++)
                {
                    var weight = EdgeWeight(zone, x, y, side);
                    var offset = checked(((y * frameWidth) + x) * 4);
                    blue += Bt709LimitedToLinear(bgraFrame[offset]) * weight;
                    green += Bt709LimitedToLinear(bgraFrame[offset + 1]) * weight;
                    red += Bt709LimitedToLinear(bgraFrame[offset + 2]) * weight;
                    weightSum += weight;
                }
            }

            samples[index] = new LinearRgb(
                (float)(red / weightSum),
                (float)(green / weightSum),
                (float)(blue / weightSum));
        }

        return samples;
    }

    /// <summary>
    /// A linear ramp from <see cref="EdgeWeightFloor"/> at the sampled band's
    /// inner boundary up to 1.0 at the picture's true outer edge -- the row or
    /// column nearest the physical LEDs, which is what a wall actually
    /// continues, counts most; the row or column deepest into the picture,
    /// least. A single-row/column zone (a very shallow band) has nowhere to
    /// ramp across and is weighted uniformly at 1.0.
    /// </summary>
    private static double EdgeWeight(SamplingZone zone, int x, int y, EdgeSide side)
    {
        (int Position, int Outer, int Inner) span = side switch
        {
            EdgeSide.Top => (y, zone.Top, zone.Bottom - 1),
            EdgeSide.Bottom => (y, zone.Bottom - 1, zone.Top),
            EdgeSide.Left => (x, zone.Left, zone.Right - 1),
            EdgeSide.Right => (x, zone.Right - 1, zone.Left),
            _ => throw new ArgumentOutOfRangeException(nameof(side)),
        };

        if (span.Inner == span.Outer)
        {
            return 1.0;
        }

        var fromOuter = Math.Abs(span.Position - span.Outer);
        var depth = Math.Abs(span.Inner - span.Outer);
        var towardInner = fromOuter / (double)depth;
        return 1.0 - (towardInner * (1.0 - EdgeWeightFloor));
    }

    /// <summary>
    /// Every one of the 256 possible <see cref="Bt709LimitedToLinear"/>
    /// results, precomputed once. The edge-sampling hot loop calls this once
    /// per channel per pixel in the sampled band -- for the default 10% depth
    /// at 160x90, on the order of several thousand calls every single
    /// analysed frame, 30 times a second -- and every input is already one of
    /// only 256 byte values, so recomputing the same <see cref="Math.Pow"/>
    /// result for the same byte, frame after frame, bought nothing a lookup
    /// does not already buy for free. Values are identical to calling the
    /// formula directly (this table *is* that formula, evaluated once) --
    /// the test project checks all 256 entries against it directly, not
    /// just spot values.
    /// </summary>
    private static readonly double[] Bt709LimitedToLinearTable = BuildTable(ComputeBt709LimitedToLinear);

    private static readonly double[] SrgbToLinearTable = BuildTable(ComputeSrgbToLinear);

    private static double[] BuildTable(Func<byte, double> compute)
    {
        var table = new double[256];
        for (var value = 0; value < 256; value++)
        {
            table[value] = compute((byte)value);
        }

        return table;
    }

    private static double Bt709LimitedToLinear(byte codeValue) => Bt709LimitedToLinearTable[codeValue];

    private static double ComputeBt709LimitedToLinear(byte codeValue)
    {
        var nonlinear = (codeValue - 16d) / 219d;
        return nonlinear < 0.081d
            ? nonlinear / 4.5d
            : Math.Pow((nonlinear + 0.099d) / 1.099d, 1d / 0.45d);
    }

    private static LinearRgb[] SampleRunSrgb(ReadOnlySpan<byte> rgbaFrame, int frameWidth, SamplingZone[] zones, EdgeSide side)
    {
        var samples = new LinearRgb[zones.Length];
        for (var index = 0; index < zones.Length; index++)
        {
            var zone = zones[index];
            double red = 0;
            double green = 0;
            double blue = 0;
            double weightSum = 0;
            for (var y = zone.Top; y < zone.Bottom; y++)
            {
                for (var x = zone.Left; x < zone.Right; x++)
                {
                    var weight = EdgeWeight(zone, x, y, side);
                    var offset = checked(((y * frameWidth) + x) * 4);
                    red += SrgbToLinear(rgbaFrame[offset]) * weight;
                    green += SrgbToLinear(rgbaFrame[offset + 1]) * weight;
                    blue += SrgbToLinear(rgbaFrame[offset + 2]) * weight;
                    weightSum += weight;
                }
            }

            samples[index] = new LinearRgb(
                (float)(red / weightSum),
                (float)(green / weightSum),
                (float)(blue / weightSum));
        }

        return samples;
    }

    private static double SrgbToLinear(byte codeValue) => SrgbToLinearTable[codeValue];

    private static double ComputeSrgbToLinear(byte codeValue)
    {
        var encoded = codeValue / 255d;
        return encoded <= 0.04045d
            ? encoded / 12.92d
            : Math.Pow((encoded + 0.055d) / 1.055d, 2.4d);
    }

    private static void ValidateFrame(ReadOnlySpan<byte> bgraFrame, int frameWidth, int frameHeight)
    {
        if (frameWidth < 16 || frameHeight < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(frameWidth), "Sampling frames must be at least 16 by 16 pixels.");
        }

        var expectedLength = checked(frameWidth * frameHeight * 4);
        if (bgraFrame.Length != expectedLength)
        {
            throw new ArgumentException($"Expected {expectedLength} BGRA bytes but received {bgraFrame.Length}.", nameof(bgraFrame));
        }
    }
}

/// <summary>Precomputed clockwise zones for one cropped frame geometry.</summary>
public sealed record EdgeSamplingZones(
    SamplingZone[] Top,
    SamplingZone[] Right,
    SamplingZone[] Bottom,
    SamplingZone[] Left);
