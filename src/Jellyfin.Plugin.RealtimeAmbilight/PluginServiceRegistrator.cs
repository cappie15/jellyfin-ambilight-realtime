using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Diagnostics;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using Jellyfin.Plugin.RealtimeAmbilight.Hue;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>Registers the lifecycle without starting work during plugin construction.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PluginActivityLog>();
        serviceCollection.AddSingleton<IMonotonicTime, StopwatchMonotonicTime>();
        serviceCollection.AddSingleton<IFfmpegAnalysisSourceResolver, JellyfinFfmpegAnalysisSourceResolver>();
        serviceCollection.AddSingleton<IPlaybackAnalysisWorker, FfmpegAnalysisWorker>();
        serviceCollection.AddSingleton<PlaybackEventCoordinator>();
        serviceCollection.AddSingleton<WledDiscoveryService>();
        serviceCollection.AddSingleton<JellyfinPlaybackEventAdapter>();
        serviceCollection.AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredService<JellyfinPlaybackEventAdapter>());
        serviceCollection.AddSingleton<JellyfinWledOutputService>();
        serviceCollection.AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredService<JellyfinWledOutputService>());

        // Hue Entertainment: independent of everything above except the
        // shared PlaybackEventCoordinator (same bound device, same decoded
        // frames) and, for HueEntertainmentService, its own hosted-service
        // slot -- never the WLED output pump's.
        serviceCollection.AddSingleton(_ => new HueCredentialStore(
            Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "jellyfin-realtime-ambilight-hue")));
        serviceCollection.AddSingleton<IHueCredentialStore>(serviceProvider => serviceProvider.GetRequiredService<HueCredentialStore>());
        serviceCollection.AddSingleton(serviceProvider => new HueBridgeClient(
            serviceProvider.GetRequiredService<ILogger<HueBridgeClient>>()));
        serviceCollection.AddSingleton<IHueBridgeClient>(serviceProvider => serviceProvider.GetRequiredService<HueBridgeClient>());
        serviceCollection.AddSingleton(serviceProvider => new HueLightControl(
            serviceProvider.GetRequiredService<ILogger<HueLightControl>>()));
        serviceCollection.AddSingleton<IHueLightControl>(serviceProvider => serviceProvider.GetRequiredService<HueLightControl>());
        serviceCollection.AddSingleton<IHueStreamChannelFactory, HueDtlsChannelFactory>();
        serviceCollection.AddSingleton<HueBridgeDiscoveryService>();
        serviceCollection.AddSingleton<HueEntertainmentService>();
        serviceCollection.AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredService<HueEntertainmentService>());
    }
}
