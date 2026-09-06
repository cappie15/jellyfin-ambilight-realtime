using Jellyfin.Plugin.RealtimeAmbilight.Core.Discovery;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Discovery;

public sealed class MdnsMessageTests
{
    /// <summary>
    /// The exact 192-byte answer captured from the reference WLED 16.0.1
    /// controller (831 LEDs) on 2026-09-06 in response to a
    /// <c>_wled._tcp.local</c> PTR query. It carries PTR, SRV, TXT and a
    /// compressed A record, so it exercises name compression end to end.
    /// </summary>
    private static readonly byte[] WledResponse =
    [
        0, 0, 132, 0, 0, 0, 0, 3, 0, 0, 0, 1, 5, 95, 119, 108, 101, 100, 4, 95, 116, 99, 112, 5, 108, 111, 99,
        97, 108, 0, 0, 12, 0, 1, 0, 0, 17, 148, 0, 14, 11, 119, 108, 101, 100, 45, 56, 101, 49, 98, 54, 56, 192,
        12, 11, 119, 108, 101, 100, 45, 56, 101, 49, 98, 54, 56, 5, 95, 119, 108, 101, 100, 4, 95, 116, 99, 112,
        5, 108, 111, 99, 97, 108, 0, 0, 33, 0, 1, 0, 0, 0, 120, 0, 25, 0, 0, 0, 0, 0, 80, 11, 119, 108, 101, 100,
        45, 56, 101, 49, 98, 54, 56, 5, 108, 111, 99, 97, 108, 0, 11, 119, 108, 101, 100, 45, 56, 101, 49, 98, 54,
        56, 5, 95, 119, 108, 101, 100, 4, 95, 116, 99, 112, 5, 108, 111, 99, 97, 108, 0, 0, 16, 0, 1, 0, 0, 17,
        148, 0, 17, 16, 109, 97, 99, 61, 97, 52, 102, 48, 48, 102, 56, 101, 49, 98, 54, 56, 192, 100, 0, 1, 0, 1,
        0, 0, 0, 120, 0, 4, 10, 0, 0, 8
    ];

    [Fact]
    public void ParseResponseReadsTheReferenceWledAnnouncement()
    {
        var instances = MdnsMessage.ParseResponse(WledResponse, MdnsMessage.WledServiceName);

        var instance = Assert.Single(instances);
        Assert.Equal("wled-8e1b68", instance.InstanceName);
        Assert.Equal("wled-8e1b68.local", instance.HostName);
        Assert.Equal(80, instance.Port);
        Assert.Equal("10.0.0.8", instance.IPv4Address);
    }

    [Fact]
    public void ParseResponseIgnoresOtherServices()
    {
        Assert.Empty(MdnsMessage.ParseResponse(WledResponse, "_http._tcp.local"));
    }

    [Fact]
    public void ParseResponseIgnoresQueriesAndEmptyMessages()
    {
        Assert.Empty(MdnsMessage.ParseResponse(MdnsMessage.CreateQuery(MdnsMessage.WledServiceName), MdnsMessage.WledServiceName));
        Assert.Empty(MdnsMessage.ParseResponse([], MdnsMessage.WledServiceName));
    }

    [Fact]
    public void ParseResponseSurvivesEveryTruncationOfARealMessage()
    {
        for (var length = 0; length < WledResponse.Length; length++)
        {
            var truncated = WledResponse.AsSpan(0, length);
            var instances = MdnsMessage.ParseResponse(truncated, MdnsMessage.WledServiceName);

            // A truncated message must never yield a half-built instance that
            // discovery would then probe as if it were a real controller.
            foreach (var instance in instances)
            {
                Assert.False(string.IsNullOrEmpty(instance.HostName));
                Assert.InRange(instance.Port, 1, 65535);
            }
        }
    }

    [Fact]
    public void ParseResponseRejectsACompressionPointerLoop()
    {
        var looping = (byte[])WledResponse.Clone();
        // Point the PTR record's own name back at itself.
        looping[52] = 0xC0;
        looping[53] = 40;

        var instances = MdnsMessage.ParseResponse(looping, MdnsMessage.WledServiceName);

        Assert.All(instances, instance => Assert.False(string.IsNullOrEmpty(instance.HostName)));
    }

    [Fact]
    public void CreateQueryEncodesAPtrQuestionForTheService()
    {
        var query = MdnsMessage.CreateQuery(MdnsMessage.WledServiceName);

        Assert.Equal(WledResponse.AsSpan(12, 18).ToArray(), query.AsSpan(12, 18).ToArray());
        Assert.Equal(1, query[5]);
        Assert.Equal(12, query[^3]);
        Assert.Equal(1, query[^1]);
    }
}
