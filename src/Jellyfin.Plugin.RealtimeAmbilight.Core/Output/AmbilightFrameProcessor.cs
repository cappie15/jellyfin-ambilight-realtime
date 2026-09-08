using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;
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
    private readonly IDitheredChannelEncoder _encoder;
    private readonly DwellFilter _dwellFilter = new();
    private DateTimeOffset? _lastProcessedAt;

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
        bool sendWhiteChannel = false)
    {
        _physicalLayout = physicalLayout ?? throw new ArgumentNullException(nameof(physicalLayout));
        _logicalLayout = logicalLayout ?? throw new ArgumentNullException(nameof(logicalLayout));
        _cropResolver = cropResolver ?? throw new ArgumentNullException(nameof(cropResolver));
        _samplingDepthPercent = Math.Clamp(samplingDepthPercent, EdgeSampler.MinimumDepthPercent, EdgeSampler.MaximumDepthPercent);
        _encodingResolver = encodingResolver ?? (static () => Rgb24Encoding.Bt709);
        _adjustmentResolver = adjustmentResolver ?? (static () => PerimeterColourAdjustment.None);
        _minimumColourHoldMillisecondsResolver = minimumColourHoldMillisecondsResolver ?? (static () => 0);
        _encoder = sendWhiteChannel ? new DitheredRgbw32Encoder() : new DitheredRgb24Encoder();
    }

    public byte[] Process(AnalysisFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var samples = EdgeSampler.SampleBgra(
            frame.BgraPixels,
            frame.Width,
            frame.Height,
            _cropResolver(frame),
            _logicalLayout,
            _samplingDepthPercent);
        var physicalFrame = LinearLightInterpolator.InterpolatePerimeter(
            _physicalLayout,
            _logicalLayout,
            samples.Top,
            samples.Right,
            samples.Bottom,
            samples.Left);
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

        return _encoder.Encode(physicalFrame, _encodingResolver(), adjustment.WhiteExtractionFactor);
    }
}
