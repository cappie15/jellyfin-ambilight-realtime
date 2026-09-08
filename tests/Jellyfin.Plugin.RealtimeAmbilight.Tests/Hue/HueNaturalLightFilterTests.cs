using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Hue;

public class HueNaturalLightFilterTests
{
    [Fact]
    public void TheFirstFrameSnapsRatherThanEasingInFromZero()
    {
        var filter = new HueNaturalLightFilter();

        var result = filter.Apply(0, new LinearRgb(1f, 1f, 1f), elapsedMilliseconds: 0);

        // Full brightness target, first ever frame: should read as bright,
        // not as if easing up from black over the following frames.
        Assert.True(result.Red > 0.9f);
    }

    [Fact]
    public void FullyBlackNeverGoesBelowTheOnePercentFloorOnceRealColourHasBeenSeen()
    {
        var filter = new HueNaturalLightFilter();
        // Establish real colour first: the floor exists for a black cut
        // mid-session, not for a channel that has never shown anything yet.
        filter.Apply(0, new LinearRgb(1f, 1f, 1f), elapsedMilliseconds: 0);

        var result = filter.Apply(0, new LinearRgb(0f, 0f, 0f), elapsedMilliseconds: 33);

        var brightness = MaxComponent(result);
        Assert.True(brightness >= (float)HueNaturalLightFilter.MinimumBrightnessFraction - 1e-4f);
        Assert.True(brightness > 0f);
    }

    [Fact]
    public void FullyBlackKeepsTheLastValidHueInsteadOfGoingToBlackOrAnUndefinedColour()
    {
        var filter = new HueNaturalLightFilter();
        // Settle on a strongly red target first.
        for (var i = 0; i < 60; i++)
        {
            filter.Apply(0, new LinearRgb(1f, 0f, 0f), elapsedMilliseconds: 33);
        }

        var onBlack = filter.Apply(0, new LinearRgb(0f, 0f, 0f), elapsedMilliseconds: 33);

        // Still reads as red-tinted (red the largest component), not white,
        // not black, not some other hue.
        Assert.True(onBlack.Red > onBlack.Green);
        Assert.True(onBlack.Red > onBlack.Blue);
    }

    [Fact]
    public void ANeverBeforeSeenBlackFrameHoldsTrueBlackRatherThanTheWarmWhiteFallback()
    {
        // Regression test for a real, live-reported bug: a session's very
        // first frames are often black (a title card, a logo, letterboxing
        // before content starts), and lighting up warm-white right then --
        // before any real colour has ever actually been shown -- reads as a
        // visible flash immediately after playback starts ("gaat nog even
        // naar geel"), not as a considerate floor. Held at true off instead
        // until the first real (non-black) frame actually arrives.
        var filter = new HueNaturalLightFilter();

        var result = filter.Apply(0, new LinearRgb(0f, 0f, 0f), elapsedMilliseconds: 0);

        Assert.Equal(0f, result.Red);
        Assert.Equal(0f, result.Green);
        Assert.Equal(0f, result.Blue);
    }


    [Fact]
    public void ABriefSinglePeakIsSuppressedMoreThanASustainedChange()
    {
        var peak = new HueNaturalLightFilter();
        var sustained = new HueNaturalLightFilter();

        // Settle both at black.
        for (var i = 0; i < 30; i++)
        {
            peak.Apply(0, new LinearRgb(0f, 0f, 0f), elapsedMilliseconds: 33);
            sustained.Apply(0, new LinearRgb(0f, 0f, 0f), elapsedMilliseconds: 33);
        }

        // One single bright frame, then back to black.
        var peakDuringFlash = peak.Apply(0, new LinearRgb(1f, 1f, 1f), elapsedMilliseconds: 33);
        peak.Apply(0, new LinearRgb(0f, 0f, 0f), elapsedMilliseconds: 33);

        // A sustained bright scene held across many frames.
        LinearRgb sustainedDuring = default;
        for (var i = 0; i < 30; i++)
        {
            sustainedDuring = sustained.Apply(0, new LinearRgb(1f, 1f, 1f), elapsedMilliseconds: 33);
        }

        Assert.True(MaxComponent(peakDuringFlash) < MaxComponent(sustainedDuring));
    }

    [Fact]
    public void BrightnessChangeIsRateLimitedRegardlessOfHowLargeTheJumpIs()
    {
        var filter = new HueNaturalLightFilter();
        filter.Apply(0, new LinearRgb(0f, 0f, 0f), elapsedMilliseconds: 0);

        // One 33 ms frame cannot jump all the way from black to full white:
        // the rate clamp bounds it independently of the smoothing time constant.
        var afterOneFrame = filter.Apply(0, new LinearRgb(1f, 1f, 1f), elapsedMilliseconds: 33);

        Assert.True(MaxComponent(afterOneFrame) < 0.5f);
    }

