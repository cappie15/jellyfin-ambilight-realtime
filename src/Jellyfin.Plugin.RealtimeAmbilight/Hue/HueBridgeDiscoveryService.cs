#pragma warning disable CA1848, CA1873
using System.Net;
using System.Net.Sockets;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Discovery;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RealtimeAmbilight.Hue;

/// <summary>
/// Finds Hue bridges on the local network for the settings page. Mirrors
/// <c>WledDiscoveryService</c>'s own mDNS approach and reuses the same
/// <see cref="MdnsMessage"/> query/parse code -- the DNS-SD mechanics are
/// identical, only the service name differs.
/// </summary>
public sealed class HueBridgeDiscoveryService
{
    /// <summary>The DNS-SD service type a Hue bridge advertises.</summary>
    public const string HueServiceName = "_hue._tcp.local";

    private static readonly IPEndPoint MdnsEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);
    private static readonly TimeSpan ListenWindow = TimeSpan.FromMilliseconds(1500);

    private readonly HueBridgeClient _bridgeClient;
    private readonly ILogger<HueBridgeDiscoveryService> _logger;

    public HueBridgeDiscoveryService(HueBridgeClient bridgeClient, ILogger<HueBridgeDiscoveryService> logger)
    {
        _bridgeClient = bridgeClient ?? throw new ArgumentNullException(nameof(bridgeClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<HueBridgeInfo>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in await QueryMdnsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (instance.IPv4Address is { } address && IsPrivateIpv4(address))
            {
                candidates.Add(address);
            }
        }

        var probes = candidates.Select(host => _bridgeClient.ProbeAsync(host, cancellationToken));
        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        var confirmed = results.OfType<HueBridgeInfo>().OrderBy(result => result.Host, StringComparer.Ordinal).ToArray();

        _logger.LogInformation(
            "Hue bridge discovery probed {CandidateCount} candidate(s) and confirmed {ConfirmedCount}.",
            candidates.Count,
            confirmed.Length);

        return confirmed;
    }

    private async Task<IReadOnlyList<MdnsServiceInstance>> QueryMdnsAsync(CancellationToken cancellationToken)
    {
        var instances = new List<MdnsServiceInstance>();
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

            var query = MdnsMessage.CreateQuery(HueServiceName);
            await udp.SendAsync(query, MdnsEndpoint, cancellationToken).ConfigureAwait(false);

            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(ListenWindow);

            while (!window.IsCancellationRequested)
            {
                UdpReceiveResult response;
                try
                {
                    response = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                foreach (var instance in MdnsMessage.ParseResponse(response.Buffer, HueServiceName))
                {
                    instances.Add(instance.IPv4Address is null
                        ? instance with { IPv4Address = response.RemoteEndPoint.Address.ToString() }
                        : instance);
                }
            }
        }
        catch (SocketException exception)
        {
            _logger.LogWarning(exception, "mDNS discovery for a Hue bridge was not possible on this host.");
        }

        return instances;
    }

    private static bool IsPrivateIpv4(string host)
    {
        if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var octets = address.GetAddressBytes();
        return octets[0] == 10
            || (octets[0] == 172 && octets[1] is >= 16 and <= 31)
            || (octets[0] == 192 && octets[1] == 168);
    }
}
