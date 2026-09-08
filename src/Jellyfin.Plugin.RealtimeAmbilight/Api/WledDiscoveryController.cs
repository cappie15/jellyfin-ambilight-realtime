using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

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

    /// <summary>
    /// Reports controller settings that visibly change how the Ambilight looks,
    /// so the settings page can warn about them instead of leaving the operator
    /// to wonder why the LEDs ignore every brightness control they can find.
    /// </summary>
    [HttpGet("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<WledRealtimeSettings?>> GetSettingsAsync([FromQuery] string host, [FromQuery] int port, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return Ok(null as WledRealtimeSettings);
        }

        return Ok(await _discoveryService
            .ReadRealtimeSettingsAsync(host, Math.Clamp(port, 1, ushort.MaxValue), cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>Reports whether the configured WLED is reachable and in realtime mode.</summary>
    [HttpGet("Status")]
    [ProducesResponseType(typeof(WledControllerStatus), StatusCodes.Status200OK)]
    public async Task<ActionResult<WledControllerStatus>> GetStatusAsync([FromQuery] string host, [FromQuery] int port, CancellationToken cancellationToken)
    {
        return Ok(await _discoveryService
            .ReadStatusAsync(host, Math.Clamp(port, 1, ushort.MaxValue), cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>Returns local WLED controllers that answer the read-only info endpoint.</summary>
    [HttpGet("Wled")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<WledDiscoveryResult>>> GetWledAsync(CancellationToken cancellationToken)
    {
        return Ok(await _discoveryService.DiscoverAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Turns off WLED's "force max brightness" for realtime data, so the
    /// operator need not log into WLED separately to fix it. The one WLED
    /// write this plugin ever offers, and only once the operator has opted in;
    /// the ABL power budget is never part of the request and this endpoint
    /// cannot change it.
    /// </summary>
    [HttpPost("FixForceMaxBrightness")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> FixForceMaxBrightnessAsync([FromQuery] string host, [FromQuery] int port, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.AllowWledControl != true)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Turn on \"Allow this plugin to fix WLED settings\" first.");
        }

        var fixedIt = await _discoveryService
            .TryDisableForceMaxBrightnessAsync(host, Math.Clamp(port, 1, ushort.MaxValue), cancellationToken)
            .ConfigureAwait(false);
        return fixedIt
            ? NoContent()
            : StatusCode(StatusCodes.Status502BadGateway, "WLED did not accept the change.");
    }
}

/// <summary>
/// A deliberately small, local calibration surface. It never writes WLED
/// configuration; it sends temporary realtime pixels through the hosted output
/// service, exactly like ordinary Ambilight playback does.
/// </summary>
[ApiController]
[Route("RealtimeAmbilight/Calibration")]
[Authorize(Policy = "RequiresElevation")]
public sealed class CalibrationController : ControllerBase
{
    private readonly JellyfinWledOutputService _outputService;

    public CalibrationController(JellyfinWledOutputService outputService)
    {
        _outputService = outputService ?? throw new ArgumentNullException(nameof(outputService));
    }

    /// <summary>
    /// A TV browser can open this without a Jellyfin login. Its values are
    /// strictly parsed below, and it contains no account or server data.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("Pattern")]
    [Produces("text/html")]
    public ActionResult Pattern([FromQuery] string? side, [FromQuery] string? colour)
    {
        var parsedSide = Enum.TryParse<CalibrationSide>(side, true, out var result)
            ? result
            : CalibrationSide.Top;
        if (!CalibrationReferenceColour.TryParse(colour, out var colourName, out var htmlColour, out _))
        {
            if (!CalibrationReferenceColour.TryParse("white", out colourName, out htmlColour, out _))
            {
                throw new InvalidOperationException("The built-in white calibration colour is missing.");
            }
        }

        var selected = parsedSide.ToString().ToLowerInvariant();
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Ambilight calibration</title>
            <style>
              * { box-sizing:border-box } html,body { width:100%;height:100%;margin:0;background:#060914;overflow:hidden }
              .art { position:fixed;inset:18vh 18vw;border-radius:2vmin;overflow:hidden;background:radial-gradient(circle at 18% 20%,#ff5e8a 0 4%,transparent 25%),radial-gradient(circle at 80% 17%,#ffca4b 0 5%,transparent 25%),radial-gradient(circle at 72% 74%,#3be0df 0 5%,transparent 28%),radial-gradient(circle at 22% 78%,#8667ff 0 4%,transparent 28%),linear-gradient(135deg,#102653,#4b276d 43%,#093d56);box-shadow:0 2vmin 8vmin #000 }
              .art:before { content:"";position:absolute;inset:-18%;background:conic-gradient(from 20deg at 50% 50%,transparent,#ff4e9a55,transparent,#37d9ee66,transparent,#ffd05355,transparent);filter:blur(2vmin);animation:drift 16s linear infinite }
              .art:after { content:"";position:absolute;inset:0;background:linear-gradient(115deg,transparent 37%,#ffffff18 48%,transparent 59%);mix-blend-mode:screen }
              @keyframes drift { to { transform:rotate(1turn) } }
              .edge { position:fixed;background:#000;z-index:2 } .top,.bottom { left:0;width:100%;height:16vh } .left,.right { top:0;height:100%;width:16vw }
              .top { top:0 } .right { right:0 } .bottom { bottom:0 } .left { left:0 }
              .{{selected}} { background:{{htmlColour}};z-index:3;box-shadow:0 0 5vmin {{htmlColour}} }
              .label { position:fixed;left:50%;top:50%;transform:translate(-50%,-50%);z-index:1;color:#fff;font:600 clamp(14px,2vw,27px) system-ui,sans-serif;text-align:center;letter-spacing:.04em;text-shadow:0 2px 8px #000 }
              .label small { display:block;margin-top:.5em;font-size:.52em;font-weight:500;opacity:.72;letter-spacing:.12em;text-transform:uppercase }
            </style></head><body><div class="art"></div><div class="edge top"></div><div class="edge right"></div><div class="edge bottom"></div><div class="edge left"></div>
            <div class="label">Color calibration<br><small>Match the {{parsedSide}} glow · {{colourName}}</small></div></body></html>
            """;
        return Content(html, "text/html; charset=utf-8");
    }

    [HttpPost("Preview")]
    [ProducesResponseType(typeof(CalibrationPreviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CalibrationPreviewResponse>> PreviewAsync(
        [FromBody] CalibrationPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<CalibrationSide>(request.Side, true, out var side)
            || !CalibrationReferenceColour.TryParse(request.Colour, out _, out _, out var colour))
        {
            return BadRequest("Choose a valid side and reference colour.");
        }

        var started = await _outputService
            .ShowCalibrationPreviewAsync(new CalibrationPreview(side, colour, request.ToTuning().ToAdjustment()), cancellationToken)
            .ConfigureAwait(false);
        if (!started)
        {
            return Conflict(new CalibrationPreviewResponse(false, "Stop playback before starting calibration; playback always has priority."));
        }

        return Ok(new CalibrationPreviewResponse(true, "Preview is live. Adjust the selected side, then save when it matches."));
    }

    [HttpDelete("Preview")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> StopPreviewAsync(CancellationToken cancellationToken)
    {
        await _outputService.StopCalibrationPreviewAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

public sealed class CalibrationPreviewRequest
{
    public string Side { get; set; } = "Top";

    public string Colour { get; set; } = "White";

    public string WallColourHex { get; set; } = "#ffffff";

    public Dictionary<string, int> Tuning { get; set; } = [];

    public PerimeterColourTuning ToTuning()
    {
        int Value(string name, int fallback = 100)
            => Tuning.TryGetValue(name, out var value) ? value : fallback;

        return new PerimeterColourTuning(
            Value("BrightnessPercent"), Value("SaturationPercent"),
            Value("RedGainPercent"), Value("GreenGainPercent"), Value("BlueGainPercent"),
            Value("BlackLevelFloorPercent", 0),
            WallColourHex, Value("WallColourCorrectionPercent"),
            Value("TopBrightnessPercent"), Value("TopRedGainPercent"), Value("TopGreenGainPercent"), Value("TopBlueGainPercent"),
            Value("RightBrightnessPercent"), Value("RightRedGainPercent"), Value("RightGreenGainPercent"), Value("RightBlueGainPercent"),
            Value("BottomBrightnessPercent"), Value("BottomRedGainPercent"), Value("BottomGreenGainPercent"), Value("BottomBlueGainPercent"),
            Value("LeftBrightnessPercent"), Value("LeftRedGainPercent"), Value("LeftGreenGainPercent"), Value("LeftBlueGainPercent"));
    }
}

public sealed record CalibrationPreviewResponse(bool Active, string Message);
