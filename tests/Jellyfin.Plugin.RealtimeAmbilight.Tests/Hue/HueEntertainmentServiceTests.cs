using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Model;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using Jellyfin.Plugin.RealtimeAmbilight.Hue;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Hue;

/// <summary>
/// Exercises <see cref="HueEntertainmentService"/>'s lifecycle against fakes
/// for every network-touching collaborator -- no real bridge, no real DTLS
/// handshake -- covering exactly the bugs found and fixed against real
/// hardware this project: turn-on-if-off, the end-of-session
/// restore/warm-white-dim split, the lifecycle double-close race, and an
/// unresolvable light id being skipped rather than crashing the pump.
/// </summary>
public sealed class HueEntertainmentServiceTests : IDisposable
{
    private readonly PluginConfiguration _configuration;
    private readonly List<PlaybackEventCoordinator> _coordinators = [];
    private readonly List<HueEntertainmentService> _services = [];

    public HueEntertainmentServiceTests()
    {
        _configuration = HueTestPluginBootstrap.EnsurePlugin();
        _configuration.ResetHueConfiguration();
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        foreach (var coordinator in _coordinators)
        {
            coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task DisabledStaysUnpairedEvenWithASessionPlaying()
    {
        var (service, _, _, _, _) = CreateHarness(out var coordinator);
        _configuration.HueEnabled = false;
        await service.StartAsync(CancellationToken.None);

        coordinator.TryPost(new PlaybackStarted("session-1", 0));

        await Task.Delay(150); // Give the 40 ms pump several ticks to (not) act.
        Assert.Equal(HueEntertainmentState.Unpaired, service.GetStatus().State);
    }

    [Fact]
    public async Task EnabledWithoutCredentialsStaysUnpaired()
    {
        var (service, _, _, credentialStore, _) = CreateHarness(out var coordinator);
        _configuration.HueEnabled = true;
        _configuration.HueEntertainmentConfigurationId = Guid.NewGuid();
        credentialStore.Credentials = null;
        await service.StartAsync(CancellationToken.None);

        coordinator.TryPost(new PlaybackStarted("session-1", 0));

        await Task.Delay(150);
        Assert.Equal(HueEntertainmentState.Unpaired, service.GetStatus().State);
    }

    [Fact]
    public async Task ConnectTurnsOnALightTheSnapshotFoundOffBeforeStreamingStarts()
    {
        var lightId = Guid.NewGuid();
        var (service, bridge, lightControl, credentialStore, channelFactory) = CreateHarness(out var coordinator);
        var configurationId = ConfigureSinglePairedLight(bridge, lightId);
        lightControl.StateByLightId[lightId] = new HueLightSnapshotEntry(lightId, On: false, null, null, null, null, null);
        credentialStore.Credentials = new HueCredentials("bridge-1", "app-key", "client-key", "thumb");
        _configuration.HueEnabled = true;
        _configuration.HueEntertainmentConfigurationId = configurationId;
        await service.StartAsync(CancellationToken.None);

        coordinator.TryPost(new PlaybackStarted("session-1", 0));
        await WaitForStateAsync(service, HueEntertainmentState.Streaming);

        Assert.Single(channelFactory.Created);
        Assert.Equal(lightId, Assert.Single(lightControl.TurnedOnLightIds));
    }

    [Fact]
    public async Task ConnectDoesNotTurnOnALightThatWasAlreadyOn()
    {
        var lightId = Guid.NewGuid();
        var (service, bridge, lightControl, credentialStore, _) = CreateHarness(out var coordinator);
        var configurationId = ConfigureSinglePairedLight(bridge, lightId);
        lightControl.StateByLightId[lightId] = new HueLightSnapshotEntry(lightId, On: true, null, null, null, null, null);
        credentialStore.Credentials = new HueCredentials("bridge-1", "app-key", "client-key", "thumb");
        _configuration.HueEnabled = true;
        _configuration.HueEntertainmentConfigurationId = configurationId;
        await service.StartAsync(CancellationToken.None);

        coordinator.TryPost(new PlaybackStarted("session-1", 0));
        await WaitForStateAsync(service, HueEntertainmentState.Streaming);

        Assert.Empty(lightControl.TurnedOnLightIds);
    }

    [Fact]
    public async Task EndOfSessionRestoresThePreviousStateWhenConfiguredTo()
    {
        var lightId = Guid.NewGuid();
        var (service, bridge, lightControl, credentialStore, _) = CreateHarness(out var coordinator);
        var configurationId = ConfigureSinglePairedLight(bridge, lightId);
        var previousState = new HueLightSnapshotEntry(lightId, On: true, 42d, "xy", (0.3, 0.4), null, null);
        lightControl.StateByLightId[lightId] = previousState;
        credentialStore.Credentials = new HueCredentials("bridge-1", "app-key", "client-key", "thumb");
        _configuration.HueEnabled = true;
        _configuration.HueEntertainmentConfigurationId = configurationId;
        _configuration.HueEndBehaviour = HueEndBehaviour.RestorePreviousState;
        await service.StartAsync(CancellationToken.None);

        coordinator.TryPost(new PlaybackStarted("session-1", 0));
        await WaitForStateAsync(service, HueEntertainmentState.Streaming);

        coordinator.TryPost(new PlaybackStopped("session-1"));
        await WaitForStateAsync(service, HueEntertainmentState.Ready);

        var restored = Assert.Single(lightControl.RestoredEntries);
        Assert.Equal(lightId, restored.LightId);
        Assert.Empty(lightControl.WarmWhiteDimmedLightIds);
    }

    [Fact]
    public async Task EndOfSessionAppliesWarmWhiteDimByDefault()
    {
        var lightId = Guid.NewGuid();
        var (service, bridge, lightControl, credentialStore, _) = CreateHarness(out var coordinator);
        var configurationId = ConfigureSinglePairedLight(bridge, lightId);
        lightControl.StateByLightId[lightId] = new HueLightSnapshotEntry(lightId, On: true, null, null, null, null, null);
        credentialStore.Credentials = new HueCredentials("bridge-1", "app-key", "client-key", "thumb");
        _configuration.HueEnabled = true;
        _configuration.HueEntertainmentConfigurationId = configurationId;
        Assert.Equal(HueEndBehaviour.WarmWhiteDim, _configuration.HueEndBehaviour); // the default

        await service.StartAsync(CancellationToken.None);
        coordinator.TryPost(new PlaybackStarted("session-1", 0));
        await WaitForStateAsync(service, HueEntertainmentState.Streaming);

        coordinator.TryPost(new PlaybackStopped("session-1"));
        await WaitForStateAsync(service, HueEntertainmentState.Ready);

        Assert.Equal(lightId, Assert.Single(lightControl.WarmWhiteDimmedLightIds));
        Assert.Empty(lightControl.RestoredEntries);
    }

    [Fact]
    public async Task ReconnectsAfterASessionWhoseChannelThrewOnClose()
    {
        var lightId = Guid.NewGuid();
        var (service, bridge, lightControl, credentialStore, channelFactory) = CreateHarness(out var coordinator);
        var configurationId = ConfigureSinglePairedLight(bridge, lightId);
        lightControl.StateByLightId[lightId] = new HueLightSnapshotEntry(lightId, On: true, null, null, null, null, null);
        credentialStore.Credentials = new HueCredentials("bridge-1", "app-key", "client-key", "thumb");
        _configuration.HueEnabled = true;
        _configuration.HueEntertainmentConfigurationId = configurationId;

        // The known HueApi.Entertainment double-close bug: the real channel's
        // Dispose()/Close() throws ObjectDisposedException every time. This
        // must not leave _lifecycleTask stuck non-null forever -- see
        // HueEntertainmentService.LifecycleTaskIsFree's remarks.
        channelFactory.Enqueue(new FakeHueStreamChannel { ConnectSucceeds = true, ThrowOnDispose = true });
        channelFactory.Enqueue(new FakeHueStreamChannel { ConnectSucceeds = true, ThrowOnDispose = true });

        await service.StartAsync(CancellationToken.None);
        coordinator.TryPost(new PlaybackStarted("session-1", 0));
        await WaitForStateAsync(service, HueEntertainmentState.Streaming);

        coordinator.TryPost(new PlaybackStopped("session-1"));
        await WaitForStateAsync(service, HueEntertainmentState.Ready);

        coordinator.TryPost(new PlaybackStarted("session-2", 0));
        await WaitForStateAsync(service, HueEntertainmentState.Streaming);

        Assert.Equal(2, channelFactory.Created.Count);
    }

    [Fact]
    public async Task AnUnresolvableLightServiceIdIsSkippedNotCrashed()
    {
        var (service, bridge, lightControl, credentialStore, _) = CreateHarness(out var coordinator);
        var configurationId = Guid.NewGuid();
        var unresolvableServiceId = Guid.NewGuid();
        bridge.Configurations =
        [
            new HueEntertainmentConfiguration(
                configurationId,
                "TV room",
                "screen",
                "inactive",
                [new HueEntertainmentChannel(0, new HuePosition(0, 0, 0), [unresolvableServiceId])]),
        ];
        bridge.LightIdsByServiceId = new Dictionary<Guid, Guid>(); // deliberately no mapping at all
        credentialStore.Credentials = new HueCredentials("bridge-1", "app-key", "client-key", "thumb");
        _configuration.HueEnabled = true;
        _configuration.HueEntertainmentConfigurationId = configurationId;

        await service.StartAsync(CancellationToken.None);
        coordinator.TryPost(new PlaybackStarted("session-1", 0));

        await WaitForStateAsync(service, HueEntertainmentState.Streaming);

        Assert.Empty(lightControl.TurnedOnLightIds);

        coordinator.TryPost(new PlaybackStopped("session-1"));
        await WaitForStateAsync(service, HueEntertainmentState.Ready);

        Assert.Empty(lightControl.RestoredEntries);
        Assert.Empty(lightControl.WarmWhiteDimmedLightIds);
    }

    /// <summary>One entertainment configuration with one channel whose sole member resolves to <paramref name="lightId"/>.</summary>
    private static Guid ConfigureSinglePairedLight(FakeHueBridgeClient bridge, Guid lightId)
    {
        var configurationId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();
        bridge.Configurations =
        [
            new HueEntertainmentConfiguration(
                configurationId,
                "TV room",
                "screen",
                "inactive",
                [new HueEntertainmentChannel(0, new HuePosition(0, 0, 0), [serviceId])]),
        ];
        bridge.LightIdsByServiceId = new Dictionary<Guid, Guid> { [serviceId] = lightId };
        return configurationId;
    }

    private (HueEntertainmentService Service, FakeHueBridgeClient Bridge, FakeHueLightControl LightControl, FakeHueCredentialStore CredentialStore, FakeHueStreamChannelFactory ChannelFactory) CreateHarness(out PlaybackEventCoordinator coordinator)
    {
        coordinator = new PlaybackEventCoordinator(new NoOpAnalysisWorker(), new StopwatchMonotonicTime());
        _coordinators.Add(coordinator);
        var bridge = new FakeHueBridgeClient();
        var lightControl = new FakeHueLightControl();
        var credentialStore = new FakeHueCredentialStore();
        var channelFactory = new FakeHueStreamChannelFactory();
        var service = new HueEntertainmentService(
            coordinator,
            credentialStore,
            bridge,
            lightControl,
            NullLogger<HueEntertainmentService>.Instance,
            channelFactory);
        _services.Add(service);
        return (service, bridge, lightControl, credentialStore, channelFactory);
    }

    private static async Task WaitForStateAsync(HueEntertainmentService service, HueEntertainmentState expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        HueEntertainmentState last = default;
        while (DateTime.UtcNow < deadline)
        {
            last = service.GetStatus().State;
            if (last == expected)
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException($"Expected Hue Entertainment state {expected} but it was still {last} after 2s.");
    }
}
