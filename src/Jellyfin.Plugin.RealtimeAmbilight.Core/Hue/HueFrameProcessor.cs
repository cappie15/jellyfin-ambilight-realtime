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

    private readonly BlackBorderDetector _borderDetector = new();
    private readonly Func<bool> _ignoreBlackBordersResolver;
    private readonly LogicalSamplingLayout _samplingLayout;
    private readonly int _samplingDepthPercent;
    private readonly IMonotonicTime _monotonicTime;
    private readonly HueNaturalLightFilter _filter = new();
    private TimeSpan? _lastProcessedAt;

    public HueFrameProcessor(
        IMonotonicTime monotonicTime,
        Func<bool>? ignoreBlackBordersResolver = null,
        int samplingDepthPercent = EdgeSampler.DefaultDepthPercent,
        LogicalSamplingLayout? samplingLayout = null)
    {
        _monotonicTime = monotonicTime ?? throw new ArgumentNullException(nameof(monotonicTime));
        _ignoreBlackBordersResolver = ignoreBlackBordersResolver ?? (static () => true);
        _samplingDepthPercent = Math.Clamp(samplingDepthPercent, EdgeSampler.MinimumDepthPercent, EdgeSampler.MaximumDepthPercent);
        _samplingLayout = samplingLayout ?? DefaultSamplingLayout;
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

        var crop = _ignoreBlackBordersResolver() ? _borderDetector.Detect(frame) : default;
        var edgeSamples = EdgeSampler.SampleBgra(frame.BgraPixels, frame.Width, frame.Height, crop, _samplingLayout, _samplingDepthPercent);
        var sceneAverage = SceneAverageSampler.SampleBgra(frame.BgraPixels, frame.Width, frame.Height, crop);

        var now = _monotonicTime.Elapsed;
        var elapsedMilliseconds = _lastProcessedAt is { } last ? (now - last).TotalMilliseconds : 0;
        _lastProcessedAt = now;

        foreach (var channel in channels)
        {
            var mapped = HueChannelMapper.Map(channel.Position, edgeSamples, sceneAverage);
            result[channel.ChannelId] = _filter.Apply(channel.ChannelId, mapped, elapsedMilliseconds, overallBrightnessFraction);
        }

        return result;
    }

    /// <summary>Forgets smoothing state and frame timing, e.g. after a reconnect.</summary>
    public void Reset()
    {
        _filter.ResetAll();
        _lastProcessedAt = null;
    }
}
