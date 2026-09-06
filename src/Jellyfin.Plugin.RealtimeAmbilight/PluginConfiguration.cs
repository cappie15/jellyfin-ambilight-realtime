using MediaBrowser.Model.Plugins;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Wled;

namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>
/// Additive-only configuration root. Future upgrades must back up the serialized
/// configuration before changing <see cref="ConfigSchemaVersion"/>.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    public int ConfigSchemaVersion { get; set; } = 2;

    /// <summary>Master safety switch. Disabled output never takes WLED realtime control.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Jellyfin client device that owns this WLED installation. An empty value
    /// preserves the legacy first-playing-session behaviour until the admin
    /// chooses a device in the configuration page.
    /// </summary>
    public string TargetDeviceId { get; set; } = string.Empty;

    public string WledHost { get; set; } = "10.0.0.8";

    public int WledHttpPort { get; set; } = 80;

    public WledRealtimeProtocol RealtimeProtocol { get; set; } = WledRealtimeProtocol.Auto;

    public int TopLedCount { get; set; } = 265;

    public int RightLedCount { get; set; } = 150;

    public int BottomLedCount { get; set; } = 266;

    public int LeftLedCount { get; set; } = 150;

    /// <summary>
    /// Delays LED output to compensate for a client display pipeline that lags
    /// the server-side decoder. Positive values make the LEDs react later.
    /// Starts at zero: it is raised only when the LEDs are demonstrably ahead of
    /// the picture on the calibrated display.
    /// </summary>
    public int OutputDelayMilliseconds { get; set; }

    public int OutputFramesPerSecond { get; set; } = 30;

    public int PauseKeepAliveSeconds { get; set; } = 2;

    public int StopFadeMilliseconds { get; set; } = 250;

    public int AnalysisWidth { get; set; } = 160;

    /// <summary>
    /// Derived from <see cref="AnalysisWidth"/> at 16:9 by the settings page.
    /// Kept serialized for backwards compatibility with existing configuration.
    /// </summary>
    public int AnalysisHeight { get; set; } = 90;

    public int AnalysisFramesPerSecond { get; set; } = 30;
}
