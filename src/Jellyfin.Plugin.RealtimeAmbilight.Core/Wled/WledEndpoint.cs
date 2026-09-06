namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Wled;

/// <summary>A validated WLED address. Configuration never becomes a shell command.</summary>
public sealed class WledEndpoint
{
    public WledEndpoint(string host, int httpPort = 80)
    {
        if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            throw new ArgumentException("A valid WLED IP address or host name is required.", nameof(host));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(httpPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(httpPort, ushort.MaxValue);

        Host = host;
        HttpPort = httpPort;
    }

    public string Host { get; }

    public int HttpPort { get; }

    public Uri StateUri => new UriBuilder(Uri.UriSchemeHttp, Host, HttpPort, "/json/state").Uri;
}
