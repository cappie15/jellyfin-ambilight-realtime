namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;

/// <summary>
/// Physical LED counts in the clockwise perimeter order used by the plugin:
/// top, right, bottom, then left.
/// </summary>
public sealed class LedLayout
{
    public LedLayout(int topLedCount, int rightLedCount, int bottomLedCount, int leftLedCount)
    {
        TopLedCount = ValidateSideCount(topLedCount, nameof(topLedCount));
        RightLedCount = ValidateSideCount(rightLedCount, nameof(rightLedCount));
        BottomLedCount = ValidateSideCount(bottomLedCount, nameof(bottomLedCount));
        LeftLedCount = ValidateSideCount(leftLedCount, nameof(leftLedCount));
    }

    public int TopLedCount { get; }

    public int RightLedCount { get; }

    public int BottomLedCount { get; }

    public int LeftLedCount { get; }

    public int TotalLedCount => checked(TopLedCount + RightLedCount + BottomLedCount + LeftLedCount);

    public int LongestSideLedCount => Math.Max(Math.Max(TopLedCount, RightLedCount), Math.Max(BottomLedCount, LeftLedCount));

    private static int ValidateSideCount(int value, string parameterName)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Each perimeter side must contain at least one physical LED.");
        }

        return value;
    }
}
