using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Model;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Hue;

public sealed class HueEntertainmentConfigurationTests
{
    [Fact]
    public void ResolveChannelPositionsLeavesAChannelUntouchedWhenNoServiceLocationsAreKnown()
    {
        var configuration = new HueEntertainmentConfiguration(
            Guid.NewGuid(),
            "TV room",
            "screen",
            "inactive",
            [new HueEntertainmentChannel(0, new HuePosition(0.4, 0.8, -0.4), [new HueChannelMember(Guid.NewGuid(), 0)])]);

        var resolved = configuration.ResolveChannelPositions();

        Assert.Equal(new HuePosition(0.4, 0.8, -0.4), resolved.Single().Position);
    }

    [Fact]
    public void ResolveChannelPositionsInterpolatesAGradientLightsSegmentAlongItsTwoRecordedEndpoints()
    {
        var gradientLightId = Guid.NewGuid();
        var configuration = new HueEntertainmentConfiguration(
            Guid.NewGuid(),
            "TV room",
            "screen",
            "inactive",
            Channels:
            [
                // Segments 0 and 6 of a 7-segment (index 0..6) gradient light,
                // sharing one bridge-assigned channel exactly like the real
                // "TV-ruimte" data this reproduces.
                new HueEntertainmentChannel(0, new HuePosition(0, 0, 0), [new HueChannelMember(gradientLightId, 0), new HueChannelMember(gradientLightId, 6)]),
            ],
            ServiceLocations: new Dictionary<Guid, IReadOnlyList<HuePosition>>
            {
                [gradientLightId] = [new HuePosition(-1, 1, 0), new HuePosition(1, 1, 0)],
            });

        var resolved = configuration.ResolveChannelPositions();

        // Segment 0 sits at the strip's start (-1,1,0), segment 6 at its end
        // (1,1,0); averaging the two endpoints should land back at the
        // midpoint, not at the channel's own original (0,0,0).
        var position = resolved.Single().Position;
        Assert.Equal(0d, position.X, precision: 6);
        Assert.Equal(1d, position.Y, precision: 6);
        Assert.Equal(0d, position.Z, precision: 6);
    }

    [Fact]
    public void ResolveChannelPositionsGivesEachGradientSegmentItsOwnDistinctPositionWhenNotSharingAChannel()
    {
        var gradientLightId = Guid.NewGuid();
        var configuration = new HueEntertainmentConfiguration(
            Guid.NewGuid(),
            "TV room",
            "screen",
            "inactive",
            Channels:
            [
                new HueEntertainmentChannel(0, new HuePosition(0, 0, 0), [new HueChannelMember(gradientLightId, 0)]),
                new HueEntertainmentChannel(1, new HuePosition(0, 0, 0), [new HueChannelMember(gradientLightId, 3)]),
                new HueEntertainmentChannel(2, new HuePosition(0, 0, 0), [new HueChannelMember(gradientLightId, 6)]),
            ],
            ServiceLocations: new Dictionary<Guid, IReadOnlyList<HuePosition>>
            {
                [gradientLightId] = [new HuePosition(-1, 1, 0), new HuePosition(1, 1, 0)],
            });

        var resolved = configuration.ResolveChannelPositions();

        Assert.Equal(-1d, resolved[0].Position.X, precision: 6);
        Assert.Equal(0d, resolved[1].Position.X, precision: 6);
        Assert.Equal(1d, resolved[2].Position.X, precision: 6);
    }

    [Fact]
    public void ResolveChannelPositionsFallsBackToTheOriginalPositionWhenOnlySomeMembersAreKnown()
    {
        var knownId = Guid.NewGuid();
        var unknownId = Guid.NewGuid();
        var configuration = new HueEntertainmentConfiguration(
            Guid.NewGuid(),
            "TV room",
            "screen",
            "inactive",
            Channels: [new HueEntertainmentChannel(0, new HuePosition(0.4, 0.8, -0.4), [new HueChannelMember(knownId, 0), new HueChannelMember(unknownId, 0)])],
            ServiceLocations: new Dictionary<Guid, IReadOnlyList<HuePosition>> { [knownId] = [new HuePosition(1, 1, 1)] });

        var resolved = configuration.ResolveChannelPositions();

        Assert.Equal(new HuePosition(0.4, 0.8, -0.4), resolved.Single().Position);
    }
}
