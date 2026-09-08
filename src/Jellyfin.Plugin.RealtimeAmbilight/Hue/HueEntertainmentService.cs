#pragma warning disable CA1848, CA1873
using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
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
    private readonly HueCredentialStore _credentialStore;
    private readonly HueBridgeClient _bridgeClient;
    private readonly HueLightControl _lightControl;
    private readonly ILogger<HueEntertainmentService> _logger;
    private readonly HueEntertainmentStateMachine _stateMachine = new();
    private readonly LatestFrameBuffer<AnalysisFrame> _latestFrames;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Random _jitter = new();

    private HueFrameProcessor? _frameProcessor;
    private HueDtlsChannel? _channel;
    private HueCredentials? _credentials;
    private IReadOnlyList<HueEntertainmentChannel> _channels = [];
    private byte[]? _lastPacket;
    private DateTimeOffset _lastSend = DateTimeOffset.MinValue;
    private DateTimeOffset _nextRetryAt = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private HueSessionSnapshot? _snapshot;
    private Task? _pump;
    private Task? _lifecycleTask;

    public HueEntertainmentService(
        PlaybackEventCoordinator coordinator,
        HueCredentialStore credentialStore,
        HueBridgeClient bridgeClient,
        HueLightControl lightControl,
        ILogger<HueEntertainmentService> logger)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _bridgeClient = bridgeClient ?? throw new ArgumentNullException(nameof(bridgeClient));
        _lightControl = lightControl ?? throw new ArgumentNullException(nameof(lightControl));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _latestFrames = _coordinator.LatestFrames.Subscribe();
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
                if (currentSession is not null && _lifecycleTask is null)
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
                else if (DateTimeOffset.UtcNow >= _nextRetryAt && _lifecycleTask is null)
                {
                    _stateMachine.ConnectRequested();
                    _lifecycleTask = ConnectAndStreamAsync(configuration, currentSession);
                }

                break;

            case HueEntertainmentState.Connecting:
            case HueEntertainmentState.Stopping:
                // A background task owns this transition; observe it once done.
                if (_lifecycleTask is { IsCompleted: true })
                {
                    _lifecycleTask = null;
                }

                break;

            case HueEntertainmentState.Unpaired:
            case HueEntertainmentState.RelinkRequired:
                break;
        }
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
            _frameProcessor ??= new HueFrameProcessor(new PlaybackMonotonicTimeAdapter(), () => true);

            if (_snapshot?.PlaybackSessionId != sessionId)
            {
                _snapshot = await CaptureSnapshotAsync(configuration, credentials, selected, sessionId).ConfigureAwait(false);
            }

            var channel = new HueDtlsChannel(configuration.HueBridgeHost, credentials.ApplicationKey, credentials.ClientKey);
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
        finally
        {
            _lifecycleTask = null;
        }
    }

    private async Task<HueSessionSnapshot?> CaptureSnapshotAsync(
        PluginConfiguration configuration, HueCredentials credentials, HueEntertainmentConfiguration selected, string sessionId)
    {
        var lightIds = selected.Channels.SelectMany(c => c.MemberServiceIds).Distinct().ToArray();
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

        var brightnessFraction = Math.Clamp(configuration.HueBrightnessPercent, 1, 100) / 100d;
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

    private async Task StopAsync(PluginConfiguration configuration)
    {
        try
        {
            _channel?.Close();
            _channel?.Dispose();
            _channel = null;
            _frameProcessor?.Reset();

            var credentials = _credentials;
            if (credentials is not null)
            {
                await ApplyEndBehaviourAsync(configuration, credentials).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Hue Entertainment did not stop cleanly; the bridge's own timeout will reclaim the stream.");
        }
        finally
        {
            _stateMachine.StopCompleted();
            _lifecycleTask = null;
        }
    }

    private async Task ApplyEndBehaviourAsync(PluginConfiguration configuration, HueCredentials credentials)
    {
        var lightIds = _channels.SelectMany(c => c.MemberServiceIds).Distinct().ToArray();
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

            _channel?.Close();
            _channel?.Dispose();
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

        _channel?.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>Adapts a free-running stopwatch to <see cref="IMonotonicTime"/> for <see cref="HueFrameProcessor"/>.</summary>
    private sealed class PlaybackMonotonicTimeAdapter : IMonotonicTime
    {
        private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

        public TimeSpan Elapsed => _stopwatch.Elapsed;
    }
}
