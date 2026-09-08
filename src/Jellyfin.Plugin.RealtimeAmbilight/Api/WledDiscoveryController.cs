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
    /// <summary>A calibration photo, once fetched, rarely changes; the fixed 19-photo set costs a bounded amount of memory to keep hot.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> PhotoCache = new();

    private readonly JellyfinWledOutputService _outputService;
    private readonly IHttpClientFactory _httpClientFactory;

    public CalibrationController(JellyfinWledOutputService outputService, IHttpClientFactory httpClientFactory)
    {
        _outputService = outputService ?? throw new ArgumentNullException(nameof(outputService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    /// <summary>
    /// A TV browser opens this once, without a Jellyfin login, and leaves it
    /// open for the whole wizard: it polls <see cref="GetWizardState"/> every
    /// 1.5 s and updates itself in place, so "Next" on the settings page never
    /// requires touching the TV again -- and opening the link at all is enough
    /// to (re)start the wizard at White, see <see cref="GetWizardState"/>.
    /// Also reachable at the short <c>/amb</c> alias: this address is typed on
    /// a remote control one letter at a time, so its length matters.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("Pattern")]
    [HttpGet("/amb")]
    [Produces("text/html")]
    public ActionResult Pattern()
    {
        const string html = """
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Ambilight calibration</title>
            <style>
              * { box-sizing:border-box } html,body { width:100%;height:100%;margin:0;background:#060914;overflow:hidden }
              img, .whiteBlock { position:fixed;inset:0;width:100%;height:100%;object-fit:cover;display:block }
              .whiteBlock { background:#f4f4f2 }
              .label { position:fixed;left:50%;top:5vh;transform:translateX(-50%);z-index:4;color:#fff;font:600 clamp(14px,2vw,27px) system-ui,sans-serif;text-align:center;letter-spacing:.04em;text-shadow:0 2px 10px #000 }
              .credit { position:fixed;left:0;right:0;bottom:0;padding:1.4em 1.6em;z-index:2;background:linear-gradient(transparent,rgba(0,0,0,.72));color:#fff;font:500 clamp(11px,1.3vw,16px) system-ui,sans-serif;text-align:right }
              .credit a { color:#fff }
              .hint { position:fixed;right:1.2em;bottom:1.2em;z-index:5;padding:.5em 1em;border-radius:1.4em;background:rgba(0,0,0,.55);color:#fff;font:600 clamp(11px,1.2vw,15px) system-ui,sans-serif;text-shadow:0 1px 4px #000 }
              [hidden] { display:none !important }
            </style></head><body>
            <div class="whiteBlock" id="whiteBlock" hidden></div>
            <img id="photo" alt="" hidden />
            <div class="credit" id="credit" hidden></div>
            <div class="label" id="label">Loading&hellip;</div>
            <div class="hint">Continue on your phone &rarr;</div>
            <canvas id="canvas" width="320" height="180" hidden></canvas>
            <script>
              const whiteBlock = document.getElementById("whiteBlock");
              const photo = document.getElementById("photo");
              const credit = document.getElementById("credit");
              const label = document.getElementById("label");
              const canvas = document.getElementById("canvas");
              let lastKey = "";
              function pick(state, name) {
                return state[name] ?? state[name[0].toLowerCase() + name.slice(1)];
              }
              function uploadSample() {
                try {
                  const ctx = canvas.getContext("2d");
                  ctx.drawImage(photo, 0, 0, canvas.width, canvas.height);
                  const pixels = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
                  fetch(`/RealtimeAmbilight/Calibration/PhotoFrame?width=${canvas.width}&height=${canvas.height}`, {
                    method: "POST",
                    headers: { "Content-Type": "application/octet-stream" },
                    body: pixels
                  }).catch(() => {});
                } catch (error) {
                  // A same-origin image should never taint the canvas; if it
                  // somehow does, the photo still displays, it just cannot drive
                  // the LEDs from its own edges.
                }
              }
              function render(state) {
                const colourName = pick(state, "ColourName");
                const photoUrl = pick(state, "PhotoUrl");
                const isWhite = !photoUrl;
                whiteBlock.hidden = !isWhite;
                photo.hidden = isWhite;
                credit.hidden = isWhite;
                if (!isWhite) {
                  if (photo.src !== photoUrl) {
                    photo.onload = uploadSample;
                    photo.src = photoUrl;
                  }
                  credit.innerHTML = `Photo by <a href="${pick(state, "CreditProfileUrl")}" target="_blank" rel="noopener">${pick(state, "CreditName")}</a> on <a href="${pick(state, "CreditSourceUrl")}" target="_blank" rel="noopener">Wallhaven</a>`;
                }
                label.textContent = `${colourName} tuning · step ${pick(state, "StepIndex") + 1} of ${pick(state, "StepCount")}`;
              }
              async function tick() {
                try {
                  const response = await fetch("/RealtimeAmbilight/Calibration/WizardState?tv=true", { cache: "no-store" });
                  const state = await response.json();
                  const key = [pick(state, "StepIndex"), pick(state, "PhotoIndex")].join(":");
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

    /// <summary>
    /// Proxies one curated Wallhaven photo through the plugin's own origin.
    /// Two independent reasons this has to be same-origin, not a raw link to
    /// Wallhaven's CDN: the TV's canvas cannot read pixels from a cross-origin
    /// image with no CORS header (Wallhaven sends none, confirmed this
    /// session -- it would silently throw on every sample), and the operator
    /// should not need internet access on the TV's own network path once the
    /// photo is cached here.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("Photo/{colour}/{index:int}")]
    public async Task<IActionResult> GetPhotoAsync(string colour, int index, CancellationToken cancellationToken)
    {
        var photo = CalibrationWizard.PhotoAt(colour, index);
        if (photo is null)
        {
            return NotFound();
        }

        var cacheKey = photo.ImageUrl;
        if (PhotoCache.TryGetValue(cacheKey, out var cached))
        {
            return File(cached.Bytes, cached.ContentType);
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(new Uri(photo.ImageUrl), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return StatusCode(StatusCodes.Status502BadGateway);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
            PhotoCache[cacheKey] = (bytes, contentType);
            return File(bytes, contentType);
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// Read by the TV pattern page every 1.5 s, and by the settings page once
    /// on load. Only the TV's own poll (<paramref name="tv"/>) can restart the
    /// wizard: a poll gap over 20 s means the link was just opened fresh, so
    /// stepping through <see cref="CalibrationWizardState.NoteTvPoll"/> resets
    /// to White automatically -- opening the link is then the whole "start".
    /// </summary>
    [AllowAnonymous]
    [HttpGet("WizardState")]
    [ProducesResponseType(typeof(CalibrationWizardStateResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CalibrationWizardStateResponse>> GetWizardState(
        [FromQuery] bool tv,
        CancellationToken cancellationToken)
    {
        if (tv && _outputService.Wizard.NoteTvPoll() && _outputService.IsCalibrationPreviewActive)
        {
            // The wizard just reset to White under an already-live preview
            // (an earlier session's Stop was skipped): match the LEDs to it.
            await StartWhiteAsync(cancellationToken).ConfigureAwait(false);
        }

        return Ok(BuildWizardStateResponse());
    }

    /// <summary>
    /// Moves the wizard, so the already-open TV page picks it up on its next
    /// poll. Arriving at White auto-starts its flat preview; arriving at a
    /// photo colour does not -- the TV's own upload after it loads that photo
    /// is what starts those, independently and asynchronously.
    /// </summary>
    [HttpPost("WizardState")]
    [ProducesResponseType(typeof(CalibrationWizardStateResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CalibrationWizardStateResponse>> MoveWizardAsync(
        [FromBody] CalibrationWizardMoveRequest request,
        CancellationToken cancellationToken)
    {
        _outputService.Wizard.MoveTo(request.StepIndex, request.PhotoIndex, CalibrationSide.All);

        if (_outputService.Wizard.ColourName == "White")
        {
            await StartWhiteAsync(cancellationToken, request).ConfigureAwait(false);
        }

        return Ok(BuildWizardStateResponse());
    }

    /// <summary>
    /// Uploaded by the TV page after it draws the current photo to a canvas:
    /// full-range sRGB RGBA, downsized client-side. Anonymous like the rest of
    /// this page's own endpoints, and bounded the same way every calibration
    /// write is -- playback always wins, checked inside the service.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("PhotoFrame")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PostPhotoFrameAsync([FromQuery] int width, [FromQuery] int height, CancellationToken cancellationToken)
    {
        if (width < 16 || height < 16 || (long)width * height > 2_000_000)
        {
            return BadRequest("Frame dimensions are out of range.");
        }

        var expectedLength = checked(width * height * 4);
        if (Request.ContentLength is { } declaredLength && declaredLength != expectedLength)
        {
            return BadRequest($"Expected {expectedLength} RGBA bytes for {width}x{height}.");
        }

        using var buffer = new MemoryStream(expectedLength);
        await Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (buffer.Length != expectedLength)
        {
            return BadRequest($"Expected {expectedLength} RGBA bytes for {width}x{height} but received {buffer.Length}.");
        }

        await _outputService.ShowCalibrationPhotoAsync(buffer.ToArray(), width, height, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Re-applies fresh slider values to whatever the preview currently shows,
    /// without re-fetching or re-sampling a photo: this is what makes a slider
    /// feel live on the TV instead of requiring Save.
    /// </summary>
    [HttpPost("Retune")]
    [ProducesResponseType(typeof(CalibrationPreviewResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CalibrationPreviewResponse>> RetuneAsync(
        [FromBody] CalibrationWizardMoveRequest request,
        CancellationToken cancellationToken)
    {
        var retuned = await _outputService
            .RetuneCalibrationAsync(request.ToTuning().ToAdjustment(), cancellationToken)
            .ConfigureAwait(false);
        return Ok(new CalibrationPreviewResponse(retuned, retuned ? "Live." : "Nothing is being previewed yet."));
    }

    private async Task StartWhiteAsync(CancellationToken cancellationToken, CalibrationWizardMoveRequest? request = null)
    {
        if (!CalibrationReferenceColour.TryParse("White", out _, out _, out var white))
        {
            return;
        }

        var adjustment = request?.ToTuning().ToAdjustment() ?? PerimeterColourAdjustment.None;
        await _outputService
            .ShowCalibrationPreviewAsync(new CalibrationPreview(CalibrationSide.All, white, adjustment), cancellationToken)
            .ConfigureAwait(false);
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
        var proxiedPhotoUrl = photo is null ? null : $"/RealtimeAmbilight/Calibration/Photo/{colourName}/{wizard.PhotoIndex}";

        return new CalibrationWizardStateResponse(
            wizard.StepIndex,
            CalibrationWizard.ColourOrder.Count,
            colourName,
            htmlColour,
            wizard.Side.ToString(),
            wizard.PhotoIndex,
            photoCount,
            proxiedPhotoUrl,
            photo?.UploaderName,
            photo?.UploaderProfileUrl,
            photo?.SourcePageUrl,
            _outputService.IsCalibrationPreviewActive,
            _outputService.Wizard.TvConnected);
    }

    [HttpDelete("Preview")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> StopPreviewAsync(CancellationToken cancellationToken)
    {
        await _outputService.StopCalibrationPreviewAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

/// <summary>Moves the wizard, or retunes it, carrying the sliders' current values either way.</summary>
public sealed class CalibrationWizardMoveRequest
{
    public int StepIndex { get; set; }

    public int PhotoIndex { get; set; }

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
    bool PreviewActive,
    bool TvConnected);
