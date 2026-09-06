using System.Threading.Channels;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;

/// <summary>
/// Owns the one v1 Ambilight session and serializes playback decisions away from
/// Jellyfin's event thread. Event handlers only enqueue data; decoder lifetime is
/// guarded so a replacement can never overlap its predecessor.
/// </summary>
public sealed class PlaybackEventCoordinator : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Channel<PlaybackEvent> _events = Channel.CreateUnbounded<PlaybackEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly IPlaybackAnalysisWorker _worker;
    private readonly PlaybackCoordinatorOptions _options;
    private readonly PlaybackClock _clock;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _workerGate = new(1, 1);
    private readonly Task _eventLoop;
    private CancellationTokenSource? _workerCancellation;
    private CancellationTokenSource? _seekCancellation;
    private Task? _workerTask;
    private string? _activeSessionId;
    private PlaybackSourceReference? _source;
    private long _generation;
    private bool _isPaused;
    private bool _disposed;

    public PlaybackEventCoordinator(
        IPlaybackAnalysisWorker worker,
        IMonotonicTime monotonicTime,
        PlaybackCoordinatorOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _options = options ?? new PlaybackCoordinatorOptions();
        _options.Validate();
        _clock = new PlaybackClock(monotonicTime ?? throw new ArgumentNullException(nameof(monotonicTime)));
        _timeProvider = timeProvider ?? TimeProvider.System;
        LatestFrames = new LatestFrameBuffer<AnalysisFrame>();
        _eventLoop = Task.Run(EventLoopAsync);
    }

    /// <summary>Frames published by the worker; an output scheduler takes only the newest.</summary>
    public LatestFrameBuffer<AnalysisFrame> LatestFrames { get; }

    public string? ActiveSessionId
    {
        get
        {
            lock (_sync)
            {
                return _activeSessionId;
            }
        }
    }

    public long CurrentPositionTicks
    {
        get
        {
            lock (_sync)
            {
                return _clock.PositionTicks;
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_sync)
            {
                return _isPaused;
            }
        }
    }

    /// <summary>
    /// Non-blocking event-thread entry point. It never starts, stops, or awaits
    /// a decoder; false means disposal has already begun.
    /// </summary>
    public bool TryPost(PlaybackEvent playbackEvent)
    {
        ArgumentNullException.ThrowIfNull(playbackEvent);
        if (string.IsNullOrWhiteSpace(playbackEvent.SessionId))
        {
            throw new ArgumentException("A playback event needs a session identifier.", nameof(playbackEvent));
        }

        lock (_sync)
        {
            return !_disposed && _events.Writer.TryWrite(playbackEvent);
        }
    }

    private async Task EventLoopAsync()
    {
        try
        {
            await foreach (var playbackEvent in _events.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                Handle(playbackEvent);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Disposal owns and awaits the active worker below.
        }
    }

    private void Handle(PlaybackEvent playbackEvent)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            switch (playbackEvent)
            {
                case PlaybackStarted started:
                    HandleStarted(started);
                    break;
                case PlaybackProgressed progressed:
                    HandleProgressed(progressed);
                    break;
                case PlaybackStopped stopped:
                    HandleStopped(stopped);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(playbackEvent));
            }
        }
    }

    private void HandleStarted(PlaybackStarted started)
    {
        ValidatePosition(started.PositionTicks);
        if (_activeSessionId is not null)
        {
            // ADR-002: first active session wins, including against a second start.
            return;
        }

        _activeSessionId = started.SessionId;
        _source = started.Source;
        _isPaused = false;
        _clock.Start(started.PositionTicks);
        ScheduleWorkerStart(started.SessionId, started.PositionTicks, _source);
    }

    private void HandleProgressed(PlaybackProgressed progressed)
    {
        ValidatePosition(progressed.PositionTicks);
        if (!string.Equals(_activeSessionId, progressed.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        _source = progressed.Source ?? _source;

        // Server-generated progress is liveness-only: never correct from it.
        if (progressed.IsAutomated)
        {
            return;
        }

        if (progressed.IsPaused)
        {
            if (!_isPaused)
            {
                _isPaused = true;
                _clock.Pause(progressed.PositionTicks);
                CancelSeekAndWorker();
            }

            return;
        }

        if (_seekCancellation is not null)
        {
            // Scrubbing reports a sequence of discontinuities. Restart its window
            // from the newest target instead of accidentally treating it as resume.
            BeginSeekDebounce(progressed.SessionId, progressed.PositionTicks);
            return;
        }

        if (_isPaused)
        {
            _isPaused = false;
            _clock.Start(progressed.PositionTicks);
            ScheduleWorkerStart(progressed.SessionId, progressed.PositionTicks, _source);
            return;
        }

        if (!_clock.IsRunning)
        {
            _clock.Start(progressed.PositionTicks);
            ScheduleWorkerStart(progressed.SessionId, progressed.PositionTicks, _source);
            return;
        }

        var drift = Math.Abs(progressed.PositionTicks - _clock.PositionTicks);
        if (drift <= _options.DriftTolerance.Ticks)
        {
            return;
        }

        if (drift < _options.SeekDiscontinuityThreshold.Ticks)
        {
            // A small correction avoids a costly decoder restart for normal client rounding.
            _clock.Resynchronize(progressed.PositionTicks, isRunning: true);
            return;
        }

        BeginSeekDebounce(progressed.SessionId, progressed.PositionTicks);
    }

    private void HandleStopped(PlaybackStopped stopped)
    {
        if (!string.Equals(_activeSessionId, stopped.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        _activeSessionId = null;
        _source = null;
        _isPaused = false;
        _clock.Pause();
        CancelSeekAndWorker();
        LatestFrames.Clear();
    }

    /// <summary>
    /// Restarts the analysis decoder at the current playback position.
    /// </summary>
    /// <remarks>
    /// Output calls this when frames keep arriving after the moment they should
    /// have been shown. A decoder that has lost its lead never regains it on its
    /// own -- it runs at playback speed, not faster -- so without this the error
    /// grows without bound, and the LEDs end up showing an earlier scene
    /// entirely. Returns false when there is nothing to restart.
    /// </remarks>
    public bool RequestAnalysisResync()
    {
        lock (_sync)
        {
            if (_disposed || _isPaused || _activeSessionId is null || !_clock.IsRunning)
            {
                return false;
            }

            BeginSeekDebounce(_activeSessionId, _clock.PositionTicks);
            return true;
        }
    }

    private void BeginSeekDebounce(string sessionId, long positionTicks)
    {
        _clock.Pause(); // Output holds its last frame while the future output driver keepalives it.
        CancelSeekAndWorker();
        var generation = ++_generation;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _seekCancellation = cancellation;
        _ = RestartAfterDebounceAsync(sessionId, positionTicks, generation, cancellation);
    }

    private async Task RestartAfterDebounceAsync(string sessionId, long positionTicks, long generation, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_options.SeekDebounce, _timeProvider, cancellation.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (_disposed || generation != _generation || !string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
                {
                    return;
                }

                if (!ReferenceEquals(_seekCancellation, cancellation))
                {
                    return;
                }

                _seekCancellation = null;
                _clock.Start(positionTicks);
                ScheduleWorkerStart(sessionId, positionTicks, _source);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer seek, pause, stop, or disposal superseded this request.
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_seekCancellation, cancellation))
                {
                    _seekCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private void ScheduleWorkerStart(string sessionId, long positionTicks, PlaybackSourceReference? source)
    {
        CancelWorker();
        var generation = ++_generation;
        _ = StartWorkerWhenSafeAsync(sessionId, positionTicks, source, generation);
    }

    private async Task StartWorkerWhenSafeAsync(string sessionId, long positionTicks, PlaybackSourceReference? source, long generation)
    {
        try
        {
            await _workerGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                Task? previousWorker;
                lock (_sync)
                {
                    previousWorker = _workerTask;
                    _workerCancellation?.Cancel();
                }

                if (previousWorker is not null)
                {
                    await ObserveWorkerAsync(previousWorker).ConfigureAwait(false);
                }

                lock (_sync)
                {
                    if (_disposed || generation != _generation || !string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal) || !_clock.IsRunning)
                    {
                        return;
                    }

                    _workerCancellation?.Dispose();
                    _workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    var request = new PlaybackWorkerRequest(sessionId, positionTicks, source);
                    _workerTask = RunWorkerAsync(request, _workerCancellation.Token);
                }
            }
            finally
            {
                _workerGate.Release();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Disposal prevents any queued replacement from starting.
        }
    }

    private async Task RunWorkerAsync(PlaybackWorkerRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _worker.RunAsync(request, LatestFrames, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected lifecycle cancellation.
        }
        catch (Exception exception)
        {
            // Never let a background analysis failure tear down Jellyfin's event
            // handling -- but never swallow it silently either. Reporting is the
            // host's job; a failure that is only visible as "the LEDs stay dark"
            // costs hours to diagnose, because FFmpeg's own stderr is carried on
            // this exception and is lost with it.
            ReportAnalysisFailure(exception);
        }
    }

    /// <summary>
    /// Raised when an analysis worker fails. The host subscribes to log it; the
    /// coordinator stays free of any logging dependency.
    /// </summary>
    public event Action<Exception>? AnalysisFailed;

    private void ReportAnalysisFailure(Exception exception)
    {
        try
        {
            AnalysisFailed?.Invoke(exception);
        }
        catch
        {
            // A faulty subscriber must not escalate into the event loop.
        }
    }

    private void CancelSeekAndWorker()
    {
        _seekCancellation?.Cancel();
        _seekCancellation = null;
        ++_generation;
        CancelWorker();
    }

    private void CancelWorker() => _workerCancellation?.Cancel();

    private static async Task ObserveWorkerAsync(Task worker)
    {
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Worker contracts use cancellation for normal teardown.
        }
    }

    private static void ValidatePosition(long positionTicks)
    {
        if (positionTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(positionTicks), positionTicks, "Playback position cannot be negative.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeSessionId = null;
            ++_generation;
            _seekCancellation?.Cancel();
            CancelWorker();
            _events.Writer.TryComplete();
            _shutdown.Cancel();
        }

        await _eventLoop.ConfigureAwait(false);
        await _workerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Task? worker;
            lock (_sync)
            {
                CancelWorker();
                worker = _workerTask;
            }

            if (worker is not null)
            {
                await ObserveWorkerAsync(worker).ConfigureAwait(false);
            }
        }
        finally
        {
            _workerGate.Release();
            _workerCancellation?.Dispose();
            _seekCancellation?.Dispose();
            _shutdown.Dispose();
            _workerGate.Dispose();
        }
    }
}
