using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Hue;

/// <summary>
/// <see cref="HueEntertainmentService"/> reads live settings from the static
/// <see cref="Jellyfin.Plugin.RealtimeAmbilight.Plugin.Instance"/> rather than
/// through an injected configuration seam -- there was no reason to add one
/// until now, since every other caller genuinely runs inside a real Jellyfin
/// host where that singleton always exists. A test process has no such host,
/// so this constructs one real <see cref="Jellyfin.Plugin.RealtimeAmbilight.Plugin"/>
/// (Jellyfin's own <c>BasePlugin&lt;T&gt;</c> only needs two simple
/// abstractions to construct: <see cref="IApplicationPaths"/> and
/// <see cref="IXmlSerializer"/>, confirmed by reading that base class's
/// source rather than guessing) once per test process, so
/// <c>Plugin.Instance.Configuration</c> is a real, mutable object tests can
/// set fields on directly.
/// </summary>
public static class HueTestPluginBootstrap
{
    private static readonly object SyncRoot = new();
    private static bool _initialized;

    /// <summary>Ensures <c>Plugin.Instance</c> exists, then returns its (already-loaded) configuration for a test to mutate.</summary>
    public static PluginConfiguration EnsurePlugin()
    {
        lock (SyncRoot)
        {
            if (!_initialized)
            {
                _ = new Jellyfin.Plugin.RealtimeAmbilight.Plugin(new FakeApplicationPaths(), new FakeXmlSerializer());
                _initialized = true;
            }
        }

        return Jellyfin.Plugin.RealtimeAmbilight.Plugin.Instance!.Configuration;
    }

    /// <summary>Every Hue-related field back to a known baseline, since <c>Plugin.Instance</c> is one shared static across every test in this class.</summary>
    public static void ResetHueConfiguration(this PluginConfiguration configuration)
    {
        configuration.HueEnabled = false;
        configuration.HueBridgeHost = string.Empty;
        configuration.HueBridgeId = string.Empty;
        configuration.HueEntertainmentConfigurationId = Guid.Empty;
        configuration.HueEntertainmentConfigurationName = string.Empty;
        configuration.HueBrightnessPercent = 100;
        configuration.HueResponsePercent = 50;
        configuration.HueEndBehaviour = Core.Hue.HueEndBehaviour.WarmWhiteDim;
    }

    private sealed class FakeApplicationPaths : IApplicationPaths
    {
        private readonly string _root = Directory.CreateTempSubdirectory("realtime-ambilight-tests-").FullName;

        public string ProgramDataPath => _root;

        public string WebPath => _root;

        public string ProgramSystemPath => _root;

        public string DataPath => _root;

        public string ImageCachePath => _root;

        public string PluginsPath => _root;

        public string PluginConfigurationsPath => _root;

        public string LogDirectoryPath => _root;

        public string ConfigurationDirectoryPath => _root;

        public string SystemConfigurationFilePath => Path.Combine(_root, "system.xml");

        public string CachePath => _root;

        public string TempDirectory => _root;

        public string VirtualDataPath => _root;

        public string TrickplayPath => _root;

        public string BackupPath => _root;

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool throwOnError = true)
        {
        }
    }

    /// <summary>
    /// Always fails to deserialize -- <c>BasePlugin&lt;T&gt;.LoadConfiguration</c>
    /// treats that as "no saved configuration yet" and falls back to
    /// <c>new PluginConfiguration()</c>, which is exactly the clean-slate
    /// state each test wants to mutate from.
    /// </summary>
    private sealed class FakeXmlSerializer : IXmlSerializer
    {
        public object DeserializeFromFile(Type type, string path) => throw new NotSupportedException("Test fake: no configuration file exists.");

        public object DeserializeFromBytes(Type type, byte[] buffer) => throw new NotSupportedException("Test fake: no configuration bytes exist.");

        public object DeserializeFromStream(Type type, Stream stream) => throw new NotSupportedException("Test fake: no configuration stream exists.");

        public void SerializeToFile(object obj, string path)
        {
            // No-op: nothing here needs the configuration to actually persist to disk.
        }

        public void SerializeToStream(object obj, Stream stream)
        {
        }
    }
}
