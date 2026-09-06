#pragma warning disable CA1848, CA1873
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>
/// Thin host adapter. Jellyfin callbacks only translate and enqueue; no media
/// lookup, process start, network call, or await occurs on the callback thread.
/// </summary>
public sealed class JellyfinPlaybackEventAdapter : IHostedService, IAsyncDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly PlaybackEventCoordinator _coordinator;
    private readonly ILogger<JellyfinPlaybackEventAdapter> _logger;
    private int _subscribed;

    public JellyfinPlaybackEventAdapter(ISessionManager sessionManager, PlaybackEventCoordinator coordinator, ILogger<JellyfinPlaybackEventAdapter> logger)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _subscribed, 1) == 0)
        {
            _coordinator.AnalysisFailed += OnAnalysisFailed;
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        await _coordinator.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        Unsubscribe();
        return _coordinator.DisposeAsync();
    }

    private void OnAnalysisFailed(Exception exception)
    {
        _logger.LogError(exception, "Realtime Ambilight analysis failed; the LEDs will stay dark until the next playback event.");
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs args)
    {
        try
        {
            // Both rejections below used to be silent, which made a plugin that
            // simply never lit up impossible to diagnose from the log. Only the
            // start event reports; progress fires far too often to log.
            if (!IsTargetDevice(args.Session?.DeviceId, args.Session?.DeviceName))
            {
                _logger.LogInformation(
                    "Realtime Ambilight ignored playback on device {DeviceName} ({DeviceId}) because it is bound to {TargetDeviceName} ({TargetDeviceId}). Rebind on the settings page or choose \"All devices\".",
                    args.Session?.DeviceName,
                    args.Session?.DeviceId,
                    Plugin.Instance?.Configuration.TargetDeviceName,
                    Plugin.Instance?.Configuration.TargetDeviceId);
                return;
            }

            var sessionId = args.Session?.Id;
            if (string.IsNullOrWhiteSpace(sessionId) || !args.PlaybackPositionTicks.HasValue || args.Item is null || string.IsNullOrWhiteSpace(args.MediaSourceId))
            {
                _logger.LogInformation(
                    "Realtime Ambilight could not use a playback-start event on device {DeviceName}: session={SessionId}, position={HasPosition}, item={HasItem}, mediaSource={MediaSourceId}.",
                    args.Session?.DeviceName,
                    sessionId,
                    args.PlaybackPositionTicks.HasValue,
                    args.Item is not null,
                    args.MediaSourceId);
                return;
            }

            _logger.LogInformation("Realtime Ambilight playback start received for session {SessionId}, item {ItemId}.", sessionId, args.Item.Id);
            SynchroniseBinding(args.Session?.DeviceId, args.Session?.DeviceName);

            _coordinator.TryPost(new PlaybackStarted(
                sessionId,
                Math.Max(0, args.PlaybackPositionTicks.Value),
                new PlaybackSourceReference(args.Item.Id.ToString(), args.MediaSourceId)));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Realtime Ambilight playback start adapter failed.");
            // PlaybackProgress/Start are host request paths: never damage Jellyfin playback.
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs args)
    {
        try
        {
            if (!IsTargetDevice(args.Session?.DeviceId, args.Session?.DeviceName))
            {
                return;
            }

            var sessionId = args.Session?.Id;
            if (string.IsNullOrWhiteSpace(sessionId) || !args.PlaybackPositionTicks.HasValue)
            {
                return;
            }

            _logger.LogDebug("Realtime Ambilight playback progress received for session {SessionId}.", sessionId);

            PlaybackSourceReference? source = args.Item is null || string.IsNullOrWhiteSpace(args.MediaSourceId)
                ? null
                : new PlaybackSourceReference(args.Item.Id.ToString(), args.MediaSourceId);
            _coordinator.TryPost(new PlaybackProgressed(
                sessionId,
                Math.Max(0, args.PlaybackPositionTicks.Value),
                args.IsPaused,
                args.IsAutomated,
                source));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Realtime Ambilight playback progress adapter failed.");
            // See OnPlaybackStart: this callback must not throw into Jellyfin.
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs args)
    {
        try
        {
            if (!IsTargetDevice(args.Session?.DeviceId, args.Session?.DeviceName))
            {
                return;
            }

            var sessionId = args.Session?.Id;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                _coordinator.TryPost(new PlaybackStopped(sessionId));
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Realtime Ambilight playback stop adapter failed.");
            // See OnPlaybackStart: this callback must not throw into Jellyfin.
        }
    }

    private void Unsubscribe()
    {
        if (Interlocked.Exchange(ref _subscribed, 0) == 1)
        {
            _coordinator.AnalysisFailed -= OnAnalysisFailed;
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        }
    }

    /// <summary>
    /// Decides whether a playback event belongs to the bound device. An empty
    /// binding accepts every device. Otherwise the device id is authoritative,
    /// with the display name as a fallback: one physical television was observed
    /// reporting three different ids, and the id carried by a live session need
    /// not be listed in /Devices at all.
    /// </summary>
    /// <summary>
    /// Repairs a bound device whose id or name has drifted. A television was
    /// observed under three different ids, and a stale settings page can save an
    /// empty name over a good one; either alone would silently stop matching.
    /// Once an event has matched, both fields are known good, so writing them
    /// back makes the binding converge instead of decaying.
    /// </summary>
    private void SynchroniseBinding(string? deviceId, string? deviceName)
    {
        var plugin = Plugin.Instance;
        var configuration = plugin?.Configuration;
        if (plugin is null
            || configuration is null
            || string.IsNullOrWhiteSpace(deviceId)
            || string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }

        // An unbound plugin follows every device and must stay that way.
        if (string.IsNullOrWhiteSpace(configuration.TargetDeviceId) && string.IsNullOrWhiteSpace(configuration.TargetDeviceName))
        {
            return;
        }

        if (string.Equals(configuration.TargetDeviceId, deviceId, StringComparison.Ordinal)
            && string.Equals(configuration.TargetDeviceName, deviceName, StringComparison.Ordinal))
        {
            return;
        }

        _logger.LogInformation(
            "Realtime Ambilight refreshed its device binding to {DeviceName} ({DeviceId}).",
            deviceName,
            deviceId);

        configuration.TargetDeviceId = deviceId;
        configuration.TargetDeviceName = deviceName;
        plugin.SaveConfiguration();
    }

    private static bool IsTargetDevice(string? deviceId, string? deviceName)
    {
        var configuration = Plugin.Instance?.Configuration;
        var targetDeviceId = configuration?.TargetDeviceId;
        var targetDeviceName = configuration?.TargetDeviceName;

        if (string.IsNullOrWhiteSpace(targetDeviceId) && string.IsNullOrWhiteSpace(targetDeviceName))
        {
            return true;
        }

        return (!string.IsNullOrWhiteSpace(targetDeviceId) && string.Equals(targetDeviceId, deviceId, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(targetDeviceName) && string.Equals(targetDeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
    }
}
