#pragma warning disable CA1848, CA1873
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Discovery;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Output;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>
/// Finds WLED controllers on the local network for the settings page.
/// </summary>
/// <remarks>
/// Discovery uses mDNS, not SSDP: a stock WLED only answers SSDP when its Alexa
/// emulation is enabled, but always advertises <c>_wled._tcp.local</c>. Every
/// candidate is then confirmed with WLED's read-only <c>/json/info</c> endpoint,
/// so discovery never writes WLED state or persistent configuration.
/// </remarks>
public sealed class WledDiscoveryService
{
    private static readonly IPEndPoint MdnsEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);
    private static readonly TimeSpan ListenWindow = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WledDiscoveryService> _logger;

    public WledDiscoveryService(IHttpClientFactory httpClientFactory, ILogger<WledDiscoveryService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Discovers and verifies locally announced WLED controllers.</summary>
    public async Task<IReadOnlyList<WledDiscoveryResult>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // The already configured host is admin-supplied, so it is probed as
        // written; addresses learned from the network are untrusted and are
        // restricted to private IPv4 before anything connects to them.
        var configuredHost = Plugin.Instance?.Configuration.WledHost;
        if (!string.IsNullOrWhiteSpace(configuredHost))
        {
            candidates[configuredHost] = Math.Clamp(
                Plugin.Instance?.Configuration.WledHttpPort ?? 80,
                1,
                ushort.MaxValue);
        }

        foreach (var instance in await QueryMdnsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (IsPrivateIpv4(instance.IPv4Address))
            {
                candidates[instance.IPv4Address!] = instance.Port;
            }
        }

        var probes = candidates.Select(candidate => ProbeAsync(candidate.Key, candidate.Value, cancellationToken));
        var results = await Task.WhenAll(probes).ConfigureAwait(false);

        var confirmed = results
            .OfType<WledDiscoveryResult>()
            .OrderBy(result => result.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(result => result.Host, StringComparer.Ordinal)
            .ToArray();

        _logger.LogInformation(
            "WLED discovery probed {CandidateCount} candidate(s) and confirmed {ConfirmedCount}.",
            candidates.Count,
            confirmed.Length);

        return confirmed;
    }

    /// <summary>Collects <c>_wled._tcp.local</c> announcements from the local link.</summary>
    private async Task<IReadOnlyList<MdnsServiceInstance>> QueryMdnsAsync(CancellationToken cancellationToken)
    {
        var instances = new List<MdnsServiceInstance>();

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

            var query = MdnsMessage.CreateQuery(MdnsMessage.WledServiceName);
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

                foreach (var instance in MdnsMessage.ParseResponse(response.Buffer, MdnsMessage.WledServiceName))
                {
                    // Some responders omit the A record; the sender's own address
                    // is then the controller that answered.
                    instances.Add(instance.IPv4Address is null
                        ? instance with { IPv4Address = response.RemoteEndPoint.Address.ToString() }
                        : instance);
                }
            }
        }
        catch (SocketException exception)
        {
            // Multicast is unavailable in some container and VLAN setups. The
            // configured host is still probed, and the page offers manual entry.
            _logger.LogWarning(exception, "mDNS discovery for WLED was not possible on this host.");
        }

        return instances;
    }

    /// <summary>Confirms a candidate by reading WLED's public info document.</summary>
    private async Task<WledDiscoveryResult?> ProbeAsync(string host, int port, CancellationToken cancellationToken)
    {
        var authority = port == 80 ? host : string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            var client = _httpClientFactory.CreateClient();
            using var response = await client
                .GetAsync(new Uri($"http://{authority}/json/info"), timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                using var document = await JsonDocument
                    .ParseAsync(body, cancellationToken: timeout.Token)
                    .ConfigureAwait(false);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("leds", out var leds))
                {
                    // Something answered, but it is not a WLED controller.
                    return null;
                }

                var name = root.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                    ? nameValue.GetString()
                    : null;
                var ledCount = leds.TryGetProperty("count", out var count) && count.TryGetInt32(out var parsed)
                    ? parsed
                    : 0;
                var version = root.TryGetProperty("ver", out var ver) && ver.ValueKind == JsonValueKind.String
                    ? ver.GetString()
                    : null;

                return new WledDiscoveryResult(
                    authority,
                    string.IsNullOrWhiteSpace(name) ? "WLED" : name,
                    ledCount,
                    version ?? string.Empty);
            }
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads whether the controller applies gamma correction to realtime data.
    /// </summary>
    /// <returns>
    /// The encoding the controller expects, or <see langword="null"/> when it
    /// could not be read and the caller should keep its configured choice.
    /// </returns>
    /// <remarks>
    /// WLED exposes both halves of this: <c>light.gc.col</c> is the gamma it
    /// applies to colours, and <c>if.live.no-gc</c> says whether realtime data
    /// is exempted from it. Realtime is exempt by default, which is why sending
    /// display-encoded values is wrong far more often than it is right.
    /// </remarks>
    public async Task<Rgb24Encoding?> DetectRealtimeEncodingAsync(string host, int port, CancellationToken cancellationToken)
    {
        var settings = await ReadRealtimeSettingsAsync(host, port, cancellationToken).ConfigureAwait(false);
        return settings?.Encoding;
    }

    /// <summary>
    /// Reads the controller's public liveness state for the settings page. This
    /// is intentionally separate from configuration detection: a controller can
    /// be online even when its configuration endpoint is unavailable.
    /// </summary>
    public async Task<WledControllerStatus> ReadStatusAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return WledControllerStatus.Offline;
        }

        var authority = port == 80 ? host : string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            var client = _httpClientFactory.CreateClient();
            using var response = await client
                .GetAsync(new Uri($"http://{authority}/json/info"), timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WledControllerStatus.Offline;
            }

            var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
                var root = document.RootElement;
                var realtimeActive = root.TryGetProperty("live", out var live)
                    && live.ValueKind == JsonValueKind.True;
                return new WledControllerStatus(true, realtimeActive);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or UriFormatException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return WledControllerStatus.Offline;
        }
    }

    /// <summary>
    /// Reads the controller settings that change how realtime output looks.
    /// </summary>
    public async Task<WledRealtimeSettings?> ReadRealtimeSettingsAsync(string host, int port, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var authority = port == 80 ? host : string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            var client = _httpClientFactory.CreateClient();
            using var response = await client
                .GetAsync(new Uri($"http://{authority}/json/cfg"), timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
                var root = document.RootElement;

                var colourGamma = root.TryGetProperty("light", out var light)
                    && light.TryGetProperty("gc", out var gammaCorrection)
                    && gammaCorrection.TryGetProperty("col", out var colour)
                    && colour.TryGetDouble(out var parsedGamma)
                        ? parsedGamma
                        : 1d;

                var realtimeExempt = !root.TryGetProperty("if", out var interfaces)
                    || !interfaces.TryGetProperty("live", out var live)
                    || !live.TryGetProperty("no-gc", out var noGammaCorrection)
                    || noGammaCorrection.ValueKind != JsonValueKind.False;

                var forcesMaxBrightness = root.TryGetProperty("if", out var interfaceRoot)
                    && interfaceRoot.TryGetProperty("live", out var liveRoot)
                    && liveRoot.TryGetProperty("maxbri", out var maxBrightness)
                    && maxBrightness.ValueKind == JsonValueKind.True;

                var appliesGamma = !realtimeExempt && colourGamma > 1d;
                _logger.LogInformation(
                    "WLED {Host} reports colour gamma {Gamma} and realtime gamma {RealtimeGamma}; sending {Encoding} values.",
                    authority,
                    colourGamma,
                    realtimeExempt ? "skipped" : "applied",
                    appliesGamma ? "display-encoded" : "light-proportional");

                if (forcesMaxBrightness)
                {
                    _logger.LogWarning(
                        "WLED {Host} has \"force max brightness\" enabled for realtime data, so it ignores its own brightness slider while the Ambilight runs and drives the strip at full. Turn it off in WLED, or lower the LED brightness on the plugin's settings page.",
                        authority);
                }

                return new WledRealtimeSettings(
                    appliesGamma ? Rgb24Encoding.Bt709 : Rgb24Encoding.Linear,
                    forcesMaxBrightness);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("Could not read the realtime settings from WLED {Host}; keeping the configured choice.", authority);
            return null;
        }
    }

    private static bool IsPrivateIpv4(string? host)
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

/// <summary>Controller settings that change how realtime output looks.</summary>
public sealed record WledRealtimeSettings(Rgb24Encoding Encoding, bool ForcesMaxBrightness);

/// <summary>Reachability and temporary realtime ownership reported by WLED.</summary>
public sealed record WledControllerStatus(bool IsOnline, bool IsRealtimeActive)
{
    public static WledControllerStatus Offline { get; } = new(false, false);
}

/// <summary>Read-only WLED discovery data shown on the settings page.</summary>
public sealed record WledDiscoveryResult(string Host, string Name, int LedCount, string Version);
