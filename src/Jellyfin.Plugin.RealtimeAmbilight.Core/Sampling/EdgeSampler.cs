using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;

/// <summary>
/// Implements the candidate ADR-010 geometry against packed BT.709 limited-range
/// BGRA frames. Each zone uses an unweighted mean in linear light and adjacent
/// edge runs deliberately overlap at corners.
/// </summary>
public static class EdgeSampler
{
    public static PerimeterSamples SampleBgra(
        ReadOnlySpan<byte> bgraFrame,
        int frameWidth,
        int frameHeight,
        CropInsets crop,
        LogicalSamplingLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ValidateFrame(bgraFrame, frameWidth, frameHeight);
        var zones = CreateZones(frameWidth, frameHeight, crop, layout);
        return new PerimeterSamples(
            SampleRun(bgraFrame, frameWidth, zones.Top),
            SampleRun(bgraFrame, frameWidth, zones.Right),
            SampleRun(bgraFrame, frameWidth, zones.Bottom),
            SampleRun(bgraFrame, frameWidth, zones.Left));
    }

    public static EdgeSamplingZones CreateZones(
        int frameWidth,
        int frameHeight,
        CropInsets crop,
        LogicalSamplingLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
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
        var depth = Math.Clamp((int)Math.Round(activeShortEdge * 0.05, MidpointRounding.AwayFromZero), 8, 64);
        var pictureLeft = activeLeft + guard;
        var pictureTop = activeTop + guard;
        var pictureRight = activeRight - guard;
        var pictureBottom = activeBottom - guard;
        var pictureWidth = pictureRight - pictureLeft;
        var pictureHeight = pictureBottom - pictureTop;
        if (pictureWidth < 3 || pictureHeight < 3 || depth > pictureWidth || depth > pictureHeight)
        {
            throw new ArgumentException("The active picture is too small for the ADR-010 guard band and sampling depth.", nameof(crop));
        }

        return new EdgeSamplingZones(
            BuildHorizontalRun(pictureLeft, pictureRight, pictureTop, pictureTop + depth, layout.TopSampleCount, leftToRight: true),
            BuildVerticalRun(pictureRight - depth, pictureRight, pictureTop, pictureBottom, layout.RightSampleCount, topToBottom: true),
            BuildHorizontalRun(pictureLeft, pictureRight, pictureBottom - depth, pictureBottom, layout.BottomSampleCount, leftToRight: false),
            BuildVerticalRun(pictureLeft, pictureLeft + depth, pictureTop, pictureBottom, layout.LeftSampleCount, topToBottom: false));
    }

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

    private static LinearRgb[] SampleRun(ReadOnlySpan<byte> bgraFrame, int frameWidth, SamplingZone[] zones)
    {
        var samples = new LinearRgb[zones.Length];
        for (var index = 0; index < zones.Length; index++)
        {
            var zone = zones[index];
            double red = 0;
            double green = 0;
            double blue = 0;
            for (var y = zone.Top; y < zone.Bottom; y++)
            {
                for (var x = zone.Left; x < zone.Right; x++)
                {
                    var offset = checked(((y * frameWidth) + x) * 4);
                    blue += Bt709LimitedToLinear(bgraFrame[offset]);
                    green += Bt709LimitedToLinear(bgraFrame[offset + 1]);
                    red += Bt709LimitedToLinear(bgraFrame[offset + 2]);
                }
            }

            var pixelCount = zone.Width * zone.Height;
            samples[index] = new LinearRgb(
                (float)(red / pixelCount),
                (float)(green / pixelCount),
                (float)(blue / pixelCount));
        }

        return samples;
    }

    private static double Bt709LimitedToLinear(byte codeValue)
    {
        var nonlinear = (codeValue - 16d) / 219d;
        return nonlinear < 0.081d
            ? nonlinear / 4.5d
            : Math.Pow((nonlinear + 0.099d) / 1.099d, 1d / 0.45d);
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
