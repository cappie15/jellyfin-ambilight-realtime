using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.RealtimeAmbilight.Api;

/// <summary>Read-only WLED discovery endpoint used by the plugin settings page.</summary>
/// <remarks>
/// Discovery sends multicast queries and connects to hosts on the local network,
/// so it is restricted to administrators with the same policy Jellyfin uses for
/// its own device endpoints.
/// </remarks>
[ApiController]
[Route("RealtimeAmbilight/Discovery")]
[Authorize(Policy = "RequiresElevation")]
public sealed class WledDiscoveryController : ControllerBase
{
    private readonly WledDiscoveryService _discoveryService;

    public WledDiscoveryController(WledDiscoveryService discoveryService)
    {
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
    }

    /// <summary>Returns local WLED controllers that answer the read-only info endpoint.</summary>
    [HttpGet("Wled")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<WledDiscoveryResult>>> GetWledAsync(CancellationToken cancellationToken)
    {
        return Ok(await _discoveryService.DiscoverAsync(cancellationToken).ConfigureAwait(false));
    }
}
