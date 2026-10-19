using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Mapping;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Model;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Hue;

/// <summary>
/// Converts one decoded analysis frame into a colour per Hue channel. Entirely
/// independent of <c>AmbilightFrameProcessor</c>: Hue's colour comes from the
/// same decoded frame WLED's output does, but is sampled, mapped and smoothed
/// on its own path, upstream of every WLED-specific step (gamma encoding,
/// dithering, physical LED interpolation, wall-colour correction, per-side
/// RGB trims) so none of those installation-specific corrections leak into
/// Hue's own light.
/// </summary>
/// <remarks>
/// Runs its own <see cref="BlackBorderDetector"/> rather than sharing WLED's:
/// duplicating a small, cheap, stateful per-frame scan was judged the safer
/// choice this round over refactoring <c>AmbilightFrameProcessor</c> to
/// expose or share its internal detector, which risks the one thing this
/// integration must not do -- disturb already-working WLED behaviour.
/// </remarks>
public sealed class HueFrameProcessor
{
    /// <summary>
    /// A fixed, modest logical perimeter resolution for Hue's own edge
    /// sampling, independent of any WLED physical LED count: a handful of
    /// Hue channels never needs anywhere near WLED's per-LED sample density,
    /// and this stays correct even when no WLED strip is configured at all.
    /// </summary>
    private static readonly LogicalSamplingLayout DefaultSamplingLayout = new(12, 8, 12, 8);

    /// <summary>
    /// How long a freshly (re)connected session eases up from true black,
    /// matching <c>WledRealtimeOutput.ClaimWithFadeToBlackAsync</c>'s own
    /// 2000 ms -- a fixed duration independent of the operator's own
    /// responsivity slider, since at its most reactive setting (0, this
    /// operator's own default) the ordinary smoothing time constant is only
    /// ~30 ms, far too fast to read as an intentional fade-in on its own.
    /// Without this, the very first frame of a session simply snapped
    /// straight to the source's own brightness the instant real content
    /// arrived.
    /// </summary>
    private const double FadeInMilliseconds = 2000d;

    private readonly BlackBorderDetector _borderDetector = new();
    private readonly Func<bool> _ignoreBlackBordersResolver;
    private readonly LogicalSamplingLayout _samplingLayout;
    private readonly int _samplingDepthPercent;
    private readonly IMonotonicTime _monotonicTime;
    private readonly HueNaturalLightFilter _filter;
    private readonly Func<HueScreenExtent> _screenExtentResolver;

    /// <summary>
    /// Channel colours to send instead of anything derived from the picture,
    /// for finding out which part of which physical strip a channel actually
    /// drives. Null for normal operation.
    /// </summary>
    /// <remarks>
    /// Where a Hue channel physically lands cannot be computed from the
    /// bridge's own data when a gradient strip has been cut shorter than the
    /// length its firmware still spreads its segments over, which is the case
    /// on the reference installation. The only way to know is to light one
    /// channel at a time and look. This has now been the missing instrument
    /// in three separate rounds of chasing Hue position bugs.
    /// </remarks>
    private volatile IReadOnlyDictionary<int, LinearRgb>? _channelOverride;

    /// <summary>Sets or clears <see cref="_channelOverride"/>. Thread-safe; the pump reads it once per frame.</summary>
    public void SetChannelOverride(IReadOnlyDictionary<int, LinearRgb>? colours) => _channelOverride = colours;

    /// <summary>Whether an override is currently replacing the picture.</summary>
    public bool HasChannelOverride => _channelOverride is not null;
    private TimeSpan? _lastProcessedAt;
    private TimeSpan? _sessionStartedAt;

    public HueFrameProcessor(
        IMonotonicTime monotonicTime,
        Func<bool>? ignoreBlackBordersResolver = null,
        int samplingDepthPercent = EdgeSampler.DefaultDepthPercent,
        LogicalSamplingLayout? samplingLayout = null,
        Func<int>? responsePercentResolver = null,
        Func<int>? floorPercentResolver = null,
        Func<HueScreenExtent>? screenExtentResolver = null)
    {
        _monotonicTime = monotonicTime ?? throw new ArgumentNullException(nameof(monotonicTime));
        _ignoreBlackBordersResolver = ignoreBlackBordersResolver ?? (static () => true);
        _samplingDepthPercent = Math.Clamp(samplingDepthPercent, EdgeSampler.MinimumDepthPercent, EdgeSampler.MaximumDepthPercent);
        _samplingLayout = samplingLayout ?? DefaultSamplingLayout;
        _filter = new HueNaturalLightFilter(responsePercentResolver, floorPercentResolver);
        _screenExtentResolver = screenExtentResolver ?? (static () => HueScreenExtent.Full);
    }

    /// <param name="overallBrightnessFraction">The operator's overall Hue brightness control, 0-1.</param>
    /// <returns>Each channel's final colour, ready for <c>HueStreamPacketizer</c>.</returns>
    public IReadOnlyDictionary<int, LinearRgb> Process(
        AnalysisFrame frame,
        IReadOnlyList<HueEntertainmentChannel> channels,
        double overallBrightnessFraction = 1d)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(channels);

        var result = new Dictionary<int, LinearRgb>(channels.Count);
        if (channels.Count == 0)
        {
            return result;
        }

        if (_channelOverride is { } forced)
        {
            // Deliberately skips the natural-light filter and the session
            // fade: an identify colour that eases in over two seconds, or
            // drifts because the filter is smoothing it, is useless for
            // reading a boundary off a wall.
            var scale = (float)Math.Clamp(overallBrightnessFraction, 0d, 4d);
            foreach (var channel in channels)
            {
                var colour = forced.TryGetValue(channel.ChannelId, out var wanted) ? wanted : default;
                result[channel.ChannelId] = new LinearRgb(colour.Red * scale, colour.Green * scale, colour.Blue * scale);
            }

            return result;
        }

        var crop = _ignoreBlackBordersResolver() ? _borderDetector.Detect(frame) : default;
        var edgeSamples = EdgeSampler.SampleBgra(frame.BgraPixels, frame.Width, frame.Height, crop, _samplingLayout, _samplingDepthPercent);
        var sceneAverage = SceneAverageSampler.SampleBgra(frame.BgraPixels, frame.Width, frame.Height, crop);
        var screen = _screenExtentResolver();

        var now = _monotonicTime.Elapsed;
        var elapsedMilliseconds = _lastProcessedAt is { } last ? (now - last).TotalMilliseconds : 0;
        _lastProcessedAt = now;
        _sessionStartedAt ??= now;
        var fadeIn = Math.Clamp((now - _sessionStartedAt.Value).TotalMilliseconds / FadeInMilliseconds, 0d, 1d);

        foreach (var channel in channels)
        {
            var mapped = HueChannelMapper.Map(channel.Position, edgeSamples, sceneAverage, screen);
            var filtered = _filter.Apply(channel.ChannelId, mapped, elapsedMilliseconds, overallBrightnessFraction);
            result[channel.ChannelId] = fadeIn >= 1d
                ? filtered
                : new LinearRgb(filtered.Red * (float)fadeIn, filtered.Green * (float)fadeIn, filtered.Blue * (float)fadeIn);
        }

        return result;
    }

    /// <summary>Forgets smoothing state, frame timing, and the fade-in start time, e.g. after a reconnect.</summary>
    public void Reset()
    {
        _filter.ResetAll();
        _lastProcessedAt = null;
        _sessionStartedAt = null;
    }
}
