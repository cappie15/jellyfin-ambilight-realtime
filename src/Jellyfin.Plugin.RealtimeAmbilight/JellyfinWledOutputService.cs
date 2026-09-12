#pragma warning disable CA1848, CA1873
using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Diagnostics;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Output;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Wled;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>Dashboard-facing throughput at each pipeline stage, Hz. Null means no session is active.</summary>
public sealed record PipelinePerformanceSnapshot(double? AnalyseFps, double? SampleFps, double? WledRenderFps, double? HueSendFps = null, double? SourceFps = null);

/// <summary>Runs the latest-only frame pump for the reference 831-led WLED.</summary>
public sealed class JellyfinWledOutputService : IHostedService, IAsyncDisposable
{
    private readonly PlaybackEventCoordinator _coordinator;
    private readonly WledDiscoveryService _discoveryService;
    private readonly WledRealtimeOutput _output;
    private readonly LatestFrameOutputScheduler _scheduler;
    private readonly ILogger<JellyfinWledOutputService> _logger;
    private readonly PluginActivityLog _activityLog;
    private readonly LedLayout _physicalLayout;
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
    private int _disposed;
    private Task? _pump;
    private Rgb24Encoding _encoding;
    private readonly int _ledCount;
    private readonly int _bytesPerLed;
    private readonly IDitheredChannelEncoder _calibrationEncoder;
    private readonly object _calibrationPhotoLock = new();
    private bool _calibrationActive;
    private byte[]? _calibrationPhotoPixels;
    private int _calibrationPhotoWidth;
    private int _calibrationPhotoHeight;
    private PerimeterColourAdjustment _calibrationAdjustment = PerimeterColourAdjustment.None;

    /// <summary>
    /// The colour-tuning wizard's current step, shared between the settings
    /// page and the anonymous TV pattern page. Reading and moving it never
    /// itself touches WLED; only the calibration preview methods below do.
    /// </summary>
    public CalibrationWizardState Wizard { get; } = new();

    public bool IsCalibrationPreviewActive => Volatile.Read(ref _calibrationActive);

    /// <summary>
    /// The three throughput numbers a real bottleneck actually shows up in,
    /// each measured at its own choke point in the pipeline rather than
    /// inferred: how many frames per second the analysis decoder is
    /// actually producing, how many the sampling/colour stage actually
    /// processes, and how many actually reach WLED. Comparing them locates
    /// a stall -- analysis far below the configured decode rate points at
    /// the decoder (see ADR-013); sampling matching analysis but WLED
    /// render far below both points at the output side instead. Each
    /// number is <see langword="null"/> whenever no session is active,
    /// rather than a stale reading from whatever last played -- the meters
    /// themselves do not know playback has stopped, only that nothing has
    /// incremented them since.
    /// </summary>
    public PipelinePerformanceSnapshot GetPerformance()
    {
        var active = _coordinator.ActiveSessionId is not null;
        return new PipelinePerformanceSnapshot(
            active ? _coordinator.LatestFrames.PublishRateHz : null,
            active ? _scheduler.ProcessRateHz : null,
            active ? _output.SendRateHz : null,
            SourceFps: active ? _coordinator.SourceFramesPerSecond : null);
    }

