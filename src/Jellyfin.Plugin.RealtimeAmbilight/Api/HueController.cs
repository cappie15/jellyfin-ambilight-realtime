using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Model;
using Jellyfin.Plugin.RealtimeAmbilight.Hue;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.RealtimeAmbilight.Api;

/// <summary>
/// Admin-only Hue discovery, pairing and status endpoints for the settings
/// page's Hue tab. Every mutation here is one of: discovery (read-only),
/// pairing (obtains credentials, the bridge's own supported flow), selecting
/// an existing entertainment configuration (never creating or editing one),
/// and the agreed end-of-session behaviour. Nothing here writes WLED
/// configuration, Hue lamp layout, or any other bridge setting.
/// </summary>
[ApiController]
[Route("RealtimeAmbilight/Hue")]
[Authorize(Policy = "RequiresElevation")]
public sealed class HueController : ControllerBase
{
    private readonly HueBridgeDiscoveryService _discoveryService;
    private readonly HueBridgeClient _bridgeClient;
    private readonly HueCredentialStore _credentialStore;
    private readonly HueEntertainmentService _entertainmentService;

    public HueController(
        HueBridgeDiscoveryService discoveryService,
        HueBridgeClient bridgeClient,
        HueCredentialStore credentialStore,
        HueEntertainmentService entertainmentService)
    {
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _bridgeClient = bridgeClient ?? throw new ArgumentNullException(nameof(bridgeClient));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _entertainmentService = entertainmentService ?? throw new ArgumentNullException(nameof(entertainmentService));
    }