    [Fact]
    public void BehaviourDependsOnElapsedTimeNotOnCallCount()
    {
        var manySmallSteps = new HueNaturalLightFilter();
        manySmallSteps.Apply(0, new LinearRgb(0f, 0f, 0f), elapsedMilliseconds: 0);
        LinearRgb manyStepsResult = default;
        for (var i = 0; i < 10; i++)
        {
            // 10 calls of 100 ms each: 1000 ms total elapsed.
            manyStepsResult = manySmallSteps.Apply(0, new LinearRgb(1f, 1f, 1f), elapsedMilliseconds: 100);
        }

        var oneBigStep = new HueNaturalLightFilter();
        oneBigStep.Apply(0, new LinearRgb(0f, 0f, 0f), elapsedMilliseconds: 0);
        // One call covering the same 1000 ms total elapsed toward the same target.
        var afterOneCallCoveringTheSameTime = oneBigStep.Apply(0, new LinearRgb(1f, 1f, 1f), elapsedMilliseconds: 1000);

        // Both cover the same total elapsed time toward the same target; the
        // outcome should be close regardless of how many calls it took to get there.
        Assert.Equal(afterOneCallCoveringTheSameTime.Red, manyStepsResult.Red, precision: 1);
    }

    [Fact]
    public void OverallBrightnessDimmingStillLeavesTheFloorIntact()
    {
        var filter = new HueNaturalLightFilter();

        var result = filter.Apply(0, new LinearRgb(1f, 1f, 1f), elapsedMilliseconds: 0, overallBrightnessFraction: 0d);

        Assert.True(MaxComponent(result) >= (float)HueNaturalLightFilter.MinimumBrightnessFraction - 1e-4f);
    }

    [Fact]
    public void AReconnectGapSnapsInsteadOfEasingInFromTheOldValue()
    {
        var filter = new HueNaturalLightFilter();
        for (var i = 0; i < 30; i++)
        {
            filter.Apply(0, new LinearRgb(1f, 0f, 0f), elapsedMilliseconds: 33);
        }

        // A multi-second gap -- a reconnect -- then a very different target.
        var afterGap = filter.Apply(0, new LinearRgb(0f, 0f, 1f), elapsedMilliseconds: 5000);

        Assert.True(afterGap.Blue > afterGap.Red);
    }

    [Fact]
    public void ChannelsAreSmoothedIndependently()
    {
        var filter = new HueNaturalLightFilter();
        for (var i = 0; i < 30; i++)
        {
            filter.Apply(1, new LinearRgb(1f, 0f, 0f), elapsedMilliseconds: 33);
        }

        // A brand-new channel id must not inherit channel 1's smoothed state.
        var freshChannel = filter.Apply(2, new LinearRgb(0f, 0f, 1f), elapsedMilliseconds: 33);

        Assert.True(freshChannel.Blue > freshChannel.Red);
    }

    [Fact]
    public void AMoreReactiveResponseFollowsANewTargetFasterThanASmootherOne()
    {
        var reactive = new HueNaturalLightFilter(responsePercent: 0);
        var smooth = new HueNaturalLightFilter(responsePercent: 100);
        reactive.Apply(0, new LinearRgb(0f, 0f, 0f), elapsedMilliseconds: 0);
        smooth.Apply(0, new LinearRgb(0f, 0f, 0f), elapsedMilliseconds: 0);

        var reactiveAfterOneFrame = reactive.Apply(0, new LinearRgb(1f, 1f, 1f), elapsedMilliseconds: 33);
        var smoothAfterOneFrame = smooth.Apply(0, new LinearRgb(1f, 1f, 1f), elapsedMilliseconds: 33);

        Assert.True(MaxComponent(reactiveAfterOneFrame) > MaxComponent(smoothAfterOneFrame));
    }

    [Fact]
    public void ResponsePercentIsClampedToItsZeroToOneHundredRange()
    {
        // Out-of-range input (a stored value from before validation, or a
        // bad manual edit of the configuration file) must not crash or
        // silently extrapolate past the intended extremes.
        var belowRange = new HueNaturalLightFilter(responsePercent: -50);
        var aboveRange = new HueNaturalLightFilter(responsePercent: 500);

        var belowResult = belowRange.Apply(0, new LinearRgb(1f, 1f, 1f), elapsedMilliseconds: 0);
        var aboveResult = aboveRange.Apply(0, new LinearRgb(1f, 1f, 1f), elapsedMilliseconds: 0);

        Assert.True(MaxComponent(belowResult) > 0.9f);
        Assert.True(MaxComponent(aboveResult) > 0.9f);
    }

    private static float MaxComponent(LinearRgb colour) => Math.Max(colour.Red, Math.Max(colour.Green, colour.Blue));
}
