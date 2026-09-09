#pragma warning disable CA1848, CA1873
using System.Globalization;
using System.Linq;
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
    /// <summary>
    /// WLED's digital LED type id for SK6812 RGBW, the only chipset id this
    /// plugin has actually confirmed carries a physical fourth (white) diode
    /// (see ADR-004). <c>hw.led.ins[].type</c> is hardware fact reported by
    /// the controller, unlike <c>info.leds.wv</c>/<c>lc</c>, which reflect
    /// WLED's auto-white UI mode and are not reliable for detection.
    /// </summary>
    private const int RgbwLedType = 30;

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
                var name = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;
                return new WledControllerStatus(true, realtimeActive, name);
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

                // Shown read-only on the settings page. This plugin never writes
                // it: it is the controller's own protection for the strip and
                // power supply, and changing it from here is deliberately not offered.
                var maxPowerMilliamps = root.TryGetProperty("led", out var led)
                    && led.TryGetProperty("maxpwr", out var maxPower)
                    && maxPower.TryGetInt32(out var parsedMaxPower)
                        ? parsedMaxPower
                        : 0;

                root.TryGetProperty("hw", out var hw);
                hw.TryGetProperty("led", out var hwLed);
                hwLed.TryGetProperty("ins", out var ins);

                var hasWhiteChannelHardware = ins.ValueKind == JsonValueKind.Array
                    && ins.EnumerateArray().Any(strip => strip.TryGetProperty("type", out var type)
                        && type.TryGetInt32(out var typeValue)
                        && typeValue == RgbwLedType);

                // The controller's own configured refresh-rate cap. A large
                // single-pin strip is physically bounded well below this by
                // the LED protocol's own bit-banging time (831 RGBW LEDs on
                // one WS281x-family pin measured at ~18 Hz actual against a
                // 42 Hz cap here, on the reference strip) -- shown so an
                // operator setting the plugin's own output rate above the
                // achievable ceiling can see why raising it further does
                // nothing, rather than assuming the plugin is at fault.
                var ledFramesPerSecond = hwLed.TryGetProperty("fps", out var fps) && fps.TryGetInt32(out var parsedFps)
                    ? parsedFps
                    : (int?)null;

                // Deciseconds until WLED reclaims the strip once realtime
                // data stops arriving -- the number ADR-004's own keepalive
                // design has to stay under.
                var realtimeTimeoutMilliseconds = interfaceRoot.TryGetProperty("live", out var liveTimeoutRoot)
                    && liveTimeoutRoot.TryGetProperty("timeout", out var timeoutDeciseconds)
                    && timeoutDeciseconds.TryGetInt32(out var parsedTimeout)
                        ? parsedTimeout * 100
                        : (int?)null;

                // 0 = Manual, the only mode RGBW32 output is correct under:
                // anything else has WLED deriving or subtracting its own
                // white value from what this plugin already computed and
                // sent, double-processing it. Read from the first configured
                // LED output (this project's reference install has exactly
                // one), not the top-level default-for-new-strips value.
                var rgbwMode = ins.ValueKind == JsonValueKind.Array
                    && ins.GetArrayLength() > 0
                    && ins[0].TryGetProperty("rgbwm", out var rgbwModeElement)
                    && rgbwModeElement.TryGetInt32(out var parsedRgbwMode)
                        ? parsedRgbwMode
                        : (int?)null;
                var rgbwModeIsManual = rgbwMode is null or 0;
                if (hasWhiteChannelHardware && !rgbwModeIsManual)
                {
                    _logger.LogWarning(
                        "WLED {Host} has RGBW mode {RgbwMode} instead of Manual (0) on its white-capable strip; this plugin's own white-channel output will be double-processed by WLED's own auto-white derivation until this is fixed.",
                        authority,
                        rgbwMode);
                }

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
                    forcesMaxBrightness,
                    maxPowerMilliamps,
                    hasWhiteChannelHardware,
                    ledFramesPerSecond,
                    realtimeTimeoutMilliseconds,
                    hasWhiteChannelHardware && !rgbwModeIsManual);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("Could not read the realtime settings from WLED {Host}; keeping the configured choice.", authority);
            return null;
        }
    }

    /// <summary>
    /// Turns off WLED's "force max brightness" for realtime data -- the one
    /// WLED setting this plugin can write, and only when the operator has
    /// opted in on the settings page. The request body names nothing else, so
    /// it cannot touch the ABL power budget or any other WLED configuration
    /// regardless of what else the operator has set on the controller.
    /// </summary>
    public async Task<bool> TryDisableForceMaxBrightnessAsync(string host, int port, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var authority = port == 80 ? host : string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            var client = _httpClientFactory.CreateClient();
            // A fixed-length StringContent, not PostAsJsonAsync's streamed
            // JsonContent: WLED's ESPAsyncWebServer rejects a chunked request
            // body with 400 Bad Request, the same failure that once broke the
            // realtime stop call (see the deleted IWledControlClient's history).
            using var content = new StringContent(
                """{"if":{"live":{"maxbri":false}}}""",
                System.Text.Encoding.UTF8,
                "application/json");
            using var response = await client
                .PostAsync(new Uri($"http://{authority}/json/cfg"), content, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "WLED {Host} rejected the request to turn off \"force max brightness\": {StatusCode}.",
                    authority,
                    response.StatusCode);
                return false;
            }

            _logger.LogInformation("Turned off \"force max brightness\" on WLED {Host}.", authority);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or UriFormatException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(exception, "Could not reach WLED {Host} to turn off \"force max brightness\".", authority);
            return false;
        }
    }

    /// <summary>
    /// Sets the first configured LED output's RGBW mode to Manual (0) --
    /// the only mode this plugin's own RGBW32 output is correct under,
    /// since anything else has WLED deriving or subtracting its own white
    /// value from what this plugin already computed. Same opt-in gate and
    /// fixed-length request body as <see cref="TryDisableForceMaxBrightnessAsync"/>.
    /// </summary>
    public async Task<bool> TryFixRgbwModeAsync(string host, int port, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var authority = port == 80 ? host : string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            var client = _httpClientFactory.CreateClient();
            using var content = new StringContent(
                """{"hw":{"led":{"ins":[{"rgbwm":0}]}}}""",
                System.Text.Encoding.UTF8,
                "application/json");
            using var response = await client
                .PostAsync(new Uri($"http://{authority}/json/cfg"), content, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "WLED {Host} rejected the request to set RGBW mode to Manual: {StatusCode}.",
                    authority,
                    response.StatusCode);
                return false;
            }

            _logger.LogInformation("Set RGBW mode to Manual on WLED {Host}.", authority);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or UriFormatException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(exception, "Could not reach WLED {Host} to set RGBW mode to Manual.", authority);
            return false;
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
/// <param name="HasWhiteChannelHardware">
/// Read from <c>hw.led.ins[].type</c> -- true only for a chipset id this
/// plugin has confirmed carries a physical white diode (currently SK6812
/// RGBW). A strip with a different RGBW chipset may still have one and just
/// go undetected; the settings page's "send white channel" checkbox is
/// always the operator's own manual override regardless of this value.
/// </param>
public sealed record WledRealtimeSettings(
    Rgb24Encoding Encoding,
    bool ForcesMaxBrightness,
    int MaxPowerMilliamps,
    bool HasWhiteChannelHardware,
    int? LedFramesPerSecond,
    int? RealtimeTimeoutMilliseconds,
    bool RgbwModeIsMisconfigured);

/// <summary>Reachability and temporary realtime ownership reported by WLED.</summary>
public sealed record WledControllerStatus(bool IsOnline, bool IsRealtimeActive, string? Name = null)
{
    public static WledControllerStatus Offline { get; } = new(false, false, null);
}

/// <summary>Read-only WLED discovery data shown on the settings page.</summary>
public sealed record WledDiscoveryResult(string Host, string Name, int LedCount, string Version);
