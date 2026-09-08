#pragma warning disable CA1848, CA1873
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HueApi;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RealtimeAmbilight.Hue;

/// <summary>Read-only bridge facts surfaced during discovery, before any credential exists.</summary>
public sealed record HueBridgeInfo(string Host, string BridgeId, string ModelId, string SoftwareVersion, string ApiVersion, string CertificateThumbprintSha256)
{
    /// <summary>
    /// A deny-list, not an allow-list: only the round Bridge v1
    /// (<c>BSB001</c>) is excluded, since it has no Entertainment API at all
    /// and is not, and will not become, supported here. Every square Bridge
    /// model -- <c>BSB002</c> and any newer id, including <c>BSB003</c> (the
    /// Bridge Pro, confirmed against real hardware) -- has one, so checking
    /// for a specific "supported" model id would incorrectly reject a bridge
    /// this integration actually works with.
    /// </summary>
    public bool SupportsEntertainment => !string.Equals(ModelId, "BSB001", StringComparison.OrdinalIgnoreCase);
}

public sealed record HuePairingResult(bool Success, string? ApplicationKey, string? ClientKey, string? FailureReason);

/// <summary>
/// The local REST calls this plugin makes to a Hue bridge directly (not
/// through the Entertainment DTLS stream): discovery/capability read,
/// pairing, reading entertainment configurations, and end-of-session light
/// control. Every call here uses a certificate-validation strategy this
/// plugin actually chose, never <c>HttpClientHandler.DangerousAcceptAnyServerCertificateValidator</c>
/// -- see the ADR for why that specific default matters here.
/// </summary>
public sealed class HueBridgeClient
{
    private const int Port = 443;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;

    public HueBridgeClient(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Reads a candidate bridge's public, unauthenticated <c>/api/config</c>
    /// document to confirm it really is a Hue bridge and report its
    /// Entertainment capability. This is the one call in this class made
    /// before any certificate can be pinned -- there is nothing to pin to
    /// yet -- so the certificate is accepted and its thumbprint is captured
    /// for display and, if the operator goes on to pair, for pinning from
    /// that point forward.
    /// </summary>
    public async Task<HueBridgeInfo?> ProbeAsync(string host, CancellationToken cancellationToken)
    {
        string? capturedThumbprint = null;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                capturedThumbprint = certificate is null ? null : Thumbprint(certificate);
                return certificate is not null; // any certificate: nothing pinned yet during discovery.
            },
        };
        using var client = new HttpClient(handler) { Timeout = RequestTimeout };

