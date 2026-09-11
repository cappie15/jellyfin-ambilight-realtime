using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Output;

/// <summary>
/// Converts one decoded analysis frame into the physical RGB24 perimeter. Crop
/// selection is injected so the future border detector can vary it per frame.
/// </summary>
public sealed class AmbilightFrameProcessor
{
    private readonly LedLayout _physicalLayout;
    private readonly LogicalSamplingLayout _logicalLayout;
    private readonly Func<AnalysisFrame, CropInsets> _cropResolver;
    private readonly int _samplingDepthPercent;
    private readonly Func<Rgb24Encoding> _encodingResolver;
    private readonly Func<PerimeterColourAdjustment> _adjustmentResolver;
    private readonly Func<int> _minimumColourHoldMillisecondsResolver;
    private readonly Func<int> _smoothingMillisecondsResolver;
    private readonly IDitheredChannelEncoder _encoder;
    private readonly DwellFilter _dwellFilter = new();
    private readonly WledTemporalSmoother _smoother = new();
    private readonly FrameRateMeter _processRate = new(new StopwatchMonotonicTime());
    private DateTimeOffset? _lastProcessedAt;

    /// <summary>
    /// How many frames per second are actually being sampled and colour-processed
    /// here, over the most recently completed one-second window -- dashboard-facing.
    /// See <see cref="FrameRateMeter"/> for cost and staleness caveats.
    /// </summary>
    public double ProcessRateHz => _processRate.RateHz;

    /// <param name="sendWhiteChannel">
    /// Encodes RGBW32 (one extra byte per LED, the strip's own white die)
    /// instead of RGB24. Fixed for the processor's lifetime, like the physical
    /// layout: it depends on the hardware attached, not on anything a slider
    /// changes live.
    /// </param>
    public AmbilightFrameProcessor(
        LedLayout physicalLayout,
        LogicalSamplingLayout logicalLayout,
        Func<AnalysisFrame, CropInsets> cropResolver,
        int samplingDepthPercent = EdgeSampler.DefaultDepthPercent,
        Func<Rgb24Encoding>? encodingResolver = null,
        Func<PerimeterColourAdjustment>? adjustmentResolver = null,
        Func<int>? minimumColourHoldMillisecondsResolver = null,
        bool sendWhiteChannel = false,
        Func<int>? smoothingMillisecondsResolver = null)
    {
        _physicalLayout = physicalLayout ?? throw new ArgumentNullException(nameof(physicalLayout));
        _logicalLayout = logicalLayout ?? throw new ArgumentNullException(nameof(logicalLayout));
        _cropResolver = cropResolver ?? throw new ArgumentNullException(nameof(cropResolver));
        _samplingDepthPercent = Math.Clamp(samplingDepthPercent, EdgeSampler.MinimumDepthPercent, EdgeSampler.MaximumDepthPercent);
        _encodingResolver = encodingResolver ?? (static () => Rgb24Encoding.Bt709);
        _adjustmentResolver = adjustmentResolver ?? (static () => PerimeterColourAdjustment.None);
        _minimumColourHoldMillisecondsResolver = minimumColourHoldMillisecondsResolver ?? (static () => 0);
        _smoothingMillisecondsResolver = smoothingMillisecondsResolver ?? (static () => 0);
        _encoder = sendWhiteChannel ? new DitheredRgbw32Encoder() : new DitheredRgb24Encoder();
    }

    /// <summary>
    /// The most recently sampled target -- pre-dwell, pre-smoothing, in linear
    /// light -- so <see cref="ProcessRepeat"/> can keep easing/dithering toward
    /// it between real decoded frames. Never mutated in place: dwell/smoothing
    /// mutate their own copy each call, so this always stays the true latest
    /// sample regardless of how many repeats run against it.
    /// </summary>
    private LinearRgb[]? _lastTarget;

    public byte[] Process(AnalysisFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _processRate.Increment();
        var samples = EdgeSampler.SampleBgra(
            frame.BgraPixels,
            frame.Width,
            frame.Height,
            _cropResolver(frame),
            _logicalLayout,
            _samplingDepthPercent);
        _lastTarget = LinearLightInterpolator.InterpolatePerimeter(
            _physicalLayout,
            _logicalLayout,
            samples.Top,
            samples.Right,
            samples.Bottom,
            samples.Left);
        return Encode(_lastTarget);
    }

    /// <summary>
    /// Re-applies dwell/smoothing/encoding to the last sampled target, without
    /// a new decoded frame to sample. The source video's own frame rate caps
    /// how often a genuinely new picture can be sampled (a 24fps film cannot
    /// yield more than ~24 distinct frames a second, however high the output
    /// rate is set), but smoothing and the encoder's own temporal dithering
    /// both benefit from running at the full output tick rate regardless --
    /// this is what lets the output side keep advancing toward the latest
    /// target every tick instead of only when the source happens to deliver
    /// something new. Returns <see langword="null"/> before the first frame has
    /// ever been sampled -- there is no target yet to repeat.
    /// </summary>
    public byte[]? ProcessRepeat() => _lastTarget is null ? null : Encode(_lastTarget);

    /// <summary>
    /// Forgets the last sampled target, so a stale colour from a previous
    /// session cannot be repeated onto the strip before this session's own
    /// first frame arrives.
    /// </summary>
    public void ClearTarget() => _lastTarget = null;

    private byte[] Encode(LinearRgb[] target)
    {
        // dwell/smoothing mutate in place -- a private copy, never the stored
        // target itself, so ProcessRepeat's next call still eases from the
        // true latest sample rather than from whatever the previous repeat
        // already blended it toward.
        var physicalFrame = (LinearRgb[])target.Clone();
        var now = DateTimeOffset.UtcNow;
        var elapsedMilliseconds = _lastProcessedAt is { } last ? (now - last).TotalMilliseconds : 0;
        _lastProcessedAt = now;
        _dwellFilter.Apply(physicalFrame, _minimumColourHoldMillisecondsResolver(), elapsedMilliseconds);
        var adjustment = _adjustmentResolver();
        if (!adjustment.IsIdentity)
        {
            for (var index = 0; index < physicalFrame.Length; index++)
            {
                physicalFrame[index] = adjustment.Apply(physicalFrame[index], index, _physicalLayout);
            }
        }

        _smoother.Apply(physicalFrame, _smoothingMillisecondsResolver(), elapsedMilliseconds);

        return _encoder.Encode(physicalFrame, _encodingResolver(), adjustment.WhiteExtractionFactor, adjustment.WhiteChannelCeiling);
    }
}
