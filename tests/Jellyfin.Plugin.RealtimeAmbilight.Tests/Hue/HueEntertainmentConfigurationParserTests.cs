using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Model;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Hue;

public class HueEntertainmentConfigurationParserTests
{
    // Field names follow aiohue's entertainment_configuration.py model against
    // the same CLIP v2 resource, and non-sequential channel_id values plus an
    // extra, unrecognised top-level field ("stream_proxy") are deliberate:
    // real bridge responses are not guaranteed to number channels 0..n-1, and
    // this parser must tolerate fields it does not model.
    private const string Fixture = """
        {
          "errors": [],
          "data": [
            {
              "id": "a1b2c3d4-e5f6-4789-abcd-ef0123456789",
              "id_v1": "/groups/1",
              "metadata": { "name": "Living room TV" },
              "configuration_type": "screen",
              "status": "inactive",
              "stream_proxy": { "mode": "auto", "node": { "rid": "abc", "rtype": "bridge" } },
              "channels": [
                {
                  "channel_id": 5,
                  "position": { "x": -0.9, "y": 0.1, "z": 0.6 },
                  "members": [ { "service": { "rid": "11111111-0000-0000-0000-000000000001", "rtype": "entertainment" }, "index": 0 } ]
                },
                {
                  "channel_id": 12,
                  "position": { "x": 0.9, "y": 0.1, "z": 0.6 },
                  "members": [ { "service": { "rid": "22222222-0000-0000-0000-000000000002", "rtype": "entertainment" }, "index": 0 } ]
                },
                {
                  "channel_id": 30,
                  "position": { "x": -0.9, "y": 0.9, "z": 0.9 },
                  "members": [
                    { "service": { "rid": "33333333-0000-0000-0000-000000000003", "rtype": "entertainment" }, "index": 0 },
                    { "service": { "rid": "33333333-0000-0000-0000-000000000003", "rtype": "entertainment" }, "index": 1 }
                  ]
                }
              ]
            },
            {
              "id": "not-a-valid-guid",
              "channels": []
            }
          ]
        }
        """;

    [Fact]
    public void ParseListSkipsAnEntryWithAnInvalidId()
    {
        var configurations = HueEntertainmentConfigurationParser.ParseList(Fixture);

        var configuration = Assert.Single(configurations);
        Assert.Equal(Guid.Parse("a1b2c3d4-e5f6-4789-abcd-ef0123456789"), configuration.Id);
    }

    [Fact]
    public void ParseListReadsMetadataNameAndStatus()
    {
        var configuration = HueEntertainmentConfigurationParser.ParseList(Fixture).Single();

        Assert.Equal("Living room TV", configuration.Name);
        Assert.Equal("screen", configuration.ConfigurationType);
        Assert.False(configuration.IsActive);
    }

    [Fact]
    public void ParseListPreservesNonSequentialChannelIds()
    {
        var configuration = HueEntertainmentConfigurationParser.ParseList(Fixture).Single();

        Assert.Equal([5, 12, 30], configuration.Channels.Select(c => c.ChannelId));
    }

    [Fact]
    public void ParseListReadsEachChannelsOwnPosition()
    {
        var configuration = HueEntertainmentConfigurationParser.ParseList(Fixture).Single();

        var right = configuration.Channels.Single(c => c.ChannelId == 12);
        Assert.Equal(0.9, right.Position.X, precision: 6);
        Assert.Equal(0.1, right.Position.Y, precision: 6);
        Assert.Equal(0.6, right.Position.Z, precision: 6);
    }

    [Fact]
    public void ParseListHandlesMultipleChannelsSharingOneGradientLightsServiceId()
    {
        var configuration = HueEntertainmentConfigurationParser.ParseList(Fixture).Single();

        var gradientChannel = configuration.Channels.Single(c => c.ChannelId == 30);
        Assert.Equal(2, gradientChannel.MemberServiceIds.Count);
        Assert.All(gradientChannel.MemberServiceIds, id => Assert.Equal(gradientChannel.MemberServiceIds[0], id));
    }

    [Fact]
    public void ParseListToleratesAWholeUnrecognisedResourceTypeInTheArray()
    {
        // Only entertainment_configuration entries are expected in this
        // response shape, but the parser must not throw given something else.
        const string mixed = """{"data":[{"type":"light","id":"11111111-1111-1111-1111-111111111111"}]}""";

        var configurations = HueEntertainmentConfigurationParser.ParseList(mixed);

        Assert.Single(configurations);
    }

    [Fact]
    public void ParseListReturnsEmptyForAResponseWithNoDataArray()
    {
        Assert.Empty(HueEntertainmentConfigurationParser.ParseList("""{"errors":[{"description":"bad request"}]}"""));
    }
}
