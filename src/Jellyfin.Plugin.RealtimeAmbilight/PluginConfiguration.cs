using MediaBrowser.Model.Plugins;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;
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

    /// <summary>
    /// Display name of the bound device, matched as a fallback when the id does
    /// not. The Jellyfin Android TV client was observed reporting three different
    /// device ids for one physical television across reinstalls and logins, and a
    /// live session's id need not appear in /Devices at all, so an id-only
    /// binding silently stops working. The name survives those changes.
    /// </summary>
    public string TargetDeviceName { get; set; } = string.Empty;

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

    /// <summary>
    /// Holds the last frame on the LEDs for as long as playback stays paused.
    /// Resuming continues normally and stopping still runs the configured fade.
    /// When disabled, output simply ceases on pause and WLED reclaims the strip
    /// after its own realtime timeout.
    /// </summary>
    public bool HoldWhilePaused { get; set; } = true;

    /// <summary>
    /// Retained so existing serialized configurations keep deserializing; this
    /// root is additive-only. It is no longer used: it was a resend interval
    /// rather than a hold duration, and any value above WLED's realtime timeout
    /// made the LEDs drop out and back mid-pause. The resend cadence is now a
    /// fixed interval derived from that timeout, and holding is a plain switch.
    /// </summary>
    public int PauseKeepAliveSeconds { get; set; } = 2;

    public int StopFadeMilliseconds { get; set; } = 250;

    /// <summary>
    /// Fraction of each axis, as a percentage, sampled inward from every edge.
    /// Ten percent is the recommended setting: it takes in enough of the picture
    /// to be stable without letting the centre of the image dominate an edge.
    /// </summary>
    public int SamplingDepthPercent { get; set; } = EdgeSampler.DefaultDepthPercent;

    /// <summary>
    /// Sends byte values proportional to light output, which is what a
    /// controller needs when it drives its LEDs straight from the byte. WLED
    /// skips gamma correction for realtime data by default, so leaving this on
    /// is almost always right; turn it off only if gamma correction for
    /// realtime is enabled on the controller itself.
    /// </summary>
    public bool CorrectLedGamma { get; set; } = true;

    /// <summary>
    /// Detects letterbox and pillarbox bars and samples the picture inside them.
    /// Without it the LEDs follow the bars and stay dark on a wider-than-16:9 film.
    /// </summary>
    public bool IgnoreBlackBorders { get; set; } = true;

    public int AnalysisWidth { get; set; } = 160;

    /// <summary>
    /// Derived from <see cref="AnalysisWidth"/> at 16:9 by the settings page.
    /// Kept serialized for backwards compatibility with existing configuration.
    /// </summary>
    public int AnalysisHeight { get; set; } = 90;

    public int AnalysisFramesPerSecond { get; set; } = 30;
}
