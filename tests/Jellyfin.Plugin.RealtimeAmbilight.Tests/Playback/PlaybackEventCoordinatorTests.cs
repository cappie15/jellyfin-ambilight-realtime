using System.Collections.Concurrent;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Playback;

public class PlaybackEventCoordinatorTests
{
    [Fact]
    public async Task FirstSessionWinsAndEventPostingNeverWaitsForTheWorker()
    {
        var worker = new BlockingWorker();
        await using var coordinator = CreateCoordinator(worker);

        Assert.True(coordinator.TryPost(new PlaybackStarted("first", 0)));
        Assert.True(coordinator.TryPost(new PlaybackStarted("second", 0)));

        await worker.WaitForStartsAsync(1);

        Assert.Equal("first", coordinator.ActiveSessionId);
        Assert.Single(worker.Requests);
        Assert.Equal("first", worker.Requests.Single().SessionId);
    }

    [Fact]
    public async Task PauseCancelsAnalysisAndResumeStartsAtTheReportedPosition()
    {
        var worker = new BlockingWorker();
        await using var coordinator = CreateCoordinator(worker);

        coordinator.TryPost(new PlaybackStarted("session", TimeSpan.FromSeconds(2).Ticks));
        await worker.WaitForStartsAsync(1);

        coordinator.TryPost(new PlaybackProgressed("session", TimeSpan.FromSeconds(3).Ticks, IsPaused: true));
        await worker.WaitForCancellationAsync(1);

        coordinator.TryPost(new PlaybackProgressed("session", TimeSpan.FromSeconds(4).Ticks, IsPaused: false));
        await worker.WaitForStartsAsync(2);

        Assert.Equal(TimeSpan.FromSeconds(4).Ticks, worker.Requests.ElementAt(1).PositionTicks);
    }

    [Fact]
    public async Task AutomatedProgressDoesNotCorrectTheMonotonicClockOrRestartTheWorker()
    {
        var worker = new BlockingWorker();
        await using var coordinator = CreateCoordinator(worker);

        coordinator.TryPost(new PlaybackStarted("session", TimeSpan.FromSeconds(2).Ticks));
        await worker.WaitForStartsAsync(1);
        coordinator.TryPost(new PlaybackProgressed("session", TimeSpan.FromHours(1).Ticks, IsPaused: false, IsAutomated: true));
        await Task.Delay(30);

        Assert.Single(worker.Requests);
        Assert.InRange(coordinator.CurrentPositionTicks, TimeSpan.FromSeconds(2).Ticks, TimeSpan.FromSeconds(3).Ticks);
    }

    [Fact]
    public async Task ScrubCoalescesToOneRestartAtTheLatestPosition()
    {
        var worker = new BlockingWorker();
        await using var coordinator = CreateCoordinator(worker, debounce: TimeSpan.FromMilliseconds(40));

        coordinator.TryPost(new PlaybackStarted("session", 0));
        await worker.WaitForStartsAsync(1);

        coordinator.TryPost(new PlaybackProgressed("session", TimeSpan.FromSeconds(20).Ticks, IsPaused: false));
        await worker.WaitForCancellationAsync(1);
        await Task.Delay(10);
        coordinator.TryPost(new PlaybackProgressed("session", TimeSpan.FromSeconds(30).Ticks, IsPaused: false));
        await worker.WaitForStartsAsync(2);

        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, worker.Requests.ElementAt(1).PositionTicks);
        Assert.Equal(1, worker.MaximumConcurrentRuns);
    }

    [Fact]
    public async Task DisposalWaitsForTheActiveWorkerToObserveCancellation()
    {
        var worker = new BlockingWorker();
        var coordinator = CreateCoordinator(worker);
        coordinator.TryPost(new PlaybackStarted("session", 0));
        await worker.WaitForStartsAsync(1);

        await coordinator.DisposeAsync();

        await worker.WaitForCancellationAsync(1);
        Assert.Null(coordinator.ActiveSessionId);
        Assert.False(coordinator.TryPost(new PlaybackStarted("another-session", 0)));
    }

    private static PlaybackEventCoordinator CreateCoordinator(BlockingWorker worker, TimeSpan? debounce = null)
        => new(
            worker,
            new StopwatchMonotonicTime(),
            new PlaybackCoordinatorOptions
            {
                DriftTolerance = TimeSpan.FromMilliseconds(10),
                SeekDiscontinuityThreshold = TimeSpan.FromMilliseconds(20),
                HardwareSeekDebounce = debounce ?? TimeSpan.FromMilliseconds(10),
                SoftwareSeekDebounce = debounce ?? TimeSpan.FromMilliseconds(10),
                UsesHardwareDecoder = true,
            });

    private sealed class BlockingWorker : IPlaybackAnalysisWorker
    {
        private readonly ConcurrentQueue<PlaybackWorkerRequest> _requests = new();
        private readonly TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancellationCount;
        private int _concurrentRuns;
        private int _maximumConcurrentRuns;

        public PlaybackWorkerRequest[] Requests => _requests.ToArray();

        public int MaximumConcurrentRuns => Volatile.Read(ref _maximumConcurrentRuns);

        public async Task RunAsync(PlaybackWorkerRequest request, FanOutFrameBuffer<AnalysisFrame> latestFrames, CancellationToken cancellationToken)
        {
            _requests.Enqueue(request);
            var active = Interlocked.Increment(ref _concurrentRuns);
            UpdateMaximum(active);
            _changed.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancellationCount);
                _changed.TrySetResult();
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentRuns);
            }
        }

        public async Task WaitForStartsAsync(int count)
            => await WaitForAsync(() => Requests.Length >= count);

        public async Task WaitForCancellationAsync(int count)
            => await WaitForAsync(() => Volatile.Read(ref _cancellationCount) >= count);

        private static async Task WaitForAsync(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!predicate())
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("The coordinator did not complete its expected lifecycle action.");
                }

                await Task.Delay(5);
            }
        }

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var existing = Volatile.Read(ref _maximumConcurrentRuns);
                if (existing >= active || Interlocked.CompareExchange(ref _maximumConcurrentRuns, active, existing) == existing)
                {
                    return;
                }
            }
        }
    }
}
