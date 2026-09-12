#pragma warning disable CA1848, CA1873
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RealtimeAmbilight.Hue;

/// <summary>
/// <see cref="HueEntertainmentService"/>'s whole dependency on <see cref="HueLightControl"/>
/// -- exists purely so a test can fake a bridge's light responses without a
/// real network call.
/// </summary>
public interface IHueLightControl
{
    Task<HueLightSnapshotEntry?> ReadStateAsync(
        string host, string certificateThumbprint, string applicationKey, Guid lightId, CancellationToken cancellationToken);

    Task TurnOnAsync(string host, string certificateThumbprint, string applicationKey, Guid lightId, CancellationToken cancellationToken);

    Task TurnOffAsync(string host, string certificateThumbprint, string applicationKey, Guid lightId, CancellationToken cancellationToken);

    Task ApplyWarmWhiteDimAsync(string host, string certificateThumbprint, string applicationKey, Guid lightId, CancellationToken cancellationToken);

    Task RestoreAsync(string host, string certificateThumbprint, string applicationKey, HueLightSnapshotEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// The plain (non-Entertainment) CLIP v2 <c>light</c> resource calls needed
/// for the end-of-session behaviour: reading each light's state once before
/// a session starts, and writing the warm-white-dim or restore payload once
/// it ends. Ordinary REST PUTs, not part of the realtime DTLS path.
/// </summary>
public sealed class HueLightControl : IHueLightControl, IDisposable
{
    private const int Port = 443;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>2700 K design target, this project's documented warm-white default.</summary>
    private const int WarmWhiteMirek = 370; // 1,000,000 / 2700 K, rounded.

    private readonly ILogger _logger;

    /// <summary>One pinned client per (host, thumbprint) pair -- see <see cref="HueBridgeClient"/>'s own field of the same shape for the full reasoning.</summary>
    private readonly ConcurrentDictionary<(string Host, string Thumbprint), HttpClient> _pinnedClients = new();

    public HueLightControl(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Dispose()
    {
        foreach (var client in _pinnedClients.Values)
        {
            client.Dispose();
        }

        _pinnedClients.Clear();
    }

    public async Task<HueLightSnapshotEntry?> ReadStateAsync(
        string host, string certificateThumbprint, string applicationKey, Guid lightId, CancellationToken cancellationToken)
    {
        var client = CreateClient(host, certificateThumbprint);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{host}:{Port}/clip/v2/resource/light/{lightId}"));
            request.Headers.Add("hue-application-key", applicationKey);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                return null;
            }

            var light = data[0];
            var on = light.TryGetProperty("on", out var onElement) && onElement.TryGetProperty("on", out var onValue) && onValue.ValueKind == JsonValueKind.True;
            // CLIP v2 legitimately reports several of these as JSON null --
            // color_temperature.mirek is null whenever the light is not
            // currently in colour-temperature mode (it is in xy mode
            // instead), for example. JsonElement.TryGetDouble/TryGetInt32
            // throw InvalidOperationException on a Null-kind element rather
            // than returning false, unlike TryGetProperty's own existence
            // check -- confirmed live, this crashed every single connect
            // attempt once a light's state could actually be read at all
            // (previously masked by the wrong-light-id 404 bug never letting
            // this code run this far). Every numeric read here must check
            // ValueKind first.
            double? brightness = light.TryGetProperty("dimming", out var dimming)
                && dimming.TryGetProperty("brightness", out var brightnessValue)
                && brightnessValue.ValueKind == JsonValueKind.Number
                && brightnessValue.TryGetDouble(out var parsedBrightness)
                ? parsedBrightness
                : null;
            var colorMode = light.TryGetProperty("color_mode", out var modeElement) && modeElement.ValueKind == JsonValueKind.String
                ? modeElement.GetString()
                : null;
            (double X, double Y)? xy = light.TryGetProperty("color", out var color)
                && color.TryGetProperty("xy", out var xyElement)
                && xyElement.ValueKind == JsonValueKind.Object
                && xyElement.TryGetProperty("x", out var xValue) && xValue.ValueKind == JsonValueKind.Number
                && xyElement.TryGetProperty("y", out var yValue) && yValue.ValueKind == JsonValueKind.Number
                && xValue.TryGetDouble(out var x) && yValue.TryGetDouble(out var y)
                ? (x, y)
                : null;
            int? mirek = light.TryGetProperty("color_temperature", out var ct)
                && ct.TryGetProperty("mirek", out var mirekValue)
                && mirekValue.ValueKind == JsonValueKind.Number
                && mirekValue.TryGetInt32(out var parsedMirek)
                ? parsedMirek
                : null;

            return new HueLightSnapshotEntry(lightId, on, brightness, colorMode, xy, mirek, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(exception, "Could not read the pre-session state of Hue light {LightId}.", lightId);
            return null;
        }
    }

    /// <summary>
    /// Turns a light on with no other change to its colour or brightness.
    /// Entertainment streaming updates a light's colour but never its power
    /// state -- confirmed against Philips's own Entertainment API guidance
    /// and live behaviour here: a light that is off when the DTLS stream
    /// starts stays dark for the whole session no matter what colours are
    /// sent to it. Called once per session, only for lights the pre-session
    /// snapshot found off, so <see cref="RestoreAsync"/> turning them back
    /// off afterward (it already does, from that same snapshot) is undoing
    /// exactly this and nothing else.
    /// </summary>
    public Task TurnOnAsync(string host, string certificateThumbprint, string applicationKey, Guid lightId, CancellationToken cancellationToken)
        => PutAsync(host, certificateThumbprint, applicationKey, lightId, """{"on":{"on":true}}""", cancellationToken);

    /// <summary>The <see cref="Core.Hue.HueEndBehaviour.TurnOff"/> end-of-session behaviour: off, regardless of what the light was doing before the session.</summary>
    public Task TurnOffAsync(string host, string certificateThumbprint, string applicationKey, Guid lightId, CancellationToken cancellationToken)
        => PutAsync(host, certificateThumbprint, applicationKey, lightId, """{"on":{"on":false}}""", cancellationToken);

    public Task ApplyWarmWhiteDimAsync(string host, string certificateThumbprint, string applicationKey, Guid lightId, CancellationToken cancellationToken)
        => PutAsync(
            host,
            certificateThumbprint,
            applicationKey,
            lightId,
            "{\"on\":{\"on\":true},\"dimming\":{\"brightness\":15},\"color_temperature\":{\"mirek\":" + WarmWhiteMirek.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}",
            cancellationToken);

    public Task RestoreAsync(string host, string certificateThumbprint, string applicationKey, HueLightSnapshotEntry entry, CancellationToken cancellationToken)
    {
        if (!entry.On)
        {
            return PutAsync(host, certificateThumbprint, applicationKey, entry.LightId, """{"on":{"on":false}}""", cancellationToken);
        }

        var payload = new Dictionary<string, object>
        {
            ["on"] = new Dictionary<string, object> { ["on"] = true },
        };

        if (entry.BrightnessPercent is { } brightness)
        {
            payload["dimming"] = new Dictionary<string, object> { ["brightness"] = brightness };
        }

        if (entry.XyColor is { } xy)
        {
            payload["color"] = new Dictionary<string, object>
            {
                ["xy"] = new Dictionary<string, object> { ["x"] = xy.X, ["y"] = xy.Y },
            };
        }
        else if (entry.ColorTemperatureMirek is { } mirek)
        {
            payload["color_temperature"] = new Dictionary<string, object> { ["mirek"] = mirek };
        }

        return PutAsync(host, certificateThumbprint, applicationKey, entry.LightId, JsonSerializer.Serialize(payload), cancellationToken);
    }

    private async Task PutAsync(string host, string certificateThumbprint, string applicationKey, Guid lightId, string json, CancellationToken cancellationToken)
    {
        var client = CreateClient(host, certificateThumbprint);
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"https://{host}:{Port}/clip/v2/resource/light/{lightId}")) { Content = content };
            request.Headers.Add("hue-application-key", applicationKey);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Hue bridge {Host} rejected the end-of-session update for light {LightId}: {StatusCode}.", host, lightId, response.StatusCode);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(exception, "Could not send the end-of-session update to Hue light {LightId}.", lightId);
        }
    }

    private HttpClient CreateClient(string host, string certificateThumbprint)
        => _pinnedClients.GetOrAdd((host, certificateThumbprint), key =>
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && string.Equals(HueBridgeClient.Thumbprint(certificate), key.Thumbprint, StringComparison.OrdinalIgnoreCase),
            };
            return new HttpClient(handler) { Timeout = RequestTimeout };
        });
}
