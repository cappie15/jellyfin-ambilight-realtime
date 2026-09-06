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

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs args)
    {
        try
        {
            if (!IsTargetDevice(args.Session?.DeviceId))
            {
                return;
            }

            var sessionId = args.Session?.Id;
            if (string.IsNullOrWhiteSpace(sessionId) || !args.PlaybackPositionTicks.HasValue || args.Item is null || string.IsNullOrWhiteSpace(args.MediaSourceId))
            {
                return;
            }

            _logger.LogInformation("Realtime Ambilight playback start received for session {SessionId}, item {ItemId}.", sessionId, args.Item.Id);

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
            if (!IsTargetDevice(args.Session?.DeviceId))
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
            if (!IsTargetDevice(args.Session?.DeviceId))
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
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        }
    }

    private static bool IsTargetDevice(string? deviceId)
    {
        var targetDeviceId = Plugin.Instance?.Configuration.TargetDeviceId;
        return string.IsNullOrWhiteSpace(targetDeviceId)
            || string.Equals(targetDeviceId, deviceId, StringComparison.Ordinal);
    }
}
