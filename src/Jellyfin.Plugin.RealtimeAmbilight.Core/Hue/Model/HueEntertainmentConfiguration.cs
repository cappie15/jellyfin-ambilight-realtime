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

/// <summary>
/// One light behind a channel, and which of that light's own addressable
/// segments this is -- <c>0</c> for an ordinary single-zone light, always
/// present but only meaningful (distinguishing segment from segment) for a
/// multi-segment device like a gradient lightstrip, per
/// <c>channels[].members[].index</c>.
/// </summary>
public readonly record struct HueChannelMember(Guid ServiceId, int Index);

public sealed record HueEntertainmentChannel(int ChannelId, HuePosition Position, IReadOnlyList<HueChannelMember> Members);

/// <summary>
/// One entertainment configuration (a "room" the Hue app calls an
/// entertainment area) as read from
/// <c>GET /clip/v2/resource/entertainment_configuration</c>. Only the fields
/// this plugin actually uses are modelled; everything else in the bridge's
/// response is ignored rather than rejected, so a bridge/firmware field this
/// plugin has never seen does not break parsing.
/// </summary>
/// <param name="ServiceLocations">
/// Each entertainment service's own real physical extent
/// (<c>locations.service_locations[].positions</c>) -- one position for an
/// ordinary light, two (its endpoints) for an elongated multi-segment
/// device such as a gradient lightstrip. Far more precise than any one
/// channel's own blended <see cref="HueEntertainmentChannel.Position"/>,
/// which can average together several unrelated lights and several
/// segments of the same gradient light all sharing one bridge-assigned
/// channel -- see <see cref="ResolveChannelPositions"/>.
/// </param>
public sealed record HueEntertainmentConfiguration(
    Guid Id,
    string Name,
    string ConfigurationType,
    string Status,
    IReadOnlyList<HueEntertainmentChannel> Channels,
    IReadOnlyDictionary<Guid, IReadOnlyList<HuePosition>>? ServiceLocations = null)
{
    /// <summary>True once the bridge itself reports another application already streaming.</summary>
    public bool IsActive => string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A copy of <see cref="Channels"/> with each channel's own
    /// <see cref="HueEntertainmentChannel.Position"/> replaced by the
    /// average of its members' own real positions, interpolated from
    /// <see cref="ServiceLocations"/>'s endpoints by each member's own
    /// <see cref="HueChannelMember.Index"/> when a member belongs to a
    /// multi-segment device.
    /// </summary>
    /// <remarks>
    /// The bridge's own per-channel <c>position</c> is a single point
    /// standing in for however many lights/segments got grouped under that
    /// one channel -- observed live grouping 2-5 different segments (from
    /// two different gradient lightstrips and other lights) under one
    /// channel, with a position that did not obviously represent any of
    /// them. This does not change *how many* channels exist or which
    /// segments share one (that grouping is the bridge's own, fixed by
    /// however the Entertainment Area's channel list was built -- the DTLS
    /// stream can only address by channel_id, so segments sharing a channel
    /// still show the same colour), but it does replace each channel's
    /// position with an accurate average of what it actually represents,
    /// which <see cref="Mapping.HueChannelMapper"/> maps colour from. A
    /// channel whose member(s) are missing from <see cref="ServiceLocations"/>
    /// keeps its original bridge-reported position unchanged.
    /// </remarks>
    public IReadOnlyList<HueEntertainmentChannel> ResolveChannelPositions()
    {
        var serviceLocations = ServiceLocations;
        if (serviceLocations is null || serviceLocations.Count == 0)
        {
            return Channels;
        }

        var maxIndexByService = new Dictionary<Guid, int>();
        foreach (var member in Channels.SelectMany(static c => c.Members))
        {
            maxIndexByService[member.ServiceId] = maxIndexByService.TryGetValue(member.ServiceId, out var current)
                ? Math.Max(current, member.Index)
                : member.Index;
        }

        var resolved = new List<HueEntertainmentChannel>(Channels.Count);
        foreach (var channel in Channels)
        {
            var memberPositions = new List<HuePosition>(channel.Members.Count);
            var allKnown = channel.Members.Count > 0;
            foreach (var member in channel.Members)
            {
                var position = InterpolateMemberPosition(member, serviceLocations, maxIndexByService);
                if (position is null)
                {
                    allKnown = false;
                    break;
                }

                memberPositions.Add(position.Value);
            }

            resolved.Add(allKnown ? channel with { Position = Average(memberPositions) } : channel);
        }

        return resolved;
    }

    private static HuePosition? InterpolateMemberPosition(
        HueChannelMember member,
        IReadOnlyDictionary<Guid, IReadOnlyList<HuePosition>> serviceLocations,
        Dictionary<Guid, int> maxIndexByService)
    {
        if (!serviceLocations.TryGetValue(member.ServiceId, out var positions) || positions.Count == 0)
        {
            return null;
        }

        if (positions.Count == 1)
        {
            return positions[0];
        }

        var maxIndex = maxIndexByService.TryGetValue(member.ServiceId, out var value) ? value : 0;
        var t = maxIndex > 0 ? Math.Clamp((double)member.Index / maxIndex, 0d, 1d) : 0d;
        var start = positions[0];
        var end = positions[^1];
        return new HuePosition(
            start.X + ((end.X - start.X) * t),
            start.Y + ((end.Y - start.Y) * t),
            start.Z + ((end.Z - start.Z) * t));
    }

    private static HuePosition Average(IReadOnlyList<HuePosition> positions)
        => new(positions.Average(static p => p.X), positions.Average(static p => p.Y), positions.Average(static p => p.Z));
}
