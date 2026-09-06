namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;

/// <summary>
/// Number of logical samples in each clockwise perimeter run. These counts are
/// independent of the physical LED counts and remain fixed for a session.
/// </summary>
public sealed class LogicalSamplingLayout
{
    public const int DefaultLongestSideSampleCount = 100;

    public LogicalSamplingLayout(int topSampleCount, int rightSampleCount, int bottomSampleCount, int leftSampleCount)
    {
        TopSampleCount = ValidateSampleCount(topSampleCount, nameof(topSampleCount));
        RightSampleCount = ValidateSampleCount(rightSampleCount, nameof(rightSampleCount));
        BottomSampleCount = ValidateSampleCount(bottomSampleCount, nameof(bottomSampleCount));
        LeftSampleCount = ValidateSampleCount(leftSampleCount, nameof(leftSampleCount));
    }

    public int TopSampleCount { get; }

    public int RightSampleCount { get; }

    public int BottomSampleCount { get; }

    public int LeftSampleCount { get; }

    /// <summary>
    /// Derives the ADR-010 default from a same-pitch physical layout. A future
    /// layout editor may provide measured side lengths instead; it must still
    /// create an explicit logical layout before sampling starts.
    /// </summary>
    public static LogicalSamplingLayout FromPhysicalLayout(
        LedLayout physicalLayout,
        int longestSideSampleCount = DefaultLongestSideSampleCount)
    {
        ArgumentNullException.ThrowIfNull(physicalLayout);

        if (longestSideSampleCount < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(longestSideSampleCount),
                longestSideSampleCount,
                "The longest side must have at least two logical samples.");
        }

        var longestSideLedCount = physicalLayout.LongestSideLedCount;

        return new LogicalSamplingLayout(
            Scale(physicalLayout.TopLedCount, longestSideLedCount, longestSideSampleCount),
            Scale(physicalLayout.RightLedCount, longestSideLedCount, longestSideSampleCount),
            Scale(physicalLayout.BottomLedCount, longestSideLedCount, longestSideSampleCount),
            Scale(physicalLayout.LeftLedCount, longestSideLedCount, longestSideSampleCount));
    }

    private static int Scale(int sideLedCount, int longestSideLedCount, int longestSideSampleCount)
        => Math.Max(2, (int)Math.Round(
            (double)longestSideSampleCount * sideLedCount / longestSideLedCount,
            MidpointRounding.AwayFromZero));

    private static int ValidateSampleCount(int value, string parameterName)
    {
        if (value < 2)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Each perimeter side must contain at least two logical samples.");
        }

        return value;
    }
}
