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
    /// A TV browser can open this once, without a Jellyfin login, and leave it
    /// open for the whole wizard: it polls <see cref="GetWizardState"/> and
    /// updates itself, so "Next" on the settings page never requires touching
    /// the TV again. The centred photograph and its credit are decorative --
    /// Ambilight only ever samples the solid edge glow, never the photo -- so
    /// they are positioned to never reach the sampled band at the picture's edge.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("Pattern")]
    [Produces("text/html")]
    public ActionResult Pattern()
    {
        const string html = """
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Ambilight calibration</title>
            <style>
              * { box-sizing:border-box } html,body { width:100%;height:100%;margin:0;background:#060914;overflow:hidden }
              .centre { position:fixed;inset:18vh 18vw;border-radius:2vmin;overflow:hidden;background:#111;box-shadow:0 2vmin 8vmin #000 }
              .centre img { width:100%;height:100%;object-fit:cover;display:block }
              .whiteBlock { position:absolute;inset:0;background:#f4f4f2 }
              .edge { position:fixed;background:#000;z-index:2;transition:background .25s } .top,.bottom { left:0;width:100%;height:16vh } .left,.right { top:0;height:100%;width:16vw }
              .top { top:0 } .right { right:0 } .bottom { bottom:0 } .left { left:0 }
              .edge.active { z-index:3 }
              .label { position:fixed;left:50%;top:6vh;transform:translateX(-50%);z-index:4;color:#fff;font:600 clamp(14px,2vw,27px) system-ui,sans-serif;text-align:center;letter-spacing:.04em;text-shadow:0 2px 8px #000 }
              .label small { display:block;margin-top:.5em;font-size:.52em;font-weight:500;opacity:.72;letter-spacing:.12em;text-transform:uppercase }
              .credit { position:absolute;left:0;right:0;bottom:0;padding:1.4em 1.6em;z-index:1;background:linear-gradient(transparent,rgba(0,0,0,.72));color:#fff;font:500 clamp(11px,1.3vw,16px) system-ui,sans-serif;text-align:right }
              .credit a { color:#fff }
              [hidden] { display:none !important }
            </style></head><body>
            <div class="edge top" id="edgeTop"></div><div class="edge right" id="edgeRight"></div><div class="edge bottom" id="edgeBottom"></div><div class="edge left" id="edgeLeft"></div>
            <div class="centre" id="centre">
              <div class="whiteBlock" id="whiteBlock" hidden></div>
              <img id="photo" alt="" hidden />
              <div class="credit" id="credit" hidden></div>
            </div>
            <div class="label" id="label">Color calibration</div>
            <script>
              const edges = { Top: document.getElementById("edgeTop"), Right: document.getElementById("edgeRight"), Bottom: document.getElementById("edgeBottom"), Left: document.getElementById("edgeLeft") };
              const whiteBlock = document.getElementById("whiteBlock");
              const photo = document.getElementById("photo");
              const credit = document.getElementById("credit");
              const label = document.getElementById("label");
              let lastKey = "";
              function pick(state, name) {
                return state[name] ?? state[name[0].toLowerCase() + name.slice(1)];
              }
              function render(state) {
                const side = pick(state, "Side");
                const htmlColour = pick(state, "HtmlColour");
                const photoUrl = pick(state, "PhotoUrl");
                Object.entries(edges).forEach(([edgeSide, el]) => {
                  const isActive = edgeSide === side;
                  el.classList.toggle("active", isActive);
                  el.style.background = isActive ? htmlColour : "#000";
                  el.style.boxShadow = isActive ? `0 0 5vmin ${htmlColour}` : "none";
                });
                const isWhite = !photoUrl;
                whiteBlock.hidden = !isWhite;
                photo.hidden = isWhite;
                credit.hidden = isWhite;
                if (!isWhite) {
                  photo.src = photoUrl;
                  credit.innerHTML = `Photo by <a href="${pick(state, "CreditProfileUrl")}" target="_blank" rel="noopener">${pick(state, "CreditName")}</a> on <a href="${pick(state, "CreditSourceUrl")}" target="_blank" rel="noopener">Wallhaven</a>`;
                }
                label.innerHTML = `Color calibration &middot; step ${pick(state, "StepIndex") + 1} of ${pick(state, "StepCount")}<br><small>Match the ${side} glow &middot; ${pick(state, "ColourName")}</small>`;
              }
              async function tick() {
                try {
                  const response = await fetch("WizardState", { cache: "no-store" });
                  const state = await response.json();
                  const key = [pick(state, "StepIndex"), pick(state, "PhotoIndex"), pick(state, "Side")].join(":");
                  if (key === lastKey) { return; }
                  lastKey = key;
                  render(state);
                } catch (error) {
                  // Keep showing the last known state; the next tick tries again.
                }
              }
              tick();
              setInterval(tick, 1500);
            </script>
            </body></html>
            """;
        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>Read by the TV pattern page; carries nothing beyond what that page already showed as query parameters before.</summary>
    [AllowAnonymous]
    [HttpGet("WizardState")]
    [ProducesResponseType(typeof(CalibrationWizardStateResponse), StatusCodes.Status200OK)]
    public ActionResult<CalibrationWizardStateResponse> GetWizardState()
    {
        return Ok(BuildWizardStateResponse());
    }

    /// <summary>
    /// Moves the wizard, so the already-open TV page picks it up on its next
    /// poll, and -- only if a preview is already running -- restarts it with
    /// the new step's colour so the LEDs stay in sync without an extra click.
    /// </summary>
    [HttpPost("WizardState")]
    [ProducesResponseType(typeof(CalibrationWizardStateResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CalibrationWizardStateResponse>> MoveWizardAsync(
        [FromBody] CalibrationWizardMoveRequest request,
        CancellationToken cancellationToken)
    {
        var side = Enum.TryParse<CalibrationSide>(request.Side, true, out var parsedSide) ? parsedSide : CalibrationSide.Top;
        _outputService.Wizard.MoveTo(request.StepIndex, request.PhotoIndex, side);

        if (_outputService.IsCalibrationPreviewActive
            && CalibrationReferenceColour.TryParse(_outputService.Wizard.ColourName, out _, out _, out var colour))
        {
            await _outputService
                .ShowCalibrationPreviewAsync(new CalibrationPreview(side, colour, request.ToTuning().ToAdjustment()), cancellationToken)
                .ConfigureAwait(false);
        }

        return Ok(BuildWizardStateResponse());
    }

    private CalibrationWizardStateResponse BuildWizardStateResponse()
    {
        var wizard = _outputService.Wizard;
        var colourName = wizard.ColourName;
        if (!CalibrationReferenceColour.TryParse(colourName, out _, out var htmlColour, out _))
        {
            throw new InvalidOperationException($"The built-in \"{colourName}\" calibration colour is missing.");
        }

        var photo = CalibrationWizard.PhotoAt(colourName, wizard.PhotoIndex);
        var photoCount = CalibrationWizard.Photos.TryGetValue(colourName, out var photos) ? photos.Count : 0;

        return new CalibrationWizardStateResponse(
            wizard.StepIndex,
            CalibrationWizard.ColourOrder.Count,
            colourName,
            htmlColour,
            wizard.Side.ToString(),
            wizard.PhotoIndex,
            photoCount,
            photo?.ImageUrl,
            photo?.UploaderName,
            photo?.UploaderProfileUrl,
            photo?.SourcePageUrl,
            _outputService.IsCalibrationPreviewActive);
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

    public PerimeterColourTuning ToTuning() => CalibrationTuningRequest.Build(Tuning, WallColourHex);
}

/// <summary>Moves the wizard and, if a preview is already running, carries the sliders' current values to restart it with.</summary>
public sealed class CalibrationWizardMoveRequest
{
    public int StepIndex { get; set; }

    public int PhotoIndex { get; set; }

    public string Side { get; set; } = "Top";

    public string WallColourHex { get; set; } = "#ffffff";

    public Dictionary<string, int> Tuning { get; set; } = [];

    public PerimeterColourTuning ToTuning() => CalibrationTuningRequest.Build(Tuning, WallColourHex);
}

/// <summary>Shared by both request shapes above, so the same percent-field lookup is not written out twice.</summary>
internal static class CalibrationTuningRequest
{
    public static PerimeterColourTuning Build(IReadOnlyDictionary<string, int> tuning, string wallColourHex)
    {
        int Value(string name, int fallback = 100)
            => tuning.TryGetValue(name, out var value) ? value : fallback;

        return new PerimeterColourTuning(
            Value("BrightnessPercent"), Value("SaturationPercent"),
            Value("RedGainPercent"), Value("GreenGainPercent"), Value("BlueGainPercent"),
            Value("BlackLevelFloorPercent", 0),
            wallColourHex, Value("WallColourCorrectionPercent"),
            Value("TopBrightnessPercent"), Value("TopRedGainPercent"), Value("TopGreenGainPercent"), Value("TopBlueGainPercent"),
            Value("RightBrightnessPercent"), Value("RightRedGainPercent"), Value("RightGreenGainPercent"), Value("RightBlueGainPercent"),
            Value("BottomBrightnessPercent"), Value("BottomRedGainPercent"), Value("BottomGreenGainPercent"), Value("BottomBlueGainPercent"),
            Value("LeftBrightnessPercent"), Value("LeftRedGainPercent"), Value("LeftGreenGainPercent"), Value("LeftBlueGainPercent"));
    }
}

public sealed record CalibrationPreviewResponse(bool Active, string Message);

/// <summary>The wizard state the TV pattern page polls for and the settings page reads back after moving it.</summary>
public sealed record CalibrationWizardStateResponse(
    int StepIndex,
    int StepCount,
    string ColourName,
    string HtmlColour,
    string Side,
    int PhotoIndex,
    int PhotoCount,
    string? PhotoUrl,
    string? CreditName,
    string? CreditProfileUrl,
    string? CreditSourceUrl,
    bool PreviewActive);
