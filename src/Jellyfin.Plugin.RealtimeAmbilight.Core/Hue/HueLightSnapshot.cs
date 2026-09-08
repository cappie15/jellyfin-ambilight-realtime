namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Hue;

/// <summary>
/// One light's state as it was immediately before a synchronisation session
/// took over, captured only for the properties this plugin can actually
/// restore: on/off, brightness, and whichever single colour mode (xy,
/// colour-temperature, or a Gradient's own point list) the bridge reported as
/// active. A dynamic scene or effect running before the session cannot be
/// reconstructed from this and is not promised back -- only its last static
/// appearance is.
/// </summary>
public sealed record HueLightSnapshotEntry(
    Guid LightId,
    bool On,
    double? BrightnessPercent,
    string? ColorMode,
    (double X, double Y)? XyColor,
    int? ColorTemperatureMirek,
    IReadOnlyList<(double X, double Y)>? GradientPoints);

/// <summary>
/// Every light's state captured once at the start of one playback session,
/// before the entertainment stream takes over. Keyed by the session's own
/// identity so a stale restore or a reconnect's snapshot can never be
/// confused with a session that has since ended and been replaced by a new
/// one.
/// </summary>
public sealed record HueSessionSnapshot(string PlaybackSessionId, IReadOnlyList<HueLightSnapshotEntry> Lights)
{
    public static HueSessionSnapshot Empty(string playbackSessionId) => new(playbackSessionId, []);
}

/// <summary>End-of-session behaviour, applied only to lights in the selected entertainment configuration.</summary>
public enum HueEndBehaviour
{
    /// <summary>Warm white (2700 K design target) at 15% brightness.</summary>
    WarmWhiteDim,

    /// <summary>Restore each light's own <see cref="HueSessionSnapshot"/> entry.</summary>
    RestorePreviousState,
}
