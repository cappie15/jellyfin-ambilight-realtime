using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>Registers the lifecycle without starting work during plugin construction.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IMonotonicTime, StopwatchMonotonicTime>();
        serviceCollection.AddSingleton<IFfmpegAnalysisSourceResolver, JellyfinFfmpegAnalysisSourceResolver>();
        serviceCollection.AddSingleton<IPlaybackAnalysisWorker, FfmpegAnalysisWorker>();
        serviceCollection.AddSingleton<PlaybackEventCoordinator>();
        serviceCollection.AddSingleton<WledDiscoveryService>();
        serviceCollection.AddSingleton<JellyfinPlaybackEventAdapter>();
        serviceCollection.AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredService<JellyfinPlaybackEventAdapter>());
        serviceCollection.AddSingleton<JellyfinWledOutputService>();
        serviceCollection.AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredService<JellyfinWledOutputService>());
    }
}
