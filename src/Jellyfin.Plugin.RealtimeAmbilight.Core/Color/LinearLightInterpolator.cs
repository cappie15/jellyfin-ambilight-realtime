using Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

/// <summary>
/// Resamples logical perimeter colours onto physical LEDs with endpoint-preserving
/// linear interpolation in linear light.
/// </summary>
public static class LinearLightInterpolator
{
    public static LinearRgb[] Resample(ReadOnlySpan<LinearRgb> logicalSamples, int physicalLedCount)
    {
        if (logicalSamples.IsEmpty)
        {
            throw new ArgumentException("At least one logical sample is required.", nameof(logicalSamples));
        }

        if (physicalLedCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalLedCount), physicalLedCount, "At least one physical LED is required.");
        }

        var physicalFrame = new LinearRgb[physicalLedCount];
        if (logicalSamples.Length == 1)
        {
            Array.Fill(physicalFrame, logicalSamples[0]);
            return physicalFrame;
        }

        if (physicalLedCount == 1)
        {
            physicalFrame[0] = logicalSamples[0];
            return physicalFrame;
        }

        var sourceStep = (logicalSamples.Length - 1f) / (physicalLedCount - 1f);
        for (var physicalIndex = 0; physicalIndex < physicalFrame.Length; physicalIndex++)
        {
            var sourcePosition = physicalIndex * sourceStep;
            var leftIndex = (int)sourcePosition;
            var rightIndex = Math.Min(leftIndex + 1, logicalSamples.Length - 1);
            physicalFrame[physicalIndex] = LinearRgb.Lerp(
                logicalSamples[leftIndex],
                logicalSamples[rightIndex],
                sourcePosition - leftIndex);
        }

        return physicalFrame;
    }

    /// <summary>
    /// Builds one clockwise RGB frame. Runs never interpolate across a corner:
    /// the sampler owns deliberate corner overlap in the source samples.
    /// </summary>
    /// <param name="directions">
    /// Per-side wiring correction for a physical run that was soldered against
    /// the clockwise order. Applied to each resampled run on its own, so a
    /// reversed side still draws from exactly the picture it sits beside; only
    /// the order the LEDs along it are addressed in changes.
    /// </param>
    public static LinearRgb[] InterpolatePerimeter(
        LedLayout physicalLayout,
        LogicalSamplingLayout logicalLayout,
        ReadOnlySpan<LinearRgb> topSamples,
        ReadOnlySpan<LinearRgb> rightSamples,
        ReadOnlySpan<LinearRgb> bottomSamples,
        ReadOnlySpan<LinearRgb> leftSamples,
        LedSideDirections directions = default)
    {
        ArgumentNullException.ThrowIfNull(physicalLayout);
        ArgumentNullException.ThrowIfNull(logicalLayout);
        ValidateCount(topSamples, logicalLayout.TopSampleCount, nameof(topSamples));
        ValidateCount(rightSamples, logicalLayout.RightSampleCount, nameof(rightSamples));
        ValidateCount(bottomSamples, logicalLayout.BottomSampleCount, nameof(bottomSamples));
        ValidateCount(leftSamples, logicalLayout.LeftSampleCount, nameof(leftSamples));

        var frame = new LinearRgb[physicalLayout.TotalLedCount];
        var destination = frame.AsSpan();
        var offset = 0;
        CopyResampled(topSamples, physicalLayout.TopLedCount, destination, ref offset, directions.ReverseTop);
        CopyResampled(rightSamples, physicalLayout.RightLedCount, destination, ref offset, directions.ReverseRight);
        CopyResampled(bottomSamples, physicalLayout.BottomLedCount, destination, ref offset, directions.ReverseBottom);
        CopyResampled(leftSamples, physicalLayout.LeftLedCount, destination, ref offset, directions.ReverseLeft);
        return frame;
    }

    private static void CopyResampled(
        ReadOnlySpan<LinearRgb> logicalSamples,
        int physicalLedCount,
        Span<LinearRgb> destination,
        ref int offset,
        bool reverse)
    {
        var run = Resample(logicalSamples, physicalLedCount);
        if (reverse)
        {
            Array.Reverse(run);
        }

        run.CopyTo(destination[offset..]);
        offset += physicalLedCount;
    }

    private static void ValidateCount(ReadOnlySpan<LinearRgb> samples, int expectedCount, string parameterName)
    {
        if (samples.Length != expectedCount)
        {
            throw new ArgumentException($"Expected {expectedCount} logical samples, but received {samples.Length}.", parameterName);
        }
    }
}
