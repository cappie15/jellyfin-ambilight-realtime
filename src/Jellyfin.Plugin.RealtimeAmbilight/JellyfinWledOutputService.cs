#pragma warning disable CA1848, CA1873
using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Output;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Wled;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>Runs the latest-only frame pump for the reference 831-led WLED.</summary>
public sealed class JellyfinWledOutputService : IHostedService, IAsyncDisposable
{
    private readonly PlaybackEventCoordinator _coordinator;
    private readonly WledDiscoveryService _discoveryService;
    private readonly WledRealtimeOutput _output;
    private readonly LatestFrameOutputScheduler _scheduler;
    private readonly ILogger<JellyfinWledOutputService> _logger;
    /// <summary>
    /// How often the held frame is resent while playback is paused. WLED drops
    /// out of realtime once no data arrives for its configured realtime timeout,
    /// measured at 2.46 s for DDP and 2.25 s for Raw RGB on the reference
    /// controller, so this must stay comfortably below the shorter of the two.
    /// </summary>
    private static readonly TimeSpan PauseKeepAliveInterval = TimeSpan.FromSeconds(1);

    /// <summary>How far behind the picture output must fall before it is reported.</summary>
    private static readonly TimeSpan LateFrameReportThreshold = TimeSpan.FromMilliseconds(500);

    /// <summary>Rate limit for that report, so a persistent lag cannot flood the log.</summary>
    private static readonly TimeSpan LateFrameReportInterval = TimeSpan.FromSeconds(30);

    /// <summary>How far behind output must fall before the decoder is restarted.</summary>
    private static readonly TimeSpan LateFrameResyncThreshold = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Minimum spacing between those restarts. A restart costs a visible gap, so
    /// this must be long enough that a host which cannot quite keep up drifts
    /// slightly rather than flashing: at five seconds the restarts themselves
    /// became the fault being reported.
    /// </summary>
    private static readonly TimeSpan ResyncCooldown = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the last frame is held on the LEDs while no new frame is due.
    /// A decoder restart leaves a gap of several seconds, which is longer than
    /// WLED's realtime timeout, so without this WLED reclaims the strip and the
    /// LEDs visibly drop out and back for every correction.
    /// </summary>
    /// <remarks>
    /// This is a bridge, not a freeze. It must outlast a restart, measured at
    /// three to four seconds, and no more: every second beyond that is a second
    /// of stale picture on the wall, which is what a viewer sees when the scene
    /// goes dark and the LEDs stay bright.
    /// </remarks>
    private static readonly TimeSpan OutputGapHold = TimeSpan.FromSeconds(6);

    private readonly CancellationTokenSource _shutdown = new();
    private Task? _pump;
    private Rgb24Encoding _encoding;
    private readonly int _ledCount;

