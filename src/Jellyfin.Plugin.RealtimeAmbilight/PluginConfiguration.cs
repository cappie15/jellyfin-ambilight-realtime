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
    /// Set when a physical side's LEDs are wired against the clockwise
    /// perimeter order everything else assumes (top left to right, right top
    /// to bottom, bottom right to left, left bottom to top).
    /// </summary>
    /// <remarks>
    /// A perimeter built from four cut runs joined by corner wires has an
    /// independent data direction per side, and one run soldered the other way
    /// round renders that side's picture mirrored end for end while the other
    /// three stay correct. Before these existed the single clockwise-from-the-
    /// top-left assumption was the only wiring the plugin could express, so the
    /// only remedy was to resolder the strip. Default false, which is exactly
    /// the behaviour every existing installation already has.
    /// </remarks>
    public bool ReverseTopLeds { get; set; }

    /// <inheritdoc cref="ReverseTopLeds"/>
    public bool ReverseRightLeds { get; set; }

    /// <inheritdoc cref="ReverseTopLeds"/>
    public bool ReverseBottomLeds { get; set; }

    /// <inheritdoc cref="ReverseTopLeds"/>
    public bool ReverseLeftLeds { get; set; }

    /// <summary>
    /// Delays LED output to compensate for a client display pipeline that lags
    /// the server-side decoder. Positive values make the LEDs react later.
    /// Starts at zero: it is raised only when the LEDs are demonstrably ahead of
    /// the picture on the calibrated display.
    /// </summary>
    public int OutputDelayMilliseconds { get; set; }

    /// <summary>
    /// Was kept at a conservative 30 for weaker future installs even after
    /// real-room tuning suggested higher; the operator has since run 60 on
    /// their own reference host long enough to call it validated rather than
    /// exploratory, so this now reflects that. Installs on weaker hardware
    /// can still lower it.
    /// </summary>
    public int OutputFramesPerSecond { get; set; } = 60;

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

    /// <summary>
    /// Retained so existing serialized configurations keep deserializing; no
    /// longer used. Removed as a user setting: the operator's own value had
    /// drifted to 2500 ms with no sign a shorter fade was ever wanted, so
    /// <see cref="JellyfinWledOutputService"/> now uses that as a fixed
    /// constant instead of a control almost never left at its default.
    /// </summary>
    public int StopFadeMilliseconds { get; set; } = 250;

    /// <summary>
    /// Fraction of each axis, as a percentage, sampled inward from every edge.
    /// <see cref="EdgeSampler.DefaultDepthPercent"/> (ten percent) was the
    /// original recommendation -- enough of the picture to be stable without
    /// letting the centre dominate an edge -- but real-room tuning has moved
    /// this value more than once as more real content was watched against it
    /// (5, then 27, now 7); this default tracks whatever that tuning most
    /// recently settled on rather than the original untested guess.
    /// </summary>
    public int SamplingDepthPercent { get; set; } = 7;

    /// <summary>
    /// Sends byte values proportional to light output, which is what a
    /// controller needs when it drives its LEDs straight from the byte. WLED
    /// skips gamma correction for realtime data by default, so leaving this on
    /// is almost always right; turn it off only if gamma correction for
    /// realtime is enabled on the controller itself.
    /// </summary>
    /// <summary>
    /// Overall LED brightness as a percentage of the picture's own brightness,
    /// 1-200. WLED's realtime path bypasses its master brightness when "force
    /// max brightness" is enabled, which is its default, so without this an
    /// installation has no way to dim the Ambilight on its own. Above 100%
    /// boosts it past the source picture's own value -- physical LEDs next to
    /// a bright HDR screen otherwise always read as dim, since nothing else
    /// in the pipeline can push a pixel brighter than it already is.
    /// </summary>
    public int BrightnessPercent { get; set; } = 158;

    /// <summary>
    /// Colour intensity as a percentage. Above 100 pushes each colour away from
    /// its own luminance, which deepens it without changing how bright it is.
    /// </summary>
    public int SaturationPercent { get; set; } = 175;

    /// <summary>
    /// Per-channel gains as percentages. Used today only by the wizard's
    /// White step (a red/blue push-pull for colour temperature) and kept for
    /// backward compatibility with an existing saved configuration; the
    /// wizard's Red/Green/Blue/Yellow/Cyan/Magenta steps drive the six
    /// <c>Hue*</c>/<c>...Brightness*</c>/<c>...Intensity*</c> properties
    /// below instead -- see <see cref="Core.Color.PerimeterColourTuning"/>.
    /// </summary>
    public int RedGainPercent { get; set; } = 127;

    public int GreenGainPercent { get; set; } = 100;

    public int BlueGainPercent { get; set; } = 73;

    /// <summary>
    /// The colour-tuning wizard's six primary/secondary anchors: how far to
    /// rotate that colour's own hue (degrees, clamped to ±23 -- see
    /// <see cref="Core.Color.PerimeterColourTuning"/>'s own remarks on that
    /// range), and that colour's own brightness/intensity (saturation)
    /// multipliers. Defaults below reflect real-room tuning against actual
    /// HDR10 content (validated 2026-09-14) rather than the identity
    /// (0°, 100%, 100%) every anchor started at; together they build the
    /// <see cref="Core.Color.HueCorrectionCurve"/> that is now WLED's
    /// general colour correction, applied before brightness/saturation/
    /// wall-colour/per-side trims, which are unchanged.
    /// </summary>
    public int RedHueShiftDegrees { get; set; }
    public int RedBrightnessPercent { get; set; } = 100;
    public int RedIntensityPercent { get; set; } = 105;

    public int GreenHueShiftDegrees { get; set; } = -2;
    public int GreenBrightnessPercent { get; set; } = 100;
    public int GreenIntensityPercent { get; set; } = 121;

    public int BlueHueShiftDegrees { get; set; } = 2;
    public int BlueBrightnessPercent { get; set; } = 38;
    public int BlueIntensityPercent { get; set; } = 173;

    public int YellowHueShiftDegrees { get; set; } = -3;
    public int YellowBrightnessPercent { get; set; } = 100;
    public int YellowIntensityPercent { get; set; } = 172;

    public int CyanHueShiftDegrees { get; set; } = -5;
    public int CyanBrightnessPercent { get; set; } = 100;
    public int CyanIntensityPercent { get; set; } = 200;

    public int MagentaHueShiftDegrees { get; set; } = 21;
    public int MagentaBrightnessPercent { get; set; } = 53;
    public int MagentaIntensityPercent { get; set; } = 198;

    /// <summary>
    /// The wizard's 20%-grey and 40%-grey steps, right after White: a strip's
    /// white balance can drift with brightness, not just at maximum output,
    /// so these two add an independent temperature (red/blue push-pull, same
    /// mechanic as White's own slider) and tint (green/magenta push-pull, an
    /// axis White does not expose) correction at those two brightness levels,
    /// clamped to ±60 like White's own slider. Both default to 0 (no
    /// additional correction beyond White's own flat gain) -- see
    /// <see cref="Core.Color.GreyBalanceCurve"/>.
    /// </summary>
    public int Grey20TemperatureDelta { get; set; }
    public int Grey20TintDelta { get; set; }
    public int Grey40TemperatureDelta { get; set; }
    public int Grey40TintDelta { get; set; }

    /// <summary>
    /// Below this fraction of picture-edge luminance, a physical LED is driven
    /// fully off instead of a dim, often colour-cast glow; above it, the
    /// remaining headroom rescales smoothly back up to full range so there is
    /// no jump right at the boundary. Zero disables it. The percentage is
    /// perceptual (what "4%" means on a waveform monitor), not linear-light --
    /// converted internally (see <see cref="Core.Color.PerimeterColourAdjustment"/>)
    /// since gamma compresses shadows and a perceptual 4% is roughly
    /// linear-light 0.9%; treating the raw number as already linear cut off
    /// far more real shadow detail than its own value suggested.
    /// </summary>
    public int BlackLevelFloorPercent { get; set; } = 3;

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

    /// <summary>
    /// Was kept at a conservative 160 for weaker future installs even after
    /// real-room tuning suggested higher; the operator has since run 320 on
    /// their own reference host long enough to call it validated rather than
    /// exploratory, so this now reflects that. Installs on weaker hardware
    /// can still lower it.
    /// </summary>
    public int AnalysisWidth { get; set; } = 320;

    /// <summary>
    /// Derived from <see cref="AnalysisWidth"/> at 16:9 by the settings page.
    /// Kept serialized for backwards compatibility with existing configuration.
    /// </summary>
    public int AnalysisHeight { get; set; } = 180;

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
    /// as such. Zero disables it entirely: every sampled frame is shown
    /// exactly as sampled.
    /// </summary>
    public int MinimumColourHoldMilliseconds { get; set; } = 100;

    /// <summary>
    /// Eases each LED toward a newly sampled colour over roughly this many
    /// milliseconds instead of jumping straight to it, so a real cut or a
    /// fast pan still reads as reactive while frame-to-frame sampling noise
    /// -- most visible as small "steps" between source and target colour at
    /// low brightness, where the eye is most sensitive to a brightness
    /// change -- gets smoothed away before it ever reaches the temporal
    /// ditherer. Zero disables it entirely: every sampled frame is shown
    /// exactly as sampled. Researched against HyperHDR's own "Infinite Color
    /// Engine" before building this -- see
    /// <see cref="Core.Output.WledTemporalSmoother"/>.
    /// </summary>
    public int WledSmoothingMilliseconds { get; set; } = 1200;

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
    /// How much of a bright/near-white pixel's shared grey is allowed onto
    /// the physical white LED before the rest is left on red, green and
    /// blue instead, 1-100%. Exists because a single white die has nowhere
    /// near the peak output of red, green and blue lit together: measured
    /// live via flicker photometry (alternating the same LEDs between a
    /// mixed-RGB white and the white channel alone, which cancels the two's
    /// considerable difference in colour temperature and isolates a genuine
    /// brightness gap) -- the gap did not shrink at all between white=100
    /// and white=230 out of 255, meaning no amount of drive current makes
    /// the one die reach what three combined can, and pushing the value
    /// higher cannot fix it. Below this ceiling (dim greys, where the die's
    /// own peak was never the limiting factor) behaviour is unchanged from
    /// full extraction; only pixels bright enough to hit the ceiling keep a
    /// growing share of their own grey on red, green and blue, which -- being
    /// three simultaneously lit dies -- can actually reach full brightness
    /// where the white die alone cannot. Default 50% is a reasoned starting
    /// point from where that test left off (the gap held at all tested
    /// levels up to 230/255 without narrowing); re-check while watching a
    /// bright scene, not a synthetic swatch, and adjust from there.
    /// </summary>
    public int WhiteChannelStrengthPercent { get; set; } = 20;

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

    /// <summary>
    /// Overall Hue brightness as a percentage, 1-200. Above 100% boosts
    /// past the source picture's own value, for the same reason WLED's own
    /// Brightness control was widened past 100%: nothing upstream can push
    /// a pixel brighter than its own tonemapped source value, so content
    /// that never reaches peak brightness always reads as dim on the bulb.
    /// </summary>
    public int HueBrightnessPercent { get; set; } = 188;

    /// <summary>What the paired lights do once a synchronised session ends.</summary>
    public Core.Hue.HueEndBehaviour HueEndBehaviour { get; set; } = Core.Hue.HueEndBehaviour.WarmWhiteDim;

    /// <summary>
    /// How the Hue lights follow the picture, 0 (very reactive/intense) to
    /// 100 (beautifully smooth). Scales
    /// <see cref="Core.Hue.HueNaturalLightFilter"/>'s colour/brightness time
    /// constants and its brightness rate clamp -- reactive means the lights
    /// track a cut or a flash almost instantly, smooth means colour eases
    /// into a new scene over a second or more so the lighting reads as
    /// ambience rather than something competing for attention with the
    /// content itself. Resolved live from this property on every frame (see
    /// <see cref="Core.Hue.HueFrameProcessor"/>'s own resolver), so changing
    /// it applies immediately, unlike <see cref="SendWhiteChannel"/>'s own
    /// hardware-shaped settings.
    /// </summary>
    public int HueResponsePercent { get; set; } = 50;

    /// <summary>
    /// Below this fraction of perceptual brightness, a Hue light goes to
    /// true black instead of holding a dim, always-on glow; above it, the
    /// remaining headroom rescales smoothly back up. Same meaning and same
    /// perceptual-percentage convention as WLED's own
    /// <see cref="BlackLevelFloorPercent"/>. Replaces an earlier fixed 1%
    /// minimum brightness that could never actually reach true black at
    /// all, which is exactly why Hue was reported as never going
    /// completely dark; the default was promoted to 18% once real-room
    /// tuning (2026-09-14) settled there.
    /// </summary>
    public int HueBlackLevelFloorPercent { get; set; } = 18;

    /// <summary>
    /// How wide the television is in the Hue bridge's own room coordinates,
    /// as a percentage of the room's half-width. 100 means the screen spans
    /// the whole room, which is what this plugin assumed before this setting
    /// existed.
    /// </summary>
    /// <remarks>
    /// Every Hue position is reported on a -1 to +1 room scale, and the
    /// mapping used to read -1 and +1 as the picture's own edges. A set does
    /// not span its wall. On the reference installation the three gradient
    /// strips that hug the television report X and Z of only -0.4 to +0.4, so
    /// each of them only ever sampled the middle 40% of the picture: the
    /// strip under the set never saw the left or right extremes of the image
    /// at all, and its colour boundary landed around 70% of its length rather
    /// than halfway. Setting this to roughly the extent the strips themselves
    /// report (40 there) puts a light at the edge of the screen on the edge of
    /// the picture. Read the numbers off the entertainment area rather than
    /// guessing: they are the same ones the Hue app's own position editor sets.
    /// </remarks>
    public int HueScreenWidthPercent { get; set; } = 100;

    /// <inheritdoc cref="HueScreenWidthPercent"/>
    public int HueScreenHeightPercent { get; set; } = 100;
}
