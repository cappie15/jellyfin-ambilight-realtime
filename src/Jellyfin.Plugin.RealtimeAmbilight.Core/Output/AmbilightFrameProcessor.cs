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
    private readonly Func<int> _samplingDepthPercentResolver;
    private readonly Func<Rgb24Encoding> _encodingResolver;
    private readonly Func<PerimeterColourAdjustment> _adjustmentResolver;
    private readonly Func<int> _minimumColourHoldMillisecondsResolver;
    private readonly Func<int> _smoothingMillisecondsResolver;
    private readonly Func<LedSideDirections> _sideDirectionsResolver;
    private readonly IDitheredChannelEncoder _encoder;
    private readonly DwellFilter _dwellFilter = new();
    private readonly WledTemporalSmoother _smoother = new();
    private readonly FrameRateMeter _processRate = new(new StopwatchMonotonicTime());
    private DateTimeOffset? _lastProcessedAt;
    private DateTimeOffset? _sessionStartedAt;

    /// <summary>
    /// How long a new session takes to ramp up from black to full brightness,
    /// mirroring <c>JellyfinWledOutputService.StopFadeMilliseconds</c>'s fade
    /// the other way at stop. Unlike that fade-out, this cannot simply
    /// interpolate toward one known destination frame: playback has only just
    /// started, so what the picture actually looks like is still arriving one
    /// real sample at a time. Instead every frame encoded during this window
    /// is scaled toward black by how much of it remains, converging on the
    /// real picture already fading in around it rather than after it.
    /// </summary>
    private const int FadeInMilliseconds = 2500;

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
        Func<int>? samplingDepthPercentResolver = null,
        Func<Rgb24Encoding>? encodingResolver = null,
        Func<PerimeterColourAdjustment>? adjustmentResolver = null,
        Func<int>? minimumColourHoldMillisecondsResolver = null,
        bool sendWhiteChannel = false,
        Func<int>? smoothingMillisecondsResolver = null,
        Func<LedSideDirections>? sideDirectionsResolver = null)
    {
        _physicalLayout = physicalLayout ?? throw new ArgumentNullException(nameof(physicalLayout));
        _logicalLayout = logicalLayout ?? throw new ArgumentNullException(nameof(logicalLayout));
        _cropResolver = cropResolver ?? throw new ArgumentNullException(nameof(cropResolver));
        _samplingDepthPercentResolver = samplingDepthPercentResolver ?? (static () => EdgeSampler.DefaultDepthPercent);
        _encodingResolver = encodingResolver ?? (static () => Rgb24Encoding.Bt709);
        _adjustmentResolver = adjustmentResolver ?? (static () => PerimeterColourAdjustment.None);
        _minimumColourHoldMillisecondsResolver = minimumColourHoldMillisecondsResolver ?? (static () => 0);
        _smoothingMillisecondsResolver = smoothingMillisecondsResolver ?? (static () => 0);
        _sideDirectionsResolver = sideDirectionsResolver ?? (static () => LedSideDirections.Clockwise);
        _encoder = sendWhiteChannel ? new DitheredRgbw32Encoder() : new DitheredRgb24Encoder();
    }

    /// <summary>
    /// The most recently <em>encoded</em> (dwell/smoothing already applied)
    /// target, in linear light before that -- so <see cref="ProcessRepeat"/>
    /// can keep easing/dithering toward it between real decoded frames. Set
    /// only by <see cref="EncodeTarget"/>, never by <see cref="Sample"/>:
    /// the analysis decoder runs several seconds ahead of the picture
    /// actually due to be shown (see <c>DecoderLead</c>), so a target that
    /// has only been sampled, not yet due, is not what should be repeated
    /// onto the strip right now -- repeating it caused exactly the flicker
    /// this split fixes, racing against the correctly-scheduled real frame
    /// once its own due time finally arrived.
    /// </summary>
    private LinearRgb[]? _lastTarget;

    /// <summary>
    /// Samples one decoded frame into a physical-LED target, in linear light,
    /// before any dwell/smoothing/encoding. Deliberately does not touch that
    /// state or <see cref="_lastTarget"/> -- the caller schedules the result
    /// against the playback timeline (the decoder samples ahead of the
    /// picture) and only <see cref="EncodeTarget"/>, called once it actually
    /// becomes due, may advance dwell/smoothing or become what
    /// <see cref="ProcessRepeat"/> repeats.
    /// </summary>
    public LinearRgb[] Sample(AnalysisFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _processRate.Increment();
        var samplingDepthPercent = Math.Clamp(
            _samplingDepthPercentResolver(),
            EdgeSampler.MinimumDepthPercent,
            EdgeSampler.MaximumDepthPercent);
        var samples = EdgeSampler.SampleBgra(
            frame.BgraPixels,
            frame.Width,
            frame.Height,
            _cropResolver(frame),
            _logicalLayout,
            samplingDepthPercent);
        return LinearLightInterpolator.InterpolatePerimeter(
            _physicalLayout,
            _logicalLayout,
            samples.Top,
            samples.Right,
            samples.Bottom,
            samples.Left,
            _sideDirectionsResolver());
    }

    /// <summary>
    /// Applies dwell/smoothing/encoding to <paramref name="target"/> and
    /// remembers it for <see cref="ProcessRepeat"/>. Call this only when a
    /// sampled target actually becomes due -- see <see cref="_lastTarget"/>'s
    /// own remarks for why sample time is the wrong moment.
    /// </summary>
    public byte[] EncodeTarget(LinearRgb[] target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _lastTarget = target;
        return Encode(target);
    }

    /// <summary>
    /// Re-applies dwell/smoothing/encoding to the last frame that actually
    /// became due, without a new one due yet. The source video's own frame
    /// rate caps how often a genuinely new picture can become due (a 24fps
    /// film cannot yield more than ~24 distinct frames a second, however high
    /// the output rate is set), but smoothing and the encoder's own temporal
    /// dithering both benefit from running at the full output tick rate
    /// regardless -- this is what lets the output side keep advancing toward
    /// the latest due target every tick instead of only when the source
    /// happens to deliver something new. Returns <see langword="null"/> before
    /// the first frame of a session has become due -- there is no target yet
    /// to repeat.
    /// </summary>
    public byte[]? ProcessRepeat() => _lastTarget is null ? null : Encode(_lastTarget);

    /// <summary>
    /// Forgets the last due target, so a stale colour from a previous session
    /// cannot be repeated onto the strip before this session's own first
    /// frame becomes due. Also marks the start of this session's own
    /// <see cref="FadeInMilliseconds"/> ramp-up.
    /// </summary>
    public void ClearTarget()
    {
        _lastTarget = null;
        _sessionStartedAt = DateTimeOffset.UtcNow;
    }

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

        if (_sessionStartedAt is { } startedAt)
        {
            var fadeInFraction = (now - startedAt).TotalMilliseconds / FadeInMilliseconds;
            if (fadeInFraction < 1)
            {
                var brightness = (float)Math.Clamp(fadeInFraction, 0, 1);
                for (var index = 0; index < physicalFrame.Length; index++)
                {
                    var colour = physicalFrame[index];
                    physicalFrame[index] = new LinearRgb(colour.Red * brightness, colour.Green * brightness, colour.Blue * brightness);
                }
            }
            else
            {
                _sessionStartedAt = null;
            }
        }

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
