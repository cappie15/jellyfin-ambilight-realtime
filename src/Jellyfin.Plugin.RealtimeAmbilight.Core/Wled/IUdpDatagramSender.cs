using System.Net.Sockets;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Wled;

public interface IUdpDatagramSender
{
    Task SendAsync(ReadOnlyMemory<byte> datagram, string host, int port, CancellationToken cancellationToken);
}

/// <summary>
/// Reuses one connected <see cref="UdpClient"/> for as long as the target
/// host/port stays the same, instead of opening and closing a fresh socket
/// (and re-resolving the host) for every single datagram. A frame this size
/// (831 LEDs) needs several DDP packets, so the naive per-call socket was
/// paying that setup/teardown cost 2-3 times per frame -- cheap enough in raw
/// CPU that it never showed up as load, but slow enough in wall-clock time
/// that it capped real throughput well below the configured output rate
/// (measured: sampling stuck at 23-26 fps against a 30 fps target and a
/// 60 fps decoder, with CPU, I/O wait and memory all comfortably idle).
/// </summary>
public sealed class UdpDatagramSender : IUdpDatagramSender, IDisposable
{
    private readonly object _sync = new();
    private UdpClient? _client;
    private string? _connectedHost;
    private int _connectedPort;

    public Task SendAsync(ReadOnlyMemory<byte> datagram, string host, int port, CancellationToken cancellationToken)
        => GetConnectedClient(host, port).SendAsync(datagram, cancellationToken).AsTask();

    private UdpClient GetConnectedClient(string host, int port)
    {
        lock (_sync)
        {
            if (_client is not null && _connectedHost == host && _connectedPort == port)
            {
                return _client;
            }

            _client?.Dispose();
            var client = new UdpClient();
            client.Connect(host, port);
            _client = client;
            _connectedHost = host;
            _connectedPort = port;
            return client;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
