#pragma warning disable CA1848, CA1873
using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Diagnostics;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Model;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Protocol;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RealtimeAmbilight.Hue;

/// <summary>Read-only status for the settings page and the controller.</summary>
public sealed record HueStatus(
    bool Enabled,
    bool Paired,
    HueEntertainmentState State,
    HueEntertainmentIssue Issue,
    string BridgeHost,
    string EntertainmentConfigurationName,
    int ChannelCount);

/// <summary>
/// Drives the Hue Entertainment lifecycle: its own frame subscription, its
/// own state machine, its own DTLS connection and its own pump loop --
/// entirely independent of <see cref="JellyfinWledOutputService"/>, sharing
/// only the same <see cref="PlaybackEventCoordinator"/> (for the same bound
/// device and the same decoded frames) and the same
/// <see cref="Plugin.Instance"/> configuration root.
/// </summary>
/// <remarks>
/// A Hue failure -- a bad connect, a dropped stream, a bridge that never
/// answers -- must never affect WLED or playback. It cannot: this service's
/// pump loop is its own <see cref="Task"/>, its own try/catch boundary, and
/// its own <see cref="FanOutFrameBuffer{TFrame}"/> subscription that only
/// ever replaces its own stale frame. There is no shared lock, buffer, or
/// loop between this class and <see cref="JellyfinWledOutputService"/>.
/// </remarks>
public sealed class HueEntertainmentService : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(40); // 25 Hz
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(60);
    private const int MaximumConsecutiveFailuresBeforeSlowestBackoff = 6;

    private readonly PlaybackEventCoordinator _coordinator;
    private readonly IHueCredentialStore _credentialStore;
    private readonly IHueBridgeClient _bridgeClient;
    private readonly IHueLightControl _lightControl;
    private readonly IHueStreamChannelFactory _channelFactory;
    private readonly PluginActivityLog _activityLog;
    private readonly ILogger<HueEntertainmentService> _logger;
    private readonly HueEntertainmentStateMachine _stateMachine = new();
    private readonly LatestFrameBuffer<AnalysisFrame> _latestFrames;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Random _jitter = new();
    // Same idea as JellyfinWledOutputService's own send-rate meter: how many
    // packets per second actually go out the DTLS socket, not how many
    // frames were merely produced upstream. Counts a keep-alive resend too --
    // that is genuinely a packet leaving this process every bit as much as a
    // fresh frame is.
    private readonly FrameRateMeter _sendRateMeter = new(new PlaybackMonotonicTimeAdapter());

    private HueFrameProcessor? _frameProcessor;
    private IHueStreamChannel? _channel;
    private HueCredentials? _credentials;
    private IReadOnlyList<HueEntertainmentChannel> _channels = [];
    private IReadOnlyDictionary<Guid, Guid> _lightIdsByServiceId = new Dictionary<Guid, Guid>();
    private byte[]? _lastPacket;
    private DateTimeOffset _lastSend = DateTimeOffset.MinValue;
    private DateTimeOffset _nextRetryAt = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private HueSessionSnapshot? _snapshot;
    private Task? _pump;
    private Task? _lifecycleTask;
    private int _disposed;

    public HueEntertainmentService(
        PlaybackEventCoordinator coordinator,
        IHueCredentialStore credentialStore,
        IHueBridgeClient bridgeClient,
        IHueLightControl lightControl,
        PluginActivityLog activityLog,
        ILogger<HueEntertainmentService> logger,
        IHueStreamChannelFactory? channelFactory = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _bridgeClient = bridgeClient ?? throw new ArgumentNullException(nameof(bridgeClient));
        _lightControl = lightControl ?? throw new ArgumentNullException(nameof(lightControl));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _channelFactory = channelFactory ?? new HueDtlsChannelFactory();
        _latestFrames = _coordinator.LatestFrames.Subscribe();
        // Every real state transition, mirrored into the settings page's own
        // "Live activity" log -- Unpaired/Ready are the two quiet, no-news
        // states an operator does not need called out every time.
        _stateMachine.Changed += change =>
        {
            if (change.State is HueEntertainmentState.Unpaired or HueEntertainmentState.Ready)
            {
                return;
            }

            _activityLog.Info(change.Issue == HueEntertainmentIssue.None
                ? $"Hue: {change.State}."
                : $"Hue: {change.State} ({change.Issue}).");
        };
    }

    public HueStatus GetStatus()
    {
        var configuration = Plugin.Instance?.Configuration;
        return new HueStatus(
            configuration?.HueEnabled ?? false,
            _credentials is not null,
            _stateMachine.State,
            _stateMachine.Issue,
            configuration?.HueBridgeHost ?? string.Empty,
            configuration?.HueEntertainmentConfigurationName ?? string.Empty,
            _channels.Count);
    }

    /// <summary>
    /// Packets actually sent per second, or <see langword="null"/> while not
    /// streaming -- mirrors <c>JellyfinWledOutputService.GetPerformance</c>'s
    /// own null-when-inactive convention, folded into the same dashboard
    /// "Pipeline" snapshot by <see cref="Api.WledDiscoveryController.GetPerformance"/>.
    /// </summary>
    public double? SendRateHz => _stateMachine.State == HueEntertainmentState.Streaming ? _sendRateMeter.RateHz : null;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _credentials = await _credentialStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        _pump = Task.Run(RunPumpAsync, CancellationToken.None);
    }

    /// <summary>Called by the pairing controller once new credentials are saved, so this service picks them up without a restart.</summary>
    public void NotifyCredentialsChanged(HueCredentials? credentials)
    {
        _credentials = credentials;
        if (credentials is null)
        {
            _stateMachine.Unpaired();
        }
    }

    private async Task RunPumpAsync()
    {
        _logger.LogInformation("Hue Entertainment pump started.");
        using var timer = new PeriodicTimer(PumpInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "Hue Entertainment pump failed while processing a tick.");
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task TickAsync()
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || !configuration.HueEnabled)
        {
            if (_stateMachine.State is not HueEntertainmentState.Unpaired)
            {
                await ForceStopAsync().ConfigureAwait(false);
            }

            return;
        }

        if (_credentials is null || configuration.HueEntertainmentConfigurationId == Guid.Empty)
        {
            if (_stateMachine.State is not HueEntertainmentState.Unpaired)
            {
                await ForceStopAsync().ConfigureAwait(false);
                _stateMachine.Unpaired();
            }

            return;
        }

        if (_stateMachine.State == HueEntertainmentState.Unpaired)
        {
            _stateMachine.Paired();
        }

        var currentSession = _coordinator.ActiveSessionId;
        var paused = _coordinator.IsPaused;

        switch (_stateMachine.State)
        {
            case HueEntertainmentState.Ready:
                if (currentSession is not null && LifecycleTaskIsFree())
                {
                    _stateMachine.ConnectRequested();
                    _lifecycleTask = ConnectAndStreamAsync(configuration, currentSession);
                }

                break;

            case HueEntertainmentState.Streaming:
            case HueEntertainmentState.Paused:
                if (currentSession is null)
                {
                    _stateMachine.StopRequested();
                    _lifecycleTask = StopAsync(configuration);
                }
                else if (paused && _stateMachine.State == HueEntertainmentState.Streaming)
                {
                    _stateMachine.PlaybackPaused();
                }
                else if (!paused && _stateMachine.State == HueEntertainmentState.Paused)
                {
                    _stateMachine.PlaybackResumed();
                }
                else
                {
                    await SendFrameOrKeepAliveAsync(configuration).ConfigureAwait(false);
                }

                break;

            case HueEntertainmentState.Recovering:
                if (currentSession is null)
                {
                    _stateMachine.RecoveryAbandoned();
                    _channel = null;
                }
                else if (DateTimeOffset.UtcNow >= _nextRetryAt && LifecycleTaskIsFree())
                {
                    _stateMachine.ConnectRequested();
                    _lifecycleTask = ConnectAndStreamAsync(configuration, currentSession);
                }

                break;

            case HueEntertainmentState.Connecting:
            case HueEntertainmentState.Stopping:
                // A background task owns this transition; observe it once done.
                LifecycleTaskIsFree();
                break;

            case HueEntertainmentState.Unpaired:
            case HueEntertainmentState.RelinkRequired:
                break;
        }
    }

    /// <summary>
    /// True when no lifecycle task is in flight -- and clears
    /// <see cref="_lifecycleTask"/> the moment one is found already complete,
    /// rather than leaving it set.
    /// </summary>
    /// <remarks>
    /// <see cref="ConnectAndStreamAsync"/>/<see cref="StopAsync(PluginConfiguration)"/>
    /// used to null this field themselves, in their own <c>finally</c>. That
    /// is a race: when either method throws before its first genuine
    /// <c>await</c> -- exactly what happened live, from a double-close bug in
    /// the underlying HueApi.Entertainment library's own
    /// <c>StreamingHueClient.Close()</c> -- the whole method body, including
    /// its <c>finally</c>, runs synchronously to completion *before* the
    /// caller's own <c>_lifecycleTask = StopAsync(...)</c> assignment
    /// statement finishes. The callee's <c>_lifecycleTask = null</c> then
    /// gets silently overwritten a moment later by that same assignment
    /// storing the (already-completed) returned task -- leaving
    /// <c>_lifecycleTask</c> permanently non-null with nothing left to ever
    /// clear it, since <see cref="HueEntertainmentState.Ready"/>'s own guard
    /// only ever checked for exactly <see langword="null"/>. Streaming
    /// silently never resumed for the rest of that Jellyfin process, with no
    /// further log line of any kind, until this was found and fixed. Now
    /// ownership of clearing this field lives in exactly one place -- here --
    /// checked wherever a "nothing in flight" decision is made, tolerating a
    /// completed-but-not-yet-cleared task the same as a null one.
    /// </remarks>
    private bool LifecycleTaskIsFree()
    {
        if (_lifecycleTask is null)
        {
            return true;
        }

        if (_lifecycleTask.IsCompleted)
        {
            _lifecycleTask = null;
            return true;
        }

        return false;
    }

    private async Task ConnectAndStreamAsync(PluginConfiguration configuration, string sessionId)
    {
        try
        {
            var credentials = _credentials!;
            var configurations = await _bridgeClient
                .GetEntertainmentConfigurationsAsync(configuration.HueBridgeHost, credentials.CertificateThumbprintSha256, credentials.ApplicationKey, _shutdown.Token)
                .ConfigureAwait(false);
            var selected = configurations.FirstOrDefault(c => c.Id == configuration.HueEntertainmentConfigurationId);
            if (selected is null)
            {
                _stateMachine.RequiresRelink(HueEntertainmentIssue.EntertainmentConfigurationMissing);
                return;
            }

            _channels = selected.Channels;
            // A channel member's own id is an *entertainment service* id,
            // not the light resource id CLIP v2 light calls need -- resolved
            // once per connect (cheap: one bridge call) rather than assuming
            // the two ids are interchangeable, which they are not. See
            // HueEntertainmentServiceParser's remarks for why this exists.
            _lightIdsByServiceId = await _bridgeClient
                .ResolveLightIdsAsync(configuration.HueBridgeHost, credentials.CertificateThumbprintSha256, credentials.ApplicationKey, _shutdown.Token)
                .ConfigureAwait(false);
            // Built fresh on every connect, not cached for the service's
            // whole lifetime: cheap to construct, and it means a changed
            // HueResponsePercent takes effect on the next reconnect (stop
            // then resume playback) rather than needing a full restart.
            _frameProcessor = new HueFrameProcessor(new PlaybackMonotonicTimeAdapter(), () => true, responsePercent: configuration.HueResponsePercent);

            if (_snapshot?.PlaybackSessionId != sessionId)
            {
                _snapshot = await CaptureSnapshotAsync(configuration, credentials, selected, sessionId).ConfigureAwait(false);
            }

            // Entertainment streaming changes a light's colour, never its
            // power state -- a light the snapshot found off would otherwise
            // stay dark for the whole session regardless of what is streamed
            // to it. Restoring "off" afterward is already handled by
            // RestoreAsync from this same snapshot; this is only ever turning
            // back on what that will turn back off.
            var offLightIds = ResolveLightIds(selected.Channels)
                .Where(id => _snapshot?.Lights.FirstOrDefault(l => l.LightId == id)?.On == false)
                .ToArray();
            if (offLightIds.Length > 0)
            {
                _logger.LogInformation("Hue Entertainment turning on {Count} light(s) that were off before streaming.", offLightIds.Length);
            }

            foreach (var lightId in offLightIds)
            {
                await _lightControl
                    .TurnOnAsync(configuration.HueBridgeHost, credentials.CertificateThumbprintSha256, credentials.ApplicationKey, lightId, _shutdown.Token)
                    .ConfigureAwait(false);
            }

            var channel = _channelFactory.Create(configuration.HueBridgeHost, credentials.ApplicationKey, credentials.ClientKey);
            var connected = await channel.TryConnectAsync(selected.Id, ConnectTimeout, _shutdown.Token).ConfigureAwait(false);
            if (!connected)
            {
                channel.Dispose();
                ReportTransientFailure(HueEntertainmentIssue.TemporarilyUnreachable);
                return;
            }

            _channel = channel;
            _consecutiveFailures = 0;
            _lastPacket = null;
            _stateMachine.Connected();
            _logger.LogInformation("Hue Entertainment connected to {ConfigurationName} on {Host}.", selected.Name, configuration.HueBridgeHost);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Hue Entertainment could not connect.");
            ReportTransientFailure(HueEntertainmentIssue.TemporarilyUnreachable);
        }
        // No finally clearing _lifecycleTask here -- see LifecycleTaskIsFree's remarks.
    }

    /// <summary>
    /// Maps each channel's own member ids (entertainment service ids) to the
    /// light resource ids CLIP v2 light calls actually need, via
    /// <see cref="_lightIdsByServiceId"/>. A member with no resolved mapping
    /// is dropped with a warning rather than sent to the bridge as-is, which
    /// would just 404 -- exactly the failure this whole mapping step exists
    /// to fix.
    /// </summary>
    private List<Guid> ResolveLightIds(IEnumerable<HueEntertainmentChannel> channels)
    {
        var lightIds = new List<Guid>();
        foreach (var serviceId in channels.SelectMany(c => c.MemberServiceIds).Distinct())
        {
            if (_lightIdsByServiceId.TryGetValue(serviceId, out var lightId))
            {
                lightIds.Add(lightId);
            }
            else
            {
                _logger.LogWarning("Hue Entertainment could not resolve entertainment service {ServiceId} to a light; skipping it for snapshot/end-of-session commands.", serviceId);
            }
        }

        return lightIds;
    }

    private async Task<HueSessionSnapshot?> CaptureSnapshotAsync(
        PluginConfiguration configuration, HueCredentials credentials, HueEntertainmentConfiguration selected, string sessionId)
    {
        var lightIds = ResolveLightIds(selected.Channels);
        var entries = new List<HueLightSnapshotEntry>();
        foreach (var lightId in lightIds)
        {
            var entry = await _lightControl
                .ReadStateAsync(configuration.HueBridgeHost, credentials.CertificateThumbprintSha256, credentials.ApplicationKey, lightId, _shutdown.Token)
                .ConfigureAwait(false);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return new HueSessionSnapshot(sessionId, entries);
    }

    private async Task SendFrameOrKeepAliveAsync(PluginConfiguration configuration)
    {
        if (_channel is null || _frameProcessor is null || _channels.Count == 0)
        {
            return;
        }

        var brightnessFraction = Math.Clamp(configuration.HueBrightnessPercent, 1, 200) / 100d;
        byte[]? packet = null;

        if (_stateMachine.State == HueEntertainmentState.Streaming && _latestFrames.TryTake(out var frame) && frame is not null)
        {
            var colours = _frameProcessor.Process(frame, _channels, brightnessFraction);
            var channelColours = _channels
                .Select(c => new HueStreamPacketizer.ChannelColour(
                    (byte)c.ChannelId,
                    colours.TryGetValue(c.ChannelId, out var colour) ? colour : default(LinearRgb)))
                .ToArray();
            packet = HueStreamPacketizer.Packetize(configuration.HueEntertainmentConfigurationId, channelColours)[0];
            _lastPacket = packet;
        }
        else if (DateTimeOffset.UtcNow - _lastSend < KeepAliveInterval)
        {
            return; // Paused, or no new frame yet: hold, do not resend every 40 ms.
        }
        else
        {
            packet = _lastPacket; // Paused: keep the entertainment session valid with the last colours.
        }

        if (packet is null)
        {
            return;
        }

        try
        {
            _channel.SendPacket(packet);
            _lastSend = DateTimeOffset.UtcNow;
            _sendRateMeter.Increment();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Hue Entertainment lost its connection while sending.");
            ReportTransientFailure(HueEntertainmentIssue.TemporarilyUnreachable);
        }
    }

    private void ReportTransientFailure(HueEntertainmentIssue issue)
    {
        _consecutiveFailures++;
        var exponent = Math.Min(_consecutiveFailures, MaximumConsecutiveFailuresBeforeSlowestBackoff);
        var backoffSeconds = Math.Min(MaximumBackoff.TotalSeconds, MinimumBackoff.TotalSeconds * Math.Pow(2, exponent - 1));
        var jitterSeconds = backoffSeconds * (0.8 + (_jitter.NextDouble() * 0.4)); // +/-20% jitter
        _nextRetryAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(jitterSeconds);
        _stateMachine.ConnectionLost(issue);
    }

    /// <remarks>
    /// Closing the channel and applying the end-of-session light behaviour
    /// are deliberately isolated from each other, each in its own try/catch,
    /// rather than one try around both (as this used to be). Observed live:
    /// <c>StreamingHueClient.Dispose()</c> (via the inherited
    /// <c>Close()</c>) reliably throws <see cref="ObjectDisposedException"/>
    /// -- a double-close bug in the underlying HueApi.Entertainment library
    /// itself (its DTLS transport's own <c>Close()</c> already cascades into
    /// closing the shared UDP socket; <c>Dispose()</c> then tries to close
    /// the same socket again). Harmless for the stream itself, since the
    /// bridge's own realtime timeout reclaims it regardless -- but with one
    /// shared try, that exception used to skip the end-of-session command
    /// (warm-white-dim or restore) entirely, every single time, leaving the
    /// lights showing whatever colour streaming last left them in until the
    /// bridge's own timeout eventually caught up. Splitting them means a
    /// failure to close the local socket can never prevent the light command
    /// that actually matters to the operator from running.
    /// </remarks>
    private async Task StopAsync(PluginConfiguration configuration)
    {
        try
        {
            _channel?.Dispose();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Hue Entertainment did not close its local connection cleanly; the bridge's own timeout will reclaim the stream.");
        }
        finally
        {
            _channel = null;
        }

        _frameProcessor?.Reset();

        try
        {
            var credentials = _credentials;
            if (credentials is not null)
            {
                await ApplyEndBehaviourAsync(configuration, credentials).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Hue Entertainment could not apply the end-of-session light behaviour.");
        }
        finally
        {
            _stateMachine.StopCompleted();
        }

        // No finally clearing _lifecycleTask here -- see LifecycleTaskIsFree's remarks.
    }

    private async Task ApplyEndBehaviourAsync(PluginConfiguration configuration, HueCredentials credentials)
    {
        var lightIds = ResolveLightIds(_channels);
        var snapshot = _snapshot;
        _snapshot = null;

        foreach (var lightId in lightIds)
        {
            if (configuration.HueEndBehaviour == Core.Hue.HueEndBehaviour.RestorePreviousState
                && snapshot?.Lights.FirstOrDefault(l => l.LightId == lightId) is { } entry)
            {
                await _lightControl
                    .RestoreAsync(configuration.HueBridgeHost, credentials.CertificateThumbprintSha256, credentials.ApplicationKey, entry, _shutdown.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                await _lightControl
                    .ApplyWarmWhiteDimAsync(configuration.HueBridgeHost, credentials.CertificateThumbprintSha256, credentials.ApplicationKey, lightId, _shutdown.Token)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task ForceStopAsync()
    {
        try
        {
            var configuration = Plugin.Instance?.Configuration;
            if (configuration is not null && _channel is not null && _credentials is not null)
            {
                await StopAsync(configuration).ConfigureAwait(false);
                return;
            }

            try
            {
                _channel?.Dispose();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Same known double-close bug as StopAsync -- see its remarks.
                _logger.LogWarning(exception, "Hue Entertainment did not close its local connection cleanly; the bridge's own timeout will reclaim the stream.");
            }

            _channel = null;
        }
        finally
        {
            _stateMachine.Unpaired();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        try
        {
            if (_pump is not null)
            {
                await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            _channel?.Close();
        }
        catch (Exception)
        {
            // Best-effort during shutdown; a hosted service must not throw from StopAsync.
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Jellyfin's DI registers this class both as itself and, forwarded,
        // as IHostedService (see PluginServiceRegistrator) -- two distinct
        // singleton registrations resolving to the very same instance. The
        // generic host's own ServiceProviderEngineScope does not deduplicate
        // by reference across registrations when it captures disposables,
        // so it calls DisposeAsync on this one object twice at shutdown.
        // Confirmed live: the second call reached _shutdown.Cancel() on an
        // already-disposed CancellationTokenSource and threw
        // ObjectDisposedException as an unhandled [FTL] exception during
        // shutdown. Harmless in effect (the process was already tearing
        // down) but alarming in the log and worth not repeating.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _shutdown.Cancel();
        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            _channel?.Dispose();
        }
        catch (Exception)
        {
            // Best-effort during disposal; same known double-close bug as StopAsync.
        }

        _shutdown.Dispose();
    }

    /// <summary>Adapts a free-running stopwatch to <see cref="IMonotonicTime"/> for <see cref="HueFrameProcessor"/>.</summary>
    private sealed class PlaybackMonotonicTimeAdapter : IMonotonicTime
    {
        private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

        public TimeSpan Elapsed => _stopwatch.Elapsed;
    }
}
