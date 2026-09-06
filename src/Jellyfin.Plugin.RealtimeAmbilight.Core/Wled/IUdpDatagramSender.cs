using System.Net.Sockets;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Wled;

public interface IUdpDatagramSender
{
    Task SendAsync(ReadOnlyMemory<byte> datagram, string host, int port, CancellationToken cancellationToken);
}

public sealed class UdpDatagramSender : IUdpDatagramSender
{
    public async Task SendAsync(ReadOnlyMemory<byte> datagram, string host, int port, CancellationToken cancellationToken)
    {
        using var udpClient = new UdpClient();
        await udpClient.SendAsync(datagram, host, port, cancellationToken).ConfigureAwait(false);
    }
}
