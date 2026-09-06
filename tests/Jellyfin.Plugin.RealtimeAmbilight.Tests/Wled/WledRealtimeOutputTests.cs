using Jellyfin.Plugin.RealtimeAmbilight.Core.Protocol;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Wled;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Wled;

public class WledRealtimeOutputTests
{
    [Fact]
    public async Task AutoUsesRawRgbForAFrameAtTheSafeDatagramBoundary()
    {
        var sender = new CapturingUdpSender();
        var output = CreateOutput(WledRealtimeProtocol.Auto, sender);
        var frame = new byte[HyperionRawRgbPacketizer.MaximumDatagramPayload];

        await output.SendFrameAsync(frame, CancellationToken.None);

        var sent = Assert.Single(sender.Datagrams);
        Assert.Equal(WledRealtimeOutput.HyperionRawRgbPort, sent.Port);
        Assert.Equal(frame, sent.Payload);
        Assert.Equal(WledRealtimeProtocol.HyperionRawRgb, output.CurrentProtocol!.Protocol);
    }

    [Fact]
    public async Task AutoUsesOrderedDdpPacketsAboveTheRawRgbBoundary()
    {
        var sender = new CapturingUdpSender();
        var output = CreateOutput(WledRealtimeProtocol.Auto, sender);
        var frame = new byte[(HyperionRawRgbPacketizer.MaximumLedCount + 1) * 3];

        await output.SendFrameAsync(frame, CancellationToken.None);

        Assert.Equal(2, sender.Datagrams.Count);
        Assert.All(sender.Datagrams, datagram => Assert.Equal(WledRealtimeOutput.DdpPort, datagram.Port));
        Assert.Equal(DdpPacketizer.Version1, sender.Datagrams[0].Payload[0]);
        Assert.Equal((byte)(DdpPacketizer.Version1 | DdpPacketizer.Push), sender.Datagrams[1].Payload[0]);
        Assert.Equal(WledRealtimeProtocol.Ddp, output.CurrentProtocol!.Protocol);
    }

    [Fact]
    public async Task KeepAliveResendsTheLastFrameUntilTheOutputIsReleased()
    {
        var sender = new CapturingUdpSender();
        var output = CreateOutput(WledRealtimeProtocol.HyperionRawRgb, sender);
        var frame = new byte[] { 1, 2, 3 };

        await output.SendFrameAsync(frame, CancellationToken.None);
        await output.KeepAliveAsync(CancellationToken.None);

        Assert.Equal(2, sender.Datagrams.Count);
        Assert.Equal(frame, sender.Datagrams[1].Payload);

        // Releasing is defined as ceasing transmission (ADR-004): it must send
        // nothing itself, and must stop any later keepalive from resurrecting the
        // last frame after WLED has taken its effect back.
        await output.ReleaseAsync(CancellationToken.None);
        await output.KeepAliveAsync(CancellationToken.None);

        Assert.Equal(2, sender.Datagrams.Count);
    }

    [Fact]
    public async Task ExplicitRawRgbDoesNotTruncateAnOversizedFrame()
    {
        var sender = new CapturingUdpSender();
        var output = CreateOutput(WledRealtimeProtocol.HyperionRawRgb, sender);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => output.SendFrameAsync(new byte[1473], CancellationToken.None));

        Assert.Empty(sender.Datagrams);
    }

    private static WledRealtimeOutput CreateOutput(
        WledRealtimeProtocol protocol,
        CapturingUdpSender sender)
        => new(new WledEndpoint("wled.local"), protocol, sender);

    private sealed class CapturingUdpSender : IUdpDatagramSender
    {
        public List<(byte[] Payload, string Host, int Port)> Datagrams { get; } = [];

        public Task SendAsync(ReadOnlyMemory<byte> datagram, string host, int port, CancellationToken cancellationToken)
        {
            Datagrams.Add((datagram.ToArray(), host, port));
            return Task.CompletedTask;
        }
    }

}
