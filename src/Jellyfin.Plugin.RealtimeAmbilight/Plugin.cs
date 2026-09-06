using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>
/// Jellyfin's plugin entry point. Runtime services are registered separately so
/// constructing this object never starts media work or touches playback.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Realtime Ambilight";

    public override Guid Id => Guid.Parse("7d6d91ed-0f36-46ea-9868-9623283b6b51");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "Realtime Ambilight",
            DisplayName = Name,
            EmbeddedResourcePath = "Jellyfin.Plugin.RealtimeAmbilight.Configuration.Web.config.html",
        };
        yield return new PluginPageInfo
        {
            Name = "RealtimeAmbilight.js",
            DisplayName = Name,
            EmbeddedResourcePath = "Jellyfin.Plugin.RealtimeAmbilight.Configuration.Web.config.js",
        };
    }
}
