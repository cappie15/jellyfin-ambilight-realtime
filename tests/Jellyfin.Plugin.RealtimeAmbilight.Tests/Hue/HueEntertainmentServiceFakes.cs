using System.Collections.Concurrent;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Model;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using Jellyfin.Plugin.RealtimeAmbilight.Hue;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Hue;

/// <summary>Never actually analyses anything -- the tests in this namespace only drive the coordinator's session/pause bookkeeping, never real frames.</summary>
internal sealed class NoOpAnalysisWorker : IPlaybackAnalysisWorker
{
    public async Task RunAsync(PlaybackWorkerRequest request, FanOutFrameBuffer<Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding.AnalysisFrame> latestFrames, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}

internal sealed class FakeHueBridgeClient : IHueBridgeClient
{
    public IReadOnlyList<HueEntertainmentConfiguration> Configurations { get; set; } = [];

    public IReadOnlyDictionary<Guid, Guid> LightIdsByServiceId { get; set; } = new Dictionary<Guid, Guid>();

    public Task<IReadOnlyList<HueEntertainmentConfiguration>> GetEntertainmentConfigurationsAsync(
        string host, string expectedCertificateThumbprintSha256, string applicationKey, CancellationToken cancellationToken)
        => Task.FromResult(Configurations);

    public Task<IReadOnlyDictionary<Guid, Guid>> ResolveLightIdsAsync(
        string host, string expectedCertificateThumbprintSha256, string applicationKey, CancellationToken cancellationToken)
        => Task.FromResult(LightIdsByServiceId);
}

internal sealed class FakeHueLightControl : IHueLightControl
{
    public Dictionary<Guid, HueLightSnapshotEntry> StateByLightId { get; } = new();

    public ConcurrentQueue<Guid> TurnedOnLightIds { get; } = new();

    public ConcurrentQueue<Guid> WarmWhiteDimmedLightIds { get; } = new();

    public ConcurrentQueue<HueLightSnapshotEntry> RestoredEntries { get; } = new();

    public Task<HueLightSnapshotEntry?> ReadStateAsync(string host, string certificateThumbprint, string applicationKey, Guid lightId, CancellationToken cancellationToken)
        => Task.FromResult(StateByLightId.TryGetValue(lightId, out var entry) ? entry : null);

    public Task TurnOnAsync(string host, string certificateThumbprint, string applicationKey, Guid lightId, CancellationToken cancellationToken)
    {
        TurnedOnLightIds.Enqueue(lightId);
        return Task.CompletedTask;
    }

    public Task ApplyWarmWhiteDimAsync(string host, string certificateThumbprint, string applicationKey, Guid lightId, CancellationToken cancellationToken)
    {
        WarmWhiteDimmedLightIds.Enqueue(lightId);
        return Task.CompletedTask;
    }

    public Task RestoreAsync(string host, string certificateThumbprint, string applicationKey, HueLightSnapshotEntry entry, CancellationToken cancellationToken)
    {
        RestoredEntries.Enqueue(entry);
        return Task.CompletedTask;
    }
}

internal sealed class FakeHueCredentialStore : IHueCredentialStore
{
    public HueCredentials? Credentials { get; set; }

    public Task<HueCredentials?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Credentials);
}

/// <summary>One connect attempt's worth of fake channel behaviour -- a fresh instance per <see cref="FakeHueStreamChannelFactory.Create"/> call, mirroring the single-use real channel.</summary>
internal sealed class FakeHueStreamChannel : IHueStreamChannel
{
    public bool ConnectSucceeds { get; set; } = true;

    /// <summary>Reproduces the real HueApi.Entertainment double-close bug this service works around -- see <see cref="HueEntertainmentService"/>'s StopAsync remarks.</summary>
    public bool ThrowOnDispose { get; set; }

    public ConcurrentQueue<byte[]> SentPackets { get; } = new();

    public bool Disposed { get; private set; }

    public Task<bool> TryConnectAsync(Guid entertainmentConfigurationId, TimeSpan timeout, CancellationToken cancellationToken)
        => Task.FromResult(ConnectSucceeds);

    public void SendPacket(byte[] packet) => SentPackets.Enqueue(packet);

    public void Close()
    {
        ObjectDisposedException.ThrowIf(ThrowOnDispose, this);
    }

    public void Dispose()
    {
        Disposed = true;
        ObjectDisposedException.ThrowIf(ThrowOnDispose, this);
    }
}

internal sealed class FakeHueStreamChannelFactory : IHueStreamChannelFactory
{
    private readonly ConcurrentQueue<FakeHueStreamChannel> _pending = new();

    public ConcurrentQueue<FakeHueStreamChannel> Created { get; } = new();

    /// <summary>Queues the channel the next <see cref="Create"/> call returns; falls back to a default connecting channel once the queue is empty.</summary>
    public void Enqueue(FakeHueStreamChannel channel) => _pending.Enqueue(channel);

    public IHueStreamChannel Create(string bridgeHost, string applicationKey, string clientKey)
    {
        var channel = _pending.TryDequeue(out var queued) ? queued : new FakeHueStreamChannel();
        Created.Enqueue(channel);
        return channel;
    }
}
