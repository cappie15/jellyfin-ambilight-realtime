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
    /// <summary>Resource name prefix built from the plugin's embedded calibration photos folder.</summary>
    private const string PhotoResourcePrefix = "Jellyfin.Plugin.RealtimeAmbilight.Configuration.CalibrationPhotos.";

    /// <summary>Read once per file name and kept hot: the fixed 19-photo set is a bounded, small amount of memory.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> PhotoCache = new();

    private readonly JellyfinWledOutputService _outputService;

    public CalibrationController(JellyfinWledOutputService outputService)
    {
        _outputService = outputService ?? throw new ArgumentNullException(nameof(outputService));
    }

    /// <summary>
    /// A TV browser opens this once, without a Jellyfin login, and leaves it
    /// open for the whole wizard: it polls <see cref="GetWizardState"/> every
    /// 1.5 s and updates itself in place, so "Next" on the settings page never
    /// requires touching the TV again -- and opening the link at all is enough
    /// to (re)start the wizard at step one, see <see cref="GetWizardState"/>.
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
              img { position:fixed;inset:0;width:100%;height:100%;object-fit:cover;display:block }
              .label { position:fixed;left:50%;top:5vh;transform:translateX(-50%);z-index:4;color:#fff;font:600 clamp(14px,2vw,27px) system-ui,sans-serif;text-align:center;letter-spacing:.04em;text-shadow:0 2px 10px #000 }
              .hint { position:fixed;right:1.2em;bottom:1.2em;z-index:5;padding:.5em 1em;border-radius:1.4em;background:rgba(0,0,0,.55);color:#fff;font:600 clamp(11px,1.2vw,15px) system-ui,sans-serif;text-shadow:0 1px 4px #000 }
              [hidden] { display:none !important }
            </style></head><body>
            <img id="photo" alt="" hidden />
            <div class="label" id="label">Loading&hellip;</div>
            <div class="hint">Continue on your phone &rarr;</div>
            <canvas id="canvas" width="320" height="180" hidden></canvas>
            <script>
              const photo = document.getElementById("photo");
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
                const photoUrl = pick(state, "PhotoUrl");
                photo.hidden = !photoUrl;
                if (photoUrl && photo.src !== photoUrl) {
                  photo.onload = uploadSample;
                  photo.src = photoUrl;
                }
                const stepIndex = pick(state, "StepIndex");
                const isConfirmation = pick(state, "IsConfirmationStep");
                const phase = isConfirmation ? `Confirmation ${stepIndex - 6} of 10` : `Step ${stepIndex + 1} of 7`;
                label.textContent = `${phase} — ${pick(state, "ColourName")}`;
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
    /// Serves one of the operator's own calibration photos, embedded in the
    /// plugin so there is no external fetch, no attribution and no load time
    /// beyond what is already on disk. Same-origin also matters mechanically:
    /// the TV's canvas can only read pixels from a same-origin image.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("Photo/{colour}/{index:int}")]
    public IActionResult GetPhoto(string colour, int index)
    {
        var fileName = CalibrationWizard.PhotoAt(colour, index);
        if (fileName is null)
        {
            return NotFound();
        }

        if (PhotoCache.TryGetValue(fileName, out var cached))
        {
            return File(cached, "image/jpeg");
        }

        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(PhotoResourcePrefix + fileName);
        if (stream is null)
        {
            return NotFound();
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        PhotoCache[fileName] = bytes;
        return File(bytes, "image/jpeg");
    }

    /// <summary>
    /// Read by the TV pattern page every 1.5 s, and by the settings page once
    /// on load. Only the TV's own poll (<paramref name="tv"/>) can restart the
    /// wizard: a poll gap over 20 s means the link was just opened fresh, so
    /// <see cref="CalibrationWizardState.NoteTvPoll"/> resets to step one --
    /// opening the link is then the whole "start". The TV's own next photo
    /// upload re-drives the LEDs; nothing needs starting from here.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("WizardState")]
    [ProducesResponseType(typeof(CalibrationWizardStateResponse), StatusCodes.Status200OK)]
    public ActionResult<CalibrationWizardStateResponse> GetWizardState([FromQuery] bool tv)
    {
        if (tv)
        {
            _outputService.Wizard.NoteTvPoll();
        }

        return Ok(BuildWizardStateResponse());
    }

    /// <summary>
    /// Moves the wizard, so the already-open TV page picks it up on its next
    /// poll, loads that step's photo, and uploads its sampled edges -- which is
    /// what actually drives the LEDs; this endpoint only ever changes position.
    /// </summary>
    [HttpPost("WizardState")]
    [ProducesResponseType(typeof(CalibrationWizardStateResponse), StatusCodes.Status200OK)]
    public ActionResult<CalibrationWizardStateResponse> MoveWizard([FromBody] CalibrationWizardMoveRequest request)
    {
        _outputService.Wizard.MoveTo(request.StepIndex, request.PhotoIndex);
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

    private CalibrationWizardStateResponse BuildWizardStateResponse()
    {
        var wizard = _outputService.Wizard;
        var colourName = wizard.ColourName;
        var photo = CalibrationWizard.PhotoAt(colourName, wizard.PhotoIndex);
        var photoCount = CalibrationWizard.Photos.TryGetValue(colourName, out var photos) ? photos.Count : 0;
        var proxiedPhotoUrl = photo is null ? null : $"/RealtimeAmbilight/Calibration/Photo/{colourName}/{wizard.PhotoIndex}";

        return new CalibrationWizardStateResponse(
            wizard.StepIndex,
            CalibrationWizard.ColourOrder.Count,
            CalibrationWizard.IsConfirmationStep(wizard.StepIndex),
            colourName,
            wizard.PhotoIndex,
            photoCount,
            proxiedPhotoUrl,
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
    bool IsConfirmationStep,
    string ColourName,
    int PhotoIndex,
    int PhotoCount,
    string? PhotoUrl,
    bool PreviewActive,
    bool TvConnected);