    /// <summary>Local bridges found over mDNS, each confirmed by reading its own read-only <c>/api/config</c>.</summary>
    [HttpGet("Bridges")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<HueBridgeInfo>>> GetBridgesAsync(CancellationToken cancellationToken)
        => Ok(await _discoveryService.DiscoverAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Manual-entry fallback: probes one address directly instead of relying on mDNS.</summary>
    [HttpGet("Bridges/Probe")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HueBridgeInfo>> ProbeBridgeAsync([FromQuery] string host, CancellationToken cancellationToken)
    {
        var info = await _bridgeClient.ProbeAsync(host, cancellationToken).ConfigureAwait(false);
        return info is null ? NotFound() : Ok(info);
    }

    /// <summary>
    /// One press-link pairing attempt. The settings page calls this every
    /// couple of seconds while the operator has a few seconds to press the
    /// physical button; "link button not pressed" is a normal response here,
    /// not an error, and is returned as <see cref="HuePairingResult.Success"/>
    /// <see langword="false"/> with a human-readable reason rather than a
    /// non-2xx status.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="HuePairingResponse"/>, never the internal
    /// <see cref="HuePairingResult"/> directly: that record carries the raw
    /// application key and client key back up from the bridge so they can be
    /// saved via <see cref="HueCredentialStore"/>, and echoing it straight
    /// into the HTTP response would send those secrets to the browser for no
    /// functional reason -- the settings page never reads them, it only
    /// checks success/failure. Keeping them server-side only is the whole
    /// point of <see cref="HueCredentialStore"/> existing outside
    /// <c>PluginConfiguration</c> in the first place; this endpoint must not
    /// undo that by handing them out itself.
    /// </remarks>
    [HttpPost("Pair")]
    [ProducesResponseType(typeof(HuePairingResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<HuePairingResponse>> PairAsync([FromQuery] string host, CancellationToken cancellationToken)
    {
        var probe = await _bridgeClient.ProbeAsync(host, cancellationToken).ConfigureAwait(false);
        if (probe is null)
        {
            return Ok(new HuePairingResponse(false, "The bridge could not be reached."));
        }

        if (!probe.SupportsEntertainment)
        {
            return Ok(new HuePairingResponse(false, $"This bridge ({probe.ModelId}) has no Entertainment API. Only the square Bridge (model BSB002 or newer) is supported."));
        }

        var result = await _bridgeClient.TryPairAsync(host, probe.CertificateThumbprintSha256, cancellationToken).ConfigureAwait(false);
        if (result.Success && result.ApplicationKey is not null && result.ClientKey is not null)
        {
            var credentials = new HueCredentials(probe.BridgeId, result.ApplicationKey, result.ClientKey, probe.CertificateThumbprintSha256);
            await _credentialStore.SaveAsync(credentials, cancellationToken).ConfigureAwait(false);
            _entertainmentService.NotifyCredentialsChanged(credentials);

            var configuration = Plugin.Instance!.Configuration;
            configuration.HueBridgeHost = host;
            configuration.HueBridgeId = probe.BridgeId;
            Plugin.Instance.SaveConfiguration();
        }

        return Ok(new HuePairingResponse(result.Success, result.FailureReason));
    }

    /// <summary>Entertainment configurations already on the paired bridge -- never created or edited here.</summary>
    [HttpGet("EntertainmentConfigurations")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IReadOnlyList<HueEntertainmentConfiguration>>> GetEntertainmentConfigurationsAsync(CancellationToken cancellationToken)
    {
        var credentials = await _credentialStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var host = Plugin.Instance?.Configuration.HueBridgeHost;
        if (credentials is null || string.IsNullOrWhiteSpace(host))
        {
            return Conflict("Pair with a bridge first.");
        }

        var configurations = await _bridgeClient
            .GetEntertainmentConfigurationsAsync(host, credentials.CertificateThumbprintSha256, credentials.ApplicationKey, cancellationToken)
            .ConfigureAwait(false);
        return Ok(configurations);
    }

    /// <summary>Selects an existing entertainment configuration and saves the operator's Hue preferences.</summary>
    [HttpPost("Select")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Select([FromBody] HueSelectionRequest request)
    {
        var configuration = Plugin.Instance!.Configuration;
        configuration.HueEnabled = request.Enabled;
        configuration.HueEntertainmentConfigurationId = request.EntertainmentConfigurationId;
        configuration.HueEntertainmentConfigurationName = request.EntertainmentConfigurationName ?? string.Empty;
        configuration.HueBrightnessPercent = Math.Clamp(request.BrightnessPercent, 1, 100);
        configuration.HueEndBehaviour = request.EndBehaviour;
        Plugin.Instance.SaveConfiguration();
        return NoContent();
    }

    [HttpGet("Status")]
    [ProducesResponseType(typeof(HueStatus), StatusCodes.Status200OK)]
    public ActionResult<HueStatus> GetStatus() => Ok(_entertainmentService.GetStatus());

    /// <summary>Forgets the stored credentials. Does not touch the bridge itself; the operator removes the app from the Hue app if they want the bridge side undone too.</summary>
    /// <summary>
    /// Lights one entertainment channel and blacks out the rest, so it can be
    /// seen which part of which physical strip that channel actually drives.
    /// Pass no channel to clear the override again.
    /// </summary>
    /// <remarks>
    /// Rides along on an existing stream rather than connecting one, so
    /// something must be playing on the bound television. Returns 409 when
    /// nothing is streaming. This exists because where a channel lands cannot
    /// be derived from the bridge's own data once a gradient strip has been
    /// physically shortened, and guessing at it has now cost three separate
    /// rounds of chasing Hue position bugs.
    /// </remarks>
    [HttpPost("Identify")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult Identify([FromQuery] int? channel, [FromQuery] string? colour)
    {
        if (channel is null)
        {
            return _entertainmentService.TrySetChannelOverride(null) ? NoContent() : Conflict("Hue is not streaming.");
        }

        var lit = ParseColour(colour);
        var map = _entertainmentService.ActiveChannelIds.ToDictionary(id => id, id => id == channel.Value ? lit : default);
        return _entertainmentService.TrySetChannelOverride(map) ? NoContent() : Conflict("Hue is not streaming.");
    }

    /// <summary>Hex <c>rrggbb</c> read as full-range sRGB, defaulting to white. Linear light, like everything downstream.</summary>
    private static LinearRgb ParseColour(string? colour)
    {
        var text = (colour ?? "ffffff").TrimStart('#');
        if (text.Length != 6 || !int.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            text = "ffffff";
        }

        static float Linear(string text, int offset)
        {
            var encoded = Convert.ToInt32(text.Substring(offset, 2), 16) / 255f;
            return encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
        }

        return new LinearRgb(Linear(text, 0), Linear(text, 2), Linear(text, 4));
    }

    [HttpPost("Unlink")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Unlink()
    {
        _credentialStore.Delete();
        _entertainmentService.NotifyCredentialsChanged(null);
        var configuration = Plugin.Instance!.Configuration;
        configuration.HueEnabled = false;
        configuration.HueEntertainmentConfigurationId = Guid.Empty;
        configuration.HueEntertainmentConfigurationName = string.Empty;
        Plugin.Instance.SaveConfiguration();
        return NoContent();
    }
}

/// <summary>
/// What the settings page is allowed to know about a pairing attempt --
/// never the application key or client key. See the remarks on
/// <see cref="HueController.PairAsync"/> for why.
/// </summary>
public sealed record HuePairingResponse(bool Success, string? FailureReason);

public sealed class HueSelectionRequest
{
    public bool Enabled { get; set; }

    public Guid EntertainmentConfigurationId { get; set; }

    public string? EntertainmentConfigurationName { get; set; }

    public int BrightnessPercent { get; set; } = 100;

    public Core.Hue.HueEndBehaviour EndBehaviour { get; set; } = Core.Hue.HueEndBehaviour.WarmWhiteDim;
}
