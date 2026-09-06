#pragma warning disable CA1848, CA1873
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
    private readonly WledRealtimeOutput _output;
    private readonly LatestFrameOutputScheduler _scheduler;
    private readonly ILogger<JellyfinWledOutputService> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _pump;

    public JellyfinWledOutputService(PlaybackEventCoordinator coordinator, ILogger<JellyfinWledOutputService> logger)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var physical = new LedLayout(
            Math.Max(1, configuration.TopLedCount),
            Math.Max(1, configuration.RightLedCount),
            Math.Max(1, configuration.BottomLedCount),
            Math.Max(1, configuration.LeftLedCount));
        var logical = LogicalSamplingLayout.FromPhysicalLayout(physical);
        var processor = new AmbilightFrameProcessor(physical, logical, _ => default);
        _output = new WledRealtimeOutput(
            new WledEndpoint(configuration.WledHost, Math.Clamp(configuration.WledHttpPort, 1, ushort.MaxValue)),
            configuration.RealtimeProtocol,
            new UdpDatagramSender());
        _scheduler = new LatestFrameOutputScheduler(_coordinator.LatestFrames, processor, _output);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pump = Task.Run(RunPumpAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunPumpAsync()
    {
        var lastKeepAlive = DateTimeOffset.UtcNow;
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

                previousSession = currentSession;
                try
                {
                    if (_scheduler.TryTakeProcessedFrame(out var rgb24Frame) && rgb24Frame is not null)
                    {
                        if (!loggedProcessedFrame)
                        {
                            _logger.LogInformation("Realtime Ambilight processed its first decoded frame.");
                            loggedProcessedFrame = true;
                        }

                        var delay = Math.Clamp(Plugin.Instance?.Configuration.OutputDelayMilliseconds ?? 250, 0, 2000);
                        pendingFrames.Enqueue((DateTimeOffset.UtcNow.AddMilliseconds(delay), rgb24Frame));
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
                        lastKeepAlive = now;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "Realtime Ambilight output pump failed while processing a frame.");
                    continue;
                }
                var keepAliveSeconds = Math.Clamp(Plugin.Instance?.Configuration.PauseKeepAliveSeconds ?? 2, 1, 20);
                if (pendingFrames.Count == 0 && currentSession is not null && _coordinator.IsPaused && DateTimeOffset.UtcNow - lastKeepAlive >= TimeSpan.FromSeconds(keepAliveSeconds))
                {
                    await _output.KeepAliveAsync(_shutdown.Token).ConfigureAwait(false);
                    lastKeepAlive = DateTimeOffset.UtcNow;
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