    public JellyfinWledOutputService(
        PlaybackEventCoordinator coordinator,
        WledDiscoveryService discoveryService,
        PluginActivityLog activityLog,
        ILogger<JellyfinWledOutputService> logger)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        _physicalLayout = new LedLayout(
            Math.Max(1, configuration.TopLedCount),
            Math.Max(1, configuration.RightLedCount),
            Math.Max(1, configuration.BottomLedCount),
            Math.Max(1, configuration.LeftLedCount));
        var logical = LogicalSamplingLayout.FromPhysicalLayout(_physicalLayout);
        _ledCount = _physicalLayout.TotalLedCount;
        _bytesPerLed = configuration.SendWhiteChannel ? 4 : 3;
        _calibrationEncoder = configuration.SendWhiteChannel ? new DitheredRgbw32Encoder() : new DitheredRgb24Encoder();
        _encoding = configuration.CorrectLedGamma ? Rgb24Encoding.Linear : Rgb24Encoding.Bt709;
        // The detector is stateful across frames, so it is created once with the
        // service rather than per frame. Disabled, sampling simply uses the whole
        // frame, exactly as before this option existed.
        var borderDetector = new BlackBorderDetector();
        var processor = new AmbilightFrameProcessor(
            _physicalLayout,
            logical,
            frame => (Plugin.Instance?.Configuration.IgnoreBlackBorders ?? true)
                ? borderDetector.Detect(frame)
                : default,
            Math.Clamp(
                configuration.SamplingDepthPercent,
                EdgeSampler.MinimumDepthPercent,
                EdgeSampler.MaximumDepthPercent),
            () => _encoding,
            static () => BuildColourTuning(Plugin.Instance?.Configuration).ToAdjustment(),
            static () => Plugin.Instance?.Configuration.MinimumColourHoldMilliseconds ?? 0,
            configuration.SendWhiteChannel,
            static () => Plugin.Instance?.Configuration.WledSmoothingMilliseconds ?? 0);
        _output = new WledRealtimeOutput(
            new WledEndpoint(configuration.WledHost, Math.Clamp(configuration.WledHttpPort, 1, ushort.MaxValue)),
            configuration.RealtimeProtocol,
            new UdpDatagramSender(),
            _bytesPerLed);
        _scheduler = new LatestFrameOutputScheduler(_coordinator.LatestFrames.Subscribe(), processor, _output);
    }

    /// <summary>
    /// Starts the pump immediately rather than awaiting WLED's own two
    /// startup probes first. <see cref="IHostedService.StartAsync"/> is
    /// awaited by Jellyfin's own host startup sequence, so blocking here on
    /// two WLED round trips (each bounded by its own multi-second timeout)
    /// added that same delay to Jellyfin's own overall startup whenever WLED
    /// happened to be unreachable at boot -- observed live, not
    /// hypothetical. Neither probe is needed before the pump can usefully
    /// run: a wrong encoding guess or an unset "force max brightness" for the
    /// first few frames is a much smaller cost than delaying the entire
    /// server's readiness on a controller that might not even be reachable.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pump = Task.Run(RunPumpAsync, CancellationToken.None);
        _ = RunStartupProbesAsync();
        return Task.CompletedTask;
    }

    private async Task RunStartupProbesAsync()
    {
        try
        {
            await DetectEncodingAsync(_shutdown.Token).ConfigureAwait(false);
            await EnsureForceMaxBrightnessAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Realtime Ambilight could not complete its startup WLED checks.");
        }
    }

    /// <summary>
    /// Defaults every consenting install to WLED's own brightness dial never
    /// affecting realtime output: checked once per plugin start (not every
    /// session, to keep this to one HTTP round trip rather than one per
    /// playback), and only for an operator who has already opted into this
    /// plugin writing WLED settings at all. An operator who wants WLED's own
    /// dial to keep mattering can turn "Allow this plugin to fix WLED
    /// settings" off, or turn "force max brightness" back off in WLED itself
    /// afterwards -- this only ever turns it on, never fights that choice
    /// back on within the same run.
    /// </summary>
    private async Task EnsureForceMaxBrightnessAsync(CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || !configuration.AllowWledControl || string.IsNullOrWhiteSpace(configuration.WledHost))
        {
            return;
        }

        var port = Math.Clamp(configuration.WledHttpPort, 1, ushort.MaxValue);
        var settings = await _discoveryService.ReadRealtimeSettingsAsync(configuration.WledHost, port, cancellationToken).ConfigureAwait(false);
        if (settings is null || settings.ForcesMaxBrightness)
        {
            return;
        }

        if (await _discoveryService.TryEnableForceMaxBrightnessAsync(configuration.WledHost, port, cancellationToken).ConfigureAwait(false))
        {
            _activityLog.Info("WLED: turned on \"force max brightness\", so its own dial cannot dim the Ambilight output.");
        }
    }

    /// <summary>
    /// Uploads one calibration photo's already-downsized pixels -- full-range
    /// sRGB RGBA, exactly what a browser <c>canvas</c> hands back -- and drives
    /// the LEDs from its actual sampled edges through the same sampling,
    /// interpolation and colour pipeline real playback uses: each side gets
    /// whatever the photo's own edge actually contains, not one flat colour.
    /// </summary>
    /// <remarks>
    /// Takes no tuning: the anonymous TV page uploads pixels only, and this
    /// applies whatever the settings page most recently sent via
    /// <see cref="RetuneCalibrationAsync"/> (or the identity if nothing has
    /// yet this session). That keeps the TV page ignorant of slider state
    /// entirely -- it only ever has to know which photo is showing.
    /// </remarks>
    public async Task<bool> ShowCalibrationPhotoAsync(byte[] rgbaPixels, int width, int height, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rgbaPixels);
        if (_coordinator.ActiveSessionId is not null)
        {
            return false;
        }

        PerimeterColourAdjustment adjustment;
        lock (_calibrationPhotoLock)
        {
            _calibrationPhotoPixels = rgbaPixels;
            _calibrationPhotoWidth = width;
            _calibrationPhotoHeight = height;
            adjustment = _calibrationAdjustment;
        }

        Volatile.Write(ref _calibrationActive, true);
        await SendCalibrationPhotoFrameAsync(rgbaPixels, width, height, adjustment, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Re-applies fresh tuning to the cached photo's already-sampled edges
    /// without re-fetching or re-sampling anything. A slider therefore feels
    /// live: only the cheap colour-adjustment step reruns, not image capture.
    /// Returns <see langword="false"/> when no photo has been uploaded yet.
    /// </summary>
    public async Task<bool> RetuneCalibrationAsync(PerimeterColourAdjustment adjustment, CancellationToken cancellationToken)
    {
        if (_coordinator.ActiveSessionId is not null)
        {
            return false;
        }

        byte[]? photoPixels;
        int photoWidth;
        int photoHeight;
        lock (_calibrationPhotoLock)
        {
            _calibrationAdjustment = adjustment;
            photoPixels = _calibrationPhotoPixels;
            photoWidth = _calibrationPhotoWidth;
            photoHeight = _calibrationPhotoHeight;
        }

        if (photoPixels is null)
        {
            return false;
        }

        await SendCalibrationPhotoFrameAsync(photoPixels, photoWidth, photoHeight, adjustment, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Returns control to WLED after an installer leaves calibration.</summary>
    public async Task StopCalibrationPreviewAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _calibrationActive, false);
        lock (_calibrationPhotoLock)
        {
            _calibrationPhotoPixels = null;
        }

        await _output.ReleaseAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SendCalibrationPhotoFrameAsync(
        byte[] rgbaPixels,
        int width,
        int height,
        PerimeterColourAdjustment adjustment,
        CancellationToken cancellationToken)
    {
        var logical = LogicalSamplingLayout.FromPhysicalLayout(_physicalLayout);
        var depth = Math.Clamp(
            Plugin.Instance?.Configuration.SamplingDepthPercent ?? EdgeSampler.DefaultDepthPercent,
            EdgeSampler.MinimumDepthPercent,
            EdgeSampler.MaximumDepthPercent);
        var samples = EdgeSampler.SampleSrgb(rgbaPixels, width, height, default, logical, depth);
        var physicalFrame = LinearLightInterpolator.InterpolatePerimeter(
            _physicalLayout,
            logical,
            samples.Top,
            samples.Right,
            samples.Bottom,
            samples.Left);
        for (var index = 0; index < physicalFrame.Length; index++)
        {
            physicalFrame[index] = adjustment.Apply(physicalFrame[index], index, _physicalLayout);
        }

        await _output.SendFrameAsync(_calibrationEncoder.Encode(physicalFrame, _encoding, adjustment.WhiteExtractionFactor), cancellationToken).ConfigureAwait(false);
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
        var pendingFrames = new Queue<(DateTimeOffset DueAt, LinearRgb[] Target)>();
        var loggedProcessedFrame = false;
        var loggedSentFrame = false;
        var wasOutputEnabled = true;
        _logger.LogInformation("Realtime Ambilight output pump started.");
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / Math.Clamp(configuration.OutputFramesPerSecond, 1, 180)));
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
                var calibrationActive = Volatile.Read(ref _calibrationActive);
                if (calibrationActive && currentSession is null)
                {
                    // The initial frame is sent by ShowCalibrationPreviewAsync or
                    // ShowCalibrationPhotoAsync; one keepalive per second maintains
                    // WLED's temporary ownership without resending 30 identical
                    // frames per second -- KeepAliveAsync repeats whatever the
                    // last sampled photo frame was.
                    if (DateTimeOffset.UtcNow - lastSend >= PauseKeepAliveInterval)
                    {
                        await _output.KeepAliveAsync(_shutdown.Token).ConfigureAwait(false);
                        lastSend = DateTimeOffset.UtcNow;
                    }

                    continue;
                }

                if (calibrationActive)
                {
                    // Never let a forgotten preview steal a real film. Playback
                    // wins and the next normal frame takes control immediately.
                    Volatile.Write(ref _calibrationActive, false);
                    lock (_calibrationPhotoLock)
                    {
                        _calibrationPhotoPixels = null;
                    }

                    _logger.LogInformation("Realtime Ambilight calibration preview ended because playback started.");
                }

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
                        _activityLog.Warning("WLED: could not fade out cleanly at stop; releasing anyway.");
                    }

                    _activityLog.Info("WLED: playback ended, LEDs released.");
                }

                if (previousSession is null && currentSession is not null)
                {
                    // Forget any target left over from a previous session --
                    // otherwise the repeat-frame path below could resend an
                    // old colour over the blank frame this same branch is
                    // about to send.
                    _scheduler.ClearTarget();

                    // Claim the strip immediately. Otherwise WLED keeps showing
                    // whatever effect it was running until the first analysed
                    // frame lands, which is seconds into the item.
                    try
                    {
                        await _scheduler.SendFrameAsync(new byte[_ledCount * _bytesPerLed], _shutdown.Token).ConfigureAwait(false);
                        lastFrameSent = DateTimeOffset.UtcNow;
                        lastSend = lastFrameSent;
                        _activityLog.Info("WLED: playback started, LEDs claimed.");
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        _logger.LogWarning(exception, "Realtime Ambilight could not blank the LEDs at playback start.");
                        _activityLog.Warning("WLED: could not claim the LEDs at playback start.");
                    }
                }

                previousSession = currentSession;
                try
                {
                    if (_scheduler.TrySampleFrame(out var target, out var framePositionTicks) && target is not null)
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
                                    _activityLog.Warning($"WLED: {overdue.TotalMilliseconds:F0} ms behind the picture; restarted the decoder.");
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

                        pendingFrames.Enqueue((dueAt, target));
                    }
                    else if (currentSession is not null)
                    {
                        // No new decoded frame this tick -- the source video's
                        // own frame rate is lower than the configured output
                        // rate, which is normal (a 24fps film cannot yield a
                        // new sample every 16ms at 60fps output). Smoothing and
                        // the encoder's own temporal dithering still benefit
                        // from running at the full output tick rate, so keep
                        // easing/dithering toward the last real sample and send
                        // that. Sent immediately rather than through the
                        // due-time queue below: a repeat has no media position
                        // to schedule against, and queuing it behind decode-ahead
                        // frames already due seconds from now would defeat the
                        // whole point of sending it promptly.
                        var repeatFrame = _scheduler.TryRepeatProcessedFrame();
                        if (repeatFrame is not null)
                        {
                            await _scheduler.SendFrameAsync(repeatFrame, _shutdown.Token).ConfigureAwait(false);
                            lastFrameSent = DateTimeOffset.UtcNow;
                            lastSend = lastFrameSent;
                        }
                    }

                    var now = DateTimeOffset.UtcNow;
                    while (pendingFrames.TryPeek(out var pending) && pending.DueAt <= now)
                    {
                        pendingFrames.Dequeue();

                        // Dwell/smoothing/encoding happen here, at the moment
                        // this target actually becomes due -- not back when it
                        // was sampled, which the decoder's own lead can put
                        // several seconds ahead of the picture actually due
                        // right now. Encoding at sample time instead let a
                        // repeated frame (see the branch above) race a
                        // too-early target against this queue's own correctly
                        // scheduled one once it finally arrived, which is what
                        // caused the strip to visibly flicker between the two.
                        await _scheduler.SendFrameAsync(_scheduler.EncodeTarget(pending.Target), _shutdown.Token).ConfigureAwait(false);
                        if (!loggedSentFrame)
                        {
                            _logger.LogInformation("Realtime Ambilight sent its first WLED frame.");
                            _activityLog.Info("WLED: streaming started.");
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

                // Deliberately NOT gated on pendingFrames.Count == 0. It used
                // to be: the decoder runs ahead of playback by design (see
                // the scheduling above), so frames sit queued for a future
                // dueAt for as long as that lead lasts -- which, right at
                // the start of playback (or after any resync), can already
                // exceed WLED's own realtime timeout (~2.3 s, per ADR-004)
                // before the first frame actually becomes due. With this
                // guard, that entire window sent nothing at all: WLED
                // reclaimed the strip and showed its own configured default
                // preset -- reported live as "film aan -> donker -> 2
                // seconden later: warmwit -> seconde later: correcte
                // ambilight". A keepalive firing while frames are merely
                // queued-but-not-yet-due is at worst one redundant resend of
                // the already-correct last frame right before a real one
                // arrives anyway; the previous silence was actively harmful.
                if (mayHold
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
        // Same dual-registration double-dispose exposure as
        // HueEntertainmentService.DisposeAsync -- see its remarks. This
        // class is registered the same way (itself, plus forwarded as
        // IHostedService), so it is equally at risk even though this
        // particular shutdown only happened to surface the Hue one.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _shutdown.Cancel();
        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        _output.Dispose();
        _shutdown.Dispose();
    }

    private static PerimeterColourTuning BuildColourTuning(PluginConfiguration? configuration)
    {
        if (configuration is null)
        {
            return PerimeterColourTuning.Default;
        }

        return new PerimeterColourTuning(
            configuration.BrightnessPercent, configuration.SaturationPercent,
            configuration.RedGainPercent, configuration.GreenGainPercent, configuration.BlueGainPercent,
            configuration.BlackLevelFloorPercent,
            configuration.WallColourHex, configuration.WallColourCorrectionPercent,
            configuration.TopBrightnessPercent, configuration.TopRedGainPercent, configuration.TopGreenGainPercent, configuration.TopBlueGainPercent,
            configuration.RightBrightnessPercent, configuration.RightRedGainPercent, configuration.RightGreenGainPercent, configuration.RightBlueGainPercent,
            configuration.BottomBrightnessPercent, configuration.BottomRedGainPercent, configuration.BottomGreenGainPercent, configuration.BottomBlueGainPercent,
            configuration.LeftBrightnessPercent, configuration.LeftRedGainPercent, configuration.LeftGreenGainPercent, configuration.LeftBlueGainPercent,
            configuration.RedHueShiftDegrees, configuration.RedBrightnessPercent, configuration.RedIntensityPercent,
            configuration.GreenHueShiftDegrees, configuration.GreenBrightnessPercent, configuration.GreenIntensityPercent,
            configuration.BlueHueShiftDegrees, configuration.BlueBrightnessPercent, configuration.BlueIntensityPercent,
            configuration.YellowHueShiftDegrees, configuration.YellowBrightnessPercent, configuration.YellowIntensityPercent,
            configuration.CyanHueShiftDegrees, configuration.CyanBrightnessPercent, configuration.CyanIntensityPercent,
            configuration.MagentaHueShiftDegrees, configuration.MagentaBrightnessPercent, configuration.MagentaIntensityPercent,
            configuration.WhiteChannelStrengthPercent);
    }
}
