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
    public int ConfigSchemaVersion { get; set; } = 3;

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
    /// <summary>
    /// Overall LED brightness as a percentage of the picture's own brightness.
    /// WLED's realtime path bypasses its master brightness when "force max
    /// brightness" is enabled, which is its default, so without this an
    /// installation has no way to dim the Ambilight on its own.
    /// </summary>
    public int BrightnessPercent { get; set; } = 100;

    /// <summary>
    /// Colour intensity as a percentage. Above 100 pushes each colour away from
    /// its own luminance, which deepens it without changing how bright it is.
    /// </summary>
    public int SaturationPercent { get; set; } = 100;

    /// <summary>
    /// Per-channel gains as percentages. Used today only by the wizard's
    /// White step (a red/blue push-pull for colour temperature) and kept for
    /// backward compatibility with an existing saved configuration; the
    /// wizard's Red/Green/Blue/Yellow/Cyan/Magenta steps drive the six
    /// <c>Hue*</c>/<c>...Brightness*</c>/<c>...Intensity*</c> properties
    /// below instead -- see <see cref="Core.Color.PerimeterColourTuning"/>.
    /// </summary>
    public int RedGainPercent { get; set; } = 100;

    public int GreenGainPercent { get; set; } = 100;

    public int BlueGainPercent { get; set; } = 100;

    /// <summary>
    /// The colour-tuning wizard's six primary/secondary anchors: how far to
    /// rotate that colour's own hue (degrees, clamped to ±30 -- half the 60°
    /// spacing between anchors, so one colour's correction cannot cross into
    /// its neighbour's), and that colour's own brightness/intensity
    /// (saturation) multipliers. All default to the identity (0°, 100%,
    /// 100%); together they build the <see cref="Core.Color.HueCorrectionCurve"/>
    /// that is now WLED's general colour correction, applied before
    /// brightness/saturation/wall-colour/per-side trims, which are unchanged.
    /// </summary>
    public int RedHueShiftDegrees { get; set; }
    public int RedBrightnessPercent { get; set; } = 100;
    public int RedIntensityPercent { get; set; } = 100;

    public int GreenHueShiftDegrees { get; set; }
    public int GreenBrightnessPercent { get; set; } = 100;
    public int GreenIntensityPercent { get; set; } = 100;

    public int BlueHueShiftDegrees { get; set; }
    public int BlueBrightnessPercent { get; set; } = 100;
    public int BlueIntensityPercent { get; set; } = 100;

    public int YellowHueShiftDegrees { get; set; }
    public int YellowBrightnessPercent { get; set; } = 100;
    public int YellowIntensityPercent { get; set; } = 100;

    public int CyanHueShiftDegrees { get; set; }
    public int CyanBrightnessPercent { get; set; } = 100;
    public int CyanIntensityPercent { get; set; } = 100;

    public int MagentaHueShiftDegrees { get; set; }
    public int MagentaBrightnessPercent { get; set; } = 100;
    public int MagentaIntensityPercent { get; set; } = 100;

    /// <summary>
    /// Below this fraction of picture-edge luminance, a physical LED is driven
    /// fully off instead of a dim, often colour-cast glow. Zero disables it.
    /// </summary>
    public int BlackLevelFloorPercent { get; set; }

    /// <summary>
    /// The apparent paint colour behind the television. A white value means no
    /// correction; a coloured value lets the renderer counter its reflectance
    /// as far as the LEDs' headroom permits.
    /// </summary>
    public string WallColourHex { get; set; } = "#ffffff";

    /// <summary>How much of <see cref="WallColourHex"/> compensation to apply.</summary>
    public int WallColourCorrectionPercent { get; set; } = 100;

    // Per-side trims are intentionally explicit rather than arrays: Jellyfin's
    // XML configuration stays stable, readable and additive across upgrades.
    public int TopBrightnessPercent { get; set; } = 100;
    public int TopRedGainPercent { get; set; } = 100;
    public int TopGreenGainPercent { get; set; } = 100;
    public int TopBlueGainPercent { get; set; } = 100;

    public int RightBrightnessPercent { get; set; } = 100;
    public int RightRedGainPercent { get; set; } = 100;
    public int RightGreenGainPercent { get; set; } = 100;
    public int RightBlueGainPercent { get; set; } = 100;

    public int BottomBrightnessPercent { get; set; } = 100;
    public int BottomRedGainPercent { get; set; } = 100;
    public int BottomGreenGainPercent { get; set; } = 100;
    public int BottomBlueGainPercent { get; set; } = 100;

    public int LeftBrightnessPercent { get; set; } = 100;
    public int LeftRedGainPercent { get; set; } = 100;
    public int LeftGreenGainPercent { get; set; } = 100;
    public int LeftBlueGainPercent { get; set; } = 100;

    public bool CorrectLedGamma { get; set; } = true;

    /// <summary>
    /// Reads the controller's own gamma settings at startup and uses those
    /// instead of <see cref="CorrectLedGamma"/>. WLED reports both the gamma it
    /// applies to colours and whether realtime data is exempt from it, so this
    /// is a fact to be read rather than a preference to be guessed. Falls back
    /// to the manual setting when the controller cannot be reached.
    /// </summary>
    public bool AutoDetectLedGamma { get; set; } = true;

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

    /// <summary>
    /// Lets the settings page write the one WLED setting it can fix directly
    /// (turning off "force max brightness" for realtime data), instead of
    /// requiring a separate login to WLED's own interface. Off by default: the
    /// plugin only ever reads WLED's configuration until an operator opts in.
    /// This never extends to the ABL power budget, which the settings page
    /// only ever displays.
    /// </summary>
    public bool AllowWledControl { get; set; }

    /// <summary>
    /// A physical LED is held at its last colour until a newly sampled colour
    /// has persisted continuously for at least this long, so a single-frame
    /// flash -- a cut, a lens flare, one strobe frame -- cannot snap the wall
    /// to a colour that was never really "in the picture" long enough to read
    /// as such. Zero (the default) disables it: every sampled frame is shown
    /// exactly as sampled, matching every installation before this existed.
    /// </summary>
    public int MinimumColourHoldMilliseconds { get; set; }

    /// <summary>
    /// Eases each LED toward a newly sampled colour over roughly this many
    /// milliseconds instead of jumping straight to it, so a real cut or a
    /// fast pan still reads as reactive while frame-to-frame sampling noise
    /// -- most visible as small "steps" between source and target colour at
    /// low brightness, where the eye is most sensitive to a brightness
    /// change -- gets smoothed away before it ever reaches the temporal
    /// ditherer. Zero (the default) disables it: every sampled frame is
    /// shown exactly as sampled, matching every installation before this
    /// existed. Researched against HyperHDR's own "Infinite Color Engine"
    /// before building this -- see <see cref="Core.Output.WledTemporalSmoother"/>.
    /// </summary>
    public int WledSmoothingMilliseconds { get; set; }

    /// <summary>
    /// Sends RGBW32 instead of RGB24, so a strip with its own white LEDs
    /// reproduces white and near-white tones on that channel instead of
    /// mixing them from red, green and blue. Off by default: not every strip
    /// has a fourth, white LED, and turning this on for one that does not
    /// would map its real fourth channel (if any) or simply be ignored,
    /// wasting one extra DDP packet per frame for nothing. Restart after
    /// changing it, like the other WLED connection settings -- this is a
    /// hardware fact about the strip, not a live-tunable preference.
    /// </summary>
    public bool SendWhiteChannel { get; set; }

    /// <summary>
    /// Master switch for the Hue Entertainment integration. Off by default
    /// and harmless when off: no discovery, no bridge connection, no
    /// Entertainment takeover, and WLED behaves exactly as before this
    /// existed. Turning this on alone does nothing until a bridge is paired
    /// and an entertainment configuration is selected below.
    /// </summary>
    public bool HueEnabled { get; set; }

    /// <summary>The paired bridge's LAN address, set once during pairing.</summary>
    public string HueBridgeHost { get; set; } = string.Empty;

    /// <summary>
    /// The paired bridge's own id (from <c>/api/config</c>'s <c>bridgeid</c>),
    /// used to re-identify a bridge that has moved to a new address rather
    /// than trusting whatever now answers at the last-known one.
    /// </summary>
    public string HueBridgeId { get; set; } = string.Empty;

    /// <summary>
    /// The selected entertainment configuration's id. Chosen from the
    /// bridge's own existing entertainment areas during pairing; this plugin
    /// never creates or edits one itself.
    /// </summary>
    public Guid HueEntertainmentConfigurationId { get; set; }

    /// <summary>Display name of the selected entertainment configuration, shown on the settings page.</summary>
    public string HueEntertainmentConfigurationName { get; set; } = string.Empty;

    /// <summary>Overall Hue brightness as a percentage, 1-100.</summary>
    public int HueBrightnessPercent { get; set; } = 100;

    /// <summary>What the paired lights do once a synchronised session ends.</summary>
    public Core.Hue.HueEndBehaviour HueEndBehaviour { get; set; } = Core.Hue.HueEndBehaviour.WarmWhiteDim;

    /// <summary>
    /// How the Hue lights follow the picture, 0 (very reactive/intense) to
    /// 100 (beautifully smooth), <c>50</c> by default. Scales
    /// <see cref="Core.Hue.HueNaturalLightFilter"/>'s colour/brightness time
    /// constants and its brightness rate clamp -- reactive means the lights
    /// track a cut or a flash almost instantly, smooth means colour eases
    /// into a new scene over a second or more so the lighting reads as
    /// ambience rather than something competing for attention with the
    /// content itself. Read once when a Hue connection is (re-)established,
    /// like <see cref="SendWhiteChannel"/>'s own hardware-shaped settings --
    /// restart Jellyfin (or reconnect: stop then resume playback) after
    /// changing it for the new value to take effect.
    /// </summary>
    public int HueResponsePercent { get; set; } = 50;
}