        try
        {
            using var response = await client.GetAsync(new Uri($"https://{host}:{Port}/api/config"), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("bridgeid", out var bridgeIdElement))
            {
                return null;
            }

            return new HueBridgeInfo(
                host,
                bridgeIdElement.GetString() ?? string.Empty,
                ReadString(root, "modelid") ?? "unknown",
                ReadString(root, "swversion") ?? "unknown",
                ReadString(root, "apiversion") ?? "unknown",
                capturedThumbprint ?? string.Empty);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or UriFormatException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogDebug(exception, "Could not read /api/config from candidate Hue bridge {Host}.", host);
            return null;
        }
    }

    /// <summary>
    /// One press-link pairing attempt. Returns a failure result (not an
    /// exception) for "link button not pressed" -- the settings page polls
    /// this every couple of seconds while the operator has a few seconds to
    /// press the physical button, so that specific failure is the normal,
    /// expected outcome of most calls in that loop.
    /// </summary>
    public async Task<HuePairingResult> TryPairAsync(string host, string expectedCertificateThumbprintSha256, CancellationToken cancellationToken)
    {
        using var client = CreatePinnedClient(host, expectedCertificateThumbprintSha256);
        try
        {
            using var content = new StringContent(
                """{"devicetype":"jellyfin-realtime-ambilight#server","generateclientkey":true}""",
                Encoding.UTF8,
                "application/json");
            using var response = await client.PostAsync(new Uri($"https://{host}:{Port}/api"), content, cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return new HuePairingResult(false, null, null, "The bridge returned an unexpected response.");
            }

            var entry = document.RootElement[0];
            if (entry.TryGetProperty("success", out var success))
            {
                var username = ReadString(success, "username");
                var clientKey = ReadString(success, "clientkey");
                return username is null || clientKey is null
                    ? new HuePairingResult(false, null, null, "The bridge did not return both keys.")
                    : new HuePairingResult(true, username, clientKey, null);
            }

            if (entry.TryGetProperty("error", out var error))
            {
                return new HuePairingResult(false, null, null, ReadString(error, "description") ?? "Press the link button on the bridge and try again.");
            }

            return new HuePairingResult(false, null, null, "The bridge returned an unexpected response.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new HuePairingResult(false, null, null, "The bridge could not be reached.");
        }
    }

    public async Task<IReadOnlyList<HueEntertainmentConfiguration>> GetEntertainmentConfigurationsAsync(
        string host, string expectedCertificateThumbprintSha256, string applicationKey, CancellationToken cancellationToken)
    {
        using var client = CreatePinnedClient(host, expectedCertificateThumbprintSha256);
        client.DefaultRequestHeaders.Add("hue-application-key", applicationKey);
        using var response = await client
            .GetAsync(new Uri($"https://{host}:{Port}/clip/v2/resource/entertainment_configuration"), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return HueEntertainmentConfigurationParser.ParseList(json);
    }

    /// <summary>
    /// Maps each channel member's <c>entertainment</c> service id (from
    /// <see cref="HueEntertainmentChannel.MemberServiceIds"/>) to the actual
    /// <c>light</c> resource id it renders to -- required before any plain
    /// CLIP v2 light call (<see cref="HueLightControl"/>), since those two
    /// ids are different resources on the bridge. See
    /// <see cref="HueEntertainmentServiceParser"/> for why.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, Guid>> ResolveLightIdsAsync(
        string host, string expectedCertificateThumbprintSha256, string applicationKey, CancellationToken cancellationToken)
    {
        using var client = CreatePinnedClient(host, expectedCertificateThumbprintSha256);
        client.DefaultRequestHeaders.Add("hue-application-key", applicationKey);
        using var response = await client
            .GetAsync(new Uri($"https://{host}:{Port}/clip/v2/resource/entertainment"), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return HueEntertainmentServiceParser.ParseLightIdsByServiceId(json);
    }

    /// <summary>
    /// A properly configured <see cref="LocalHueApi"/>, for callers that need
    /// HueApi's own typed client (e.g. light state PUTs for the end-of-session
    /// behaviour) rather than this class's own hand-rolled calls -- still with
    /// this plugin's own certificate-pinned <see cref="HttpClient"/> passed in
    /// explicitly, never <see cref="LocalHueApi"/>'s own accept-any default.
    /// </summary>
    public LocalHueApi CreatePinnedLocalApi(string host, string expectedCertificateThumbprintSha256, string applicationKey)
        => new(host, applicationKey, CreatePinnedClient(host, expectedCertificateThumbprintSha256));

    private HttpClient CreatePinnedClient(string host, string expectedCertificateThumbprintSha256)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null)
                {
                    return false;
                }

                var actual = Thumbprint(certificate);
                var matches = string.Equals(actual, expectedCertificateThumbprintSha256, StringComparison.OrdinalIgnoreCase);
                if (!matches)
                {
                    _logger.LogWarning(
                        "Hue bridge {Host} answered with a certificate that does not match the one pinned at pairing time (expected {Expected}, got {Actual}). Refusing the connection.",
                        host,
                        expectedCertificateThumbprintSha256,
                        actual);
                }

                return matches;
            },
        };

        return new HttpClient(handler) { Timeout = RequestTimeout };
    }

    /// <summary>
    /// SHA-256 of the leaf certificate's raw bytes -- trust-on-first-use
    /// pinning, not validation against a CA. Hue bridges use a per-device
    /// self-signed certificate rather than one issued by a public CA, so the
    /// usual chain-of-trust validation has nothing to validate against; the
    /// meaningful check here is "is this still the exact device I paired
    /// with", which a pinned thumbprint answers directly, and which changing
    /// (a different device now answering at that IP, or the bridge's cert
    /// itself being reissued) is treated as a reason to refuse, not silently
    /// proceed.
    /// </summary>
    public static string Thumbprint(X509Certificate2 certificate)
        => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
