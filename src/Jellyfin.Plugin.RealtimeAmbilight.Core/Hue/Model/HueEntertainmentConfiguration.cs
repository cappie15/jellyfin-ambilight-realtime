namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Model;

/// <summary>
/// A channel's position within an entertainment configuration's own
/// coordinate space, exactly as the bridge reports it (CLIP v2
/// <c>entertainment_configuration.channels[].position</c>). Each component is
/// a float roughly in [-1, 1].
/// </summary>
/// <remarks>
/// Axis convention, per the Hue Entertainment developer documentation and
/// corroborated by every third-party client this project could inspect (not
/// independently re-verified against the official reference this round --
/// that page is behind a developer-portal login this environment cannot
/// reach): the origin sits at the screen, at floor level, centred.
/// <c>X</c> runs left (-1) to right (+1) facing the screen. <c>Y</c> is depth:
/// roughly 0 at the screen, increasing toward and past the viewer, toward the
/// back of the room. <c>Z</c> is height: 0 at the floor, increasing toward
/// the ceiling. This is exactly the assumption <see cref="HueChannelMapper"/>
/// is built on, and it is the first thing to check against the real bridge:
/// place one light unambiguously (directly beside the TV, say) and confirm
/// its reported X is near 0 and Y is near 0 before trusting the mapping.
/// </remarks>
public readonly record struct HuePosition(double X, double Y, double Z);

public sealed record HueEntertainmentChannel(int ChannelId, HuePosition Position, IReadOnlyList<Guid> MemberServiceIds);

/// <summary>
/// One entertainment configuration (a "room" the Hue app calls an
/// entertainment area) as read from
/// <c>GET /clip/v2/resource/entertainment_configuration</c>. Only the fields
/// this plugin actually uses are modelled; everything else in the bridge's
/// response is ignored rather than rejected, so a bridge/firmware field this
/// plugin has never seen does not break parsing.
/// </summary>
public sealed record HueEntertainmentConfiguration(
    Guid Id,
    string Name,
    string ConfigurationType,
    string Status,
    IReadOnlyList<HueEntertainmentChannel> Channels)
{
    /// <summary>True once the bridge itself reports another application already streaming.</summary>
    public bool IsActive => string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase);
}
