using Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Layout;

public class LedLayoutTests
{
    [Fact]
    public void ReferenceLayoutPreservesThePhysicalAsymmetryAndDerivesLogicalRuns()
    {
        var physicalLayout = new LedLayout(topLedCount: 265, rightLedCount: 150, bottomLedCount: 266, leftLedCount: 150);

        var logicalLayout = LogicalSamplingLayout.FromPhysicalLayout(physicalLayout);

        Assert.Equal(831, physicalLayout.TotalLedCount);
        Assert.Equal(266, physicalLayout.LongestSideLedCount);
        Assert.Equal(100, logicalLayout.TopSampleCount);
        Assert.Equal(56, logicalLayout.RightSampleCount);
        Assert.Equal(100, logicalLayout.BottomSampleCount);
        Assert.Equal(56, logicalLayout.LeftSampleCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PhysicalLayoutRejectsAnEmptySide(int invalidSideCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LedLayout(invalidSideCount, 1, 1, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void LogicalLayoutRejectsFewerThanTwoSamplesPerSide(int invalidSideCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogicalSamplingLayout(invalidSideCount, 2, 2, 2));
    }

    [Fact]
    public void LogicalLayoutRejectsAnInvalidLongestSideTarget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LogicalSamplingLayout.FromPhysicalLayout(new LedLayout(1, 1, 1, 1), 1));
    }
}
