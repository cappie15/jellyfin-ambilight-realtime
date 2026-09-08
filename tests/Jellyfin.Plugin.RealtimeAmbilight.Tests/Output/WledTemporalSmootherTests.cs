using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Output;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Output;

public sealed class WledTemporalSmootherTests
{
    [Fact]
    public void DisabledByDefaultPassesFramesThroughUnchanged()
    {
        var smoother = new WledTemporalSmoother();
        var frame = new[] { new LinearRgb(0f, 0f, 0f) };

        smoother.Apply(frame, timeConstantMilliseconds: 0, elapsedMillisecondsSinceLastFrame: 33);
        frame[0] = new LinearRgb(1f, 1f, 1f);
        smoother.Apply(frame, timeConstantMilliseconds: 0, elapsedMillisecondsSinceLastFrame: 33);

        Assert.Equal(1f, frame[0].Red);
    }

    [Fact]
    public void TheFirstFrameSnapsRatherThanEasingInFromZero()
    {
        var smoother = new WledTemporalSmoother();
        var frame = new[] { new LinearRgb(0.5f, 0.5f, 0.5f) };

        smoother.Apply(frame, timeConstantMilliseconds: 200, elapsedMillisecondsSinceLastFrame: 0);

        Assert.Equal(0.5f, frame[0].Red);
    }

    [Fact]
    public void ASustainedStepChangeEasesInGraduallyThenConverges()
    {
        var smoother = new WledTemporalSmoother();
        var frame = new[] { new LinearRgb(0f, 0f, 0f) };
        smoother.Apply(frame, timeConstantMilliseconds: 200, elapsedMillisecondsSinceLastFrame: 0);

        frame[0] = new LinearRgb(1f, 1f, 1f);
        smoother.Apply(frame, timeConstantMilliseconds: 200, elapsedMillisecondsSinceLastFrame: 33);
        var afterOneFrame = frame[0].Red;

        // One 33 ms frame at a 200 ms time constant cannot jump all the way
        // to the new target -- this is exactly the point: it gives the
        // ditherer a much smaller delta to represent than an instant jump
        // would, which is what actually removes the visible "step" between
        // source and target colour.
        Assert.True(afterOneFrame is > 0f and < 0.9f, $"expected a partial step, got {afterOneFrame}");

        for (var i = 0; i < 60; i++)
        {
            frame[0] = new LinearRgb(1f, 1f, 1f);
            smoother.Apply(frame, timeConstantMilliseconds: 200, elapsedMillisecondsSinceLastFrame: 33);
        }

        Assert.True(frame[0].Red > 0.99f, $"expected convergence close to 1, got {frame[0].Red}");
    }

    [Fact]
    public void ReducesTheFrameToFrameDeltaComparedToNoSmoothingUnderRealisticSamplingNoise()
    {
        // The actual claim: for a real, continuously-varying source (video),
        // smoothing genuinely reduces how much the value moves frame to
        // frame, unlike the earlier (reverted) attempt at smoothing Hue's
        // white channel, which only ever tried to fix a near-constant
        // target -- a fundamentally different case. Here the target itself
        // keeps moving, which is exactly what a real scene does.
        var random = new Random(7);
        var target = 0.1f;
        var targets = new List<float>();
        for (var i = 0; i < 60; i++)
        {
            target = Math.Clamp(target + ((float)(random.NextDouble() - 0.5) * 0.05f), 0f, 1f);
            targets.Add(target);
        }

        var smoother = new WledTemporalSmoother();
        var smoothedValues = new List<float>();
        var frame = new[] { new LinearRgb(targets[0], 0f, 0f) };
        smoother.Apply(frame, timeConstantMilliseconds: 150, elapsedMillisecondsSinceLastFrame: 0);
        smoothedValues.Add(frame[0].Red);
        for (var i = 1; i < targets.Count; i++)
        {
            frame[0] = new LinearRgb(targets[i], 0f, 0f);
            smoother.Apply(frame, timeConstantMilliseconds: 150, elapsedMillisecondsSinceLastFrame: 33);
            smoothedValues.Add(frame[0].Red);
        }

        var rawTotalDelta = SumAbsoluteDeltas(targets);
        var smoothedTotalDelta = SumAbsoluteDeltas(smoothedValues);

        Assert.True(smoothedTotalDelta < rawTotalDelta, $"expected smoothed total movement ({smoothedTotalDelta}) below raw ({rawTotalDelta})");
    }

    [Fact]
    public void AReconnectGapSnapsInsteadOfEasingInFromTheOldValue()
    {
        var smoother = new WledTemporalSmoother();
        var frame = new[] { new LinearRgb(0f, 0f, 0f) };
        smoother.Apply(frame, timeConstantMilliseconds: 200, elapsedMillisecondsSinceLastFrame: 0);

        frame[0] = new LinearRgb(1f, 1f, 1f);
        smoother.Apply(frame, timeConstantMilliseconds: 200, elapsedMillisecondsSinceLastFrame: 5000);

        Assert.Equal(1f, frame[0].Red);
    }

    private static float SumAbsoluteDeltas(List<float> values)
    {
        var total = 0f;
        for (var i = 1; i < values.Count; i++)
        {
            total += MathF.Abs(values[i] - values[i - 1]);
        }

        return total;
    }
}