    public JellyfinWledOutputService(
        PlaybackEventCoordinator coordinator,
        WledDiscoveryService discoveryService,
        ILogger<JellyfinWledOutputService> logger)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var physical = new LedLayout(
            Math.Max(1, configuration.TopLedCount),
            Math.Max(1, configuration.RightLedCount),
            Math.Max(1, configuration.BottomLedCount),
            Math.Max(1, configuration.LeftLedCount));
        var logical = LogicalSamplingLayout.FromPhysicalLayout(physical);
        _ledCount = physical.TotalLedCount;
        _encoding = configuration.CorrectLedGamma ? Rgb24Encoding.Linear : Rgb24Encoding.Bt709;
        // The detector is stateful across frames, so it is created once with the
        // service rather than per frame. Disabled, sampling simply uses the whole
        // frame, exactly as before this option existed.
        var borderDetector = new BlackBorderDetector();
        var processor = new AmbilightFrameProcessor(
            physical,
            logical,
            frame => (Plugin.Instance?.Configuration.IgnoreBlackBorders ?? true)
                ? borderDetector.Detect(frame)
                : default,
            Math.Clamp(
                configuration.SamplingDepthPercent,
                EdgeSampler.MinimumDepthPercent,
                EdgeSampler.MaximumDepthPercent),
            () => _encoding,
            static () =>
            {
                var current = Plugin.Instance?.Configuration;
                return current is null
                    ? ColourAdjustment.None
                    : ColourAdjustment.FromPercentages(current.BrightnessPercent, current.SaturationPercent);
            });
        _output = new WledRealtimeOutput(
            new WledEndpoint(configuration.WledHost, Math.Clamp(configuration.WledHttpPort, 1, ushort.MaxValue)),
            configuration.RealtimeProtocol,
            new UdpDatagramSender());
        _scheduler = new LatestFrameOutputScheduler(_coordinator.LatestFrames, processor, _output);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await DetectEncodingAsync(cancellationToken).ConfigureAwait(false);
        _pump = Task.Run(RunPumpAsync, CancellationToken.None);
    }

    /// <summary>
    /// Asks the controller how it treats realtime data, so the encoding does not
    /// have to be guessed or configured by hand.
    /// </summary>
    private async Task DetectEncodingAsync(CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || !configuration.AutoDetectLedGamma)
        {
            return;
        }

        var detected = await _discoveryService
            .DetectRealtimeEncodingAsync(
                configuration.WledHost,
                Math.Clamp(configuration.WledHttpPort, 1, ushort.MaxValue),
                cancellationToken)
            .ConfigureAwait(false);
        if (detected is { } encoding)
        {
            _encoding = encoding;
        }
    }

    private async Task RunPumpAsync()
    {
        var lastFrameSent = DateTimeOffset.UtcNow;
        var wasPaused = false;
        var lastSend = DateTimeOffset.UtcNow;
        var lastLateReport = DateTimeOffset.MinValue;
        var lastResync = DateTimeOffset.MinValue;
        string? previousSession = null;
        var pendingFrames = new Queue<(DateTimeOffset DueAt, byte[] Rgb24Frame)>();
        var loggedProcessedFrame = false;
        var loggedSentFrame = false;
        var wasOutputEnabled = true;
        _logger.LogInformation("Realtime Ambilight output pump started.");
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / Math.Clamp(configuration.OutputFramesPerSecond, 1, 60)));
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                var outputEnabled = Plugin.Instance?.Configuration.Enabled ?? true;
                if (!outputEnabled)
                {
                    pendingFrames.Clear();
                    previousSession = null;
                    if (wasOutputEnabled)
                    {
                        await _output.ReleaseAsync(_shutdown.Token).ConfigureAwait(false);
                    }

                    wasOutputEnabled = false;
                    continue;
                }

                wasOutputEnabled = true;
                var currentSession = _coordinator.ActiveSessionId;
                if (previousSession is not null && currentSession is null)
                {
                    pendingFrames.Clear();
                    try
                    {
                        var fadeMilliseconds = Math.Clamp(Plugin.Instance?.Configuration.StopFadeMilliseconds ?? 250, 0, 5000);
                        if (fadeMilliseconds > 0)
                        {
                            await _output.FadeToBlackAsync(TimeSpan.FromMilliseconds(fadeMilliseconds), 30, _shutdown.Token).ConfigureAwait(false);
                        }
                        await _output.ReleaseAsync(_shutdown.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        _logger.LogError(exception, "Realtime Ambilight could not finish the playback-stop fade/release.");
                    }
                }

                if (previousSession is null && currentSession is not null)
                {
                    // Claim the strip immediately. Otherwise WLED keeps showing
                    // whatever effect it was running until the first analysed
                    // frame lands, which is seconds into the item.
                    try
                    {
                        await _scheduler.SendFrameAsync(new byte[_ledCount * 3], _shutdown.Token).ConfigureAwait(false);
                        lastFrameSent = DateTimeOffset.UtcNow;
                        lastSend = lastFrameSent;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        _logger.LogWarning(exception, "Realtime Ambilight could not blank the LEDs at playback start.");
                    }
                }

                previousSession = currentSession;
                try
                {
                    if (_scheduler.TryTakeProcessedFrame(out var rgb24Frame, out var framePositionTicks) && rgb24Frame is not null)
                    {
                        if (!loggedProcessedFrame)
                        {
                            _logger.LogInformation("Realtime Ambilight processed its first decoded frame.");
                            loggedProcessedFrame = true;
                        }

                        // Schedule against the playback timeline, not against the
                        // moment the frame arrived. The decoder runs ahead by a
                        // fixed lead, so each frame waits here until the picture
                        // reaches it. Because the clock is corrected from the
                        // client's own progress reports, decoder drift and
                        // restart lag are absorbed instead of accumulating.
                        var delay = TimeSpan.FromMilliseconds(Math.Clamp(Plugin.Instance?.Configuration.OutputDelayMilliseconds ?? 0, 0, 2000));
                        var lead = TimeSpan.FromTicks(framePositionTicks - _coordinator.CurrentPositionTicks);
                        var dueAt = DateTimeOffset.UtcNow + lead + delay;
                        var overdue = DateTimeOffset.UtcNow - dueAt;
                        if (overdue > TimeSpan.Zero)
                        {
                            // The decoder has fallen behind the picture; showing the
                            // frame late is still better than dropping the output.
                            dueAt = DateTimeOffset.UtcNow;

                            // A decoder that has lost its lead cannot win it back:
                            // it runs at playback speed, not faster. Restarting it
                            // at the current position is the only way back, so the
                            // error stays bounded instead of growing until the LEDs
                            // are showing an altogether earlier scene.
                            if (overdue > LateFrameResyncThreshold && DateTimeOffset.UtcNow - lastResync > ResyncCooldown)
                            {
                                // Only a resync that actually happened may start the
                                // cooldown. Charging a refusal against it leaves the
                                // pipeline stranded for the whole window, which is
                                // exactly how a lag of seconds survived unrepaired.
                                if (_coordinator.RequestAnalysisResync())
                                {
                                    lastResync = DateTimeOffset.UtcNow;

                                    // Deliberately keep what is queued: those frames
                                    // still bridge the restart, and dropping them is
                                    // what makes the gap visible.
                                    _logger.LogWarning(
                                        "Realtime Ambilight was {Overdue:F0} ms behind the picture and restarted the analysis decoder.",
                                        overdue.TotalMilliseconds);
                                }
                                else if (DateTimeOffset.UtcNow - lastLateReport > LateFrameReportInterval)
                                {
                                    lastLateReport = DateTimeOffset.UtcNow;
                                    _logger.LogWarning(
                                        "Realtime Ambilight is {Overdue:F0} ms behind the picture and could not restart the decoder; playback may be paused or between sessions.",
                                        overdue.TotalMilliseconds);
                                }
                            }
                            else if (overdue > LateFrameReportThreshold && DateTimeOffset.UtcNow - lastLateReport > LateFrameReportInterval)
                            {
                                lastLateReport = DateTimeOffset.UtcNow;
                                _logger.LogWarning(
                                    "Realtime Ambilight is {Overdue:F0} ms behind the picture; the analysis decoder is not keeping its lead.",
                                    overdue.TotalMilliseconds);
                            }
                        }

                        pendingFrames.Enqueue((dueAt, rgb24Frame));
                    }

                    var now = DateTimeOffset.UtcNow;
                    while (pendingFrames.TryPeek(out var pending) && pending.DueAt <= now)
                    {
                        pendingFrames.Dequeue();
                        await _scheduler.SendFrameAsync(pending.Rgb24Frame, _shutdown.Token).ConfigureAwait(false);
                        if (!loggedSentFrame)
                        {
                            _logger.LogInformation("Realtime Ambilight sent its first WLED frame.");
                            loggedSentFrame = true;
                        }
                        lastFrameSent = now;
                        lastSend = now;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "Realtime Ambilight output pump failed while processing a frame.");
                    continue;
                }
                // Holding the last frame covers two different silences: a pause,
                // and a gap in decoding such as a restart. Both are longer than
                // WLED's realtime timeout, and in both cases letting WLED reclaim
                // the strip looks like a fault rather than like nothing happening.
                // A pause holds indefinitely by design; a decoding gap holds only
                // long enough to bridge a restart, so a stalled decoder cannot
                // freeze one frame on the wall for the rest of the film.
                var paused = _coordinator.IsPaused;
                if (wasPaused && !paused)
                {
                    // Resuming restarts the decoder, so the bridge is needed again
                    // right now. Without this the budget was already spent by the
                    // pause itself, and WLED reclaimed the strip between the
                    // resume and the first new frame.
                    lastFrameSent = DateTimeOffset.UtcNow;
                }

                wasPaused = paused;

                var mayHold = paused
                    ? Plugin.Instance?.Configuration.HoldWhilePaused ?? true
                    : DateTimeOffset.UtcNow - lastFrameSent < OutputGapHold;

                if (mayHold
                    && pendingFrames.Count == 0
                    && currentSession is not null
                    && DateTimeOffset.UtcNow - lastSend >= PauseKeepAliveInterval)
                {
                    await _output.KeepAliveAsync(_shutdown.Token).ConfigureAwait(false);
                    lastSend = DateTimeOffset.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    /// <summary>
    /// Fades out and hands the LEDs back while Jellyfin shuts down.
    /// </summary>
    /// <remarks>
    /// Nothing here may propagate: a hosted service that throws from
    /// <c>StopAsync</c> aborts Jellyfin's own shutdown sequence, which a failing
    /// WLED call was previously observed to do. Losing the courtesy fade is
    /// always preferable to breaking the host.
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();

        try
        {
            if (_pump is not null)
            {
                await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await _output.FadeToBlackAsync(TimeSpan.FromMilliseconds(250), 30, cancellationToken).ConfigureAwait(false);
            await _output.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown was already cancelled; WLED releases on its own timeout.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Realtime Ambilight could not fade out during shutdown; WLED will release on its own timeout.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        _output.Dispose();
        _shutdown.Dispose();
    }
}
