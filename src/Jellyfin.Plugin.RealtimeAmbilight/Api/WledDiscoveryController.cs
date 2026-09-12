using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Diagnostics;
using Jellyfin.Plugin.RealtimeAmbilight.Hue;

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
    private readonly JellyfinWledOutputService _outputService;
    private readonly HueEntertainmentService _hueService;
    private readonly PluginActivityLog _activityLog;

    public WledDiscoveryController(
        WledDiscoveryService discoveryService,
        JellyfinWledOutputService outputService,
        HueEntertainmentService hueService,
        PluginActivityLog activityLog)
    {
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _outputService = outputService ?? throw new ArgumentNullException(nameof(outputService));
        _hueService = hueService ?? throw new ArgumentNullException(nameof(hueService));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
    }

    /// <summary>The settings page's "Live activity" block -- see <see cref="PluginActivityLog"/> for what does and does not end up here.</summary>
    [HttpGet("Activity")]
    [ProducesResponseType(typeof(IReadOnlyList<PluginActivityEntry>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<PluginActivityEntry>> GetActivity() => Ok(_activityLog.Snapshot());

    /// <summary>
    /// A reachability/latency check for one of this plugin's own devices
    /// (the WLED controller, the Hue bridge, or the bound TV, if its IP is
    /// known), shown live in that device's overview card. Not an ICMP ping --
    /// this process has no raw-socket privilege for that -- but a bare TCP
    /// connect to the device's own service port and how long that took,
    /// which is the standard web-dashboard approximation and answers exactly
    /// the same question: is it there, and how far away does it feel.
    /// </summary>
    [HttpGet("Ping")]
    [ProducesResponseType(typeof(PingResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<PingResult>> PingAsync([FromQuery] string host, [FromQuery] int port, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return Ok(new PingResult(false, null));
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, Math.Clamp(port, 1, ushort.MaxValue), linked.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return Ok(new PingResult(true, stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return Ok(new PingResult(false, null));
        }
    }

    /// <summary>
    /// Live pipeline throughput (analysis decode / sampling / WLED send),
    /// each null while no session is active. Reads three already-maintained
    /// counters -- see <see cref="JellyfinWledOutputService.GetPerformance"/>
    /// -- so this endpoint itself does no measuring work of its own; safe to
    /// poll from the settings page at the same cadence as the existing live
    /// status check.
    /// </summary>
    [HttpGet("Performance")]
    [ProducesResponseType(typeof(PipelinePerformanceSnapshot), StatusCodes.Status200OK)]
    public ActionResult<PipelinePerformanceSnapshot> GetPerformance()
        => Ok(_outputService.GetPerformance() with { HueSendFps = _hueService.SendRateHz });

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
    /// Turns on WLED's "force max brightness" for realtime data, so its own
    /// brightness dial (and its nightlight timer) can never silently scale
    /// down what this plugin sends. The one WLED write this plugin ever
    /// offers, and only once the operator has opted in; the ABL power budget
    /// is never part of the request and this endpoint cannot change it. Also
    /// applied automatically once per plugin start -- see
    /// <see cref="JellyfinWledOutputService"/>'s own remarks -- this endpoint
    /// exists for an operator who just opted in and does not want to wait
    /// for a restart.
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
            .TryEnableForceMaxBrightnessAsync(host, Math.Clamp(port, 1, ushort.MaxValue), cancellationToken)
            .ConfigureAwait(false);
        return fixedIt
            ? NoContent()
            : StatusCode(StatusCodes.Status502BadGateway, "WLED did not accept the change.");
    }

    /// <summary>
    /// Sets WLED's RGBW mode to Manual -- the only mode this plugin's own
    /// RGBW32 output is correct under. Same opt-in gate as
    /// <see cref="FixForceMaxBrightnessAsync"/>.
    /// </summary>
    [HttpPost("FixRgbwMode")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> FixRgbwModeAsync([FromQuery] string host, [FromQuery] int port, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.AllowWledControl != true)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Turn on \"Allow this plugin to fix WLED settings\" first.");
        }

        var fixedIt = await _discoveryService
            .TryFixRgbwModeAsync(host, Math.Clamp(port, 1, ushort.MaxValue), cancellationToken)
            .ConfigureAwait(false);
        return fixedIt
            ? NoContent()
            : StatusCode(StatusCodes.Status502BadGateway, "WLED did not accept the change.");
    }

    /// <summary>
    /// Zeroes WLED's realtime pixel offset. Same opt-in gate as the other
    /// WLED fixes.
    /// </summary>
    [HttpPost("ResetRealtimePixelOffset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ResetRealtimePixelOffsetAsync([FromQuery] string host, [FromQuery] int port, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.AllowWledControl != true)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Turn on \"Allow this plugin to fix WLED settings\" first.");
        }

        var fixedIt = await _discoveryService
            .TryResetRealtimePixelOffsetAsync(host, Math.Clamp(port, 1, ushort.MaxValue), cancellationToken)
            .ConfigureAwait(false);
        return fixedIt
            ? NoContent()
            : StatusCode(StatusCodes.Status502BadGateway, "WLED did not accept the change.");
    }
}

/// <summary>Result of one <see cref="WledDiscoveryController.PingAsync"/> reachability check.</summary>
public sealed record PingResult(bool Reachable, double? Milliseconds);

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
    private readonly TestVideoLibrary _testVideoLibrary;

    public CalibrationController(JellyfinWledOutputService outputService, TestVideoLibrary testVideoLibrary)
    {
        _outputService = outputService ?? throw new ArgumentNullException(nameof(outputService));
        _testVideoLibrary = testVideoLibrary ?? throw new ArgumentNullException(nameof(testVideoLibrary));
    }

    /// <summary>
    /// Starts a calibration session: arms the anonymous TV-facing surface
    /// (<see cref="Pattern"/>, <see cref="GetWizardState"/>,
    /// <see cref="GetPhoto"/>, <see cref="PostPhotoFrameAsync"/>, all 404
    /// while unarmed) and resets the wizard to White. Deliberately explicit
    /// rather than "the link works whenever someone opens it": on an
    /// internet-facing Jellyfin, an unauthenticated page reachable at all
    /// times is its own exposure, however little it can do.
    /// </summary>
    [HttpPost("Start")]
    [ProducesResponseType(typeof(CalibrationWizardStateResponse), StatusCodes.Status200OK)]
    public ActionResult<CalibrationWizardStateResponse> Start()
    {
        _outputService.Wizard.Arm();
        return Ok(BuildWizardStateResponse());
    }

    /// <summary>
    /// Ends a calibration session: closes the TV-facing surface again (every
    /// anonymous action below starts answering 404 immediately) and releases
    /// WLED, exactly like <see cref="StopPreviewAsync"/>. Also what the
    /// wizard's own "Done" button calls on the last step.
    /// </summary>
    [HttpPost("Finish")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> FinishAsync(CancellationToken cancellationToken)
    {
        _outputService.Wizard.Disarm();
        await _outputService.StopCalibrationPreviewAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// A TV browser opens this once, without a Jellyfin login, and leaves it
    /// open for the whole wizard: it polls <see cref="GetWizardState"/> every
    /// 1.5 s and updates itself in place, so "Next" on the settings page never
    /// requires touching the TV again. Answers 404 unless a calibration
    /// session is armed via <see cref="Start"/> -- there is deliberately
    /// nothing to open here otherwise. Also reachable at the short
    /// <c>/amb</c> alias: this address is typed on a remote control one
    /// letter at a time, so its length matters.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("Pattern")]
    [HttpGet("/amb")]
    [Produces("text/html")]
    public ActionResult Pattern()
    {
        if (!_outputService.Wizard.IsArmed)
        {
            return NotFound();
        }

        const string html = """
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Ambilight calibration</title>
            <style>
              * { box-sizing:border-box } html,body { width:100%;height:100%;margin:0;background:#060914;overflow:hidden }
              img { position:fixed;inset:0;width:100%;height:100%;object-fit:cover;display:block }
              .label { position:fixed;left:50%;top:5vh;transform:translateX(-50%);z-index:4;color:#fff;font:600 clamp(14px,2vw,27px) system-ui,sans-serif;text-align:center;letter-spacing:.04em;text-shadow:0 2px 10px #000 }
              .hint { position:fixed;right:1.2em;bottom:1.2em;z-index:5;padding:.5em 1em;border-radius:1.4em;background:rgba(0,0,0,.55);color:#fff;font:600 clamp(11px,1.2vw,15px) system-ui,sans-serif;text-shadow:0 1px 4px #000 }
              .done { position:fixed;inset:0;display:flex;align-items:center;justify-content:center;flex-direction:column;gap:.6em;background:#0b0f1a;color:#fff;font:600 clamp(16px,3vw,32px) system-ui,sans-serif;text-align:center;padding:2em }
              .done small { font-size:.5em;font-weight:500;opacity:.7 }
              [hidden] { display:none !important }
            </style></head><body>
            <img id="photo" alt="" hidden />
            <div class="label" id="label">Loading&hellip;</div>
            <div class="hint" id="hint">Continue on your phone &rarr;</div>
            <div class="done" id="done" hidden>Calibration finished<small>You can close this page now.</small></div>
            <canvas id="canvas" width="320" height="180" hidden></canvas>
            <script>
              const photo = document.getElementById("photo");
              const label = document.getElementById("label");
              const hint = document.getElementById("hint");
              const done = document.getElementById("done");
              const canvas = document.getElementById("canvas");
              let lastKey = "";
              let everConnected = false;
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
                const stepCount = pick(state, "StepCount");
                label.textContent = `Step ${stepIndex + 1} of ${stepCount}, ${pick(state, "ColourName")}`;
              }
              async function tick() {
                try {
                  const response = await fetch("/RealtimeAmbilight/Calibration/WizardState?tv=true", { cache: "no-store" });
                  if (response.status === 404) {
                    if (everConnected) {
                      label.hidden = true;
                      hint.hidden = true;
                      photo.hidden = true;
                      done.hidden = false;
                    }
                    return;
                  }
                  everConnected = true;
                  const state = await response.json();
                  const key = String(pick(state, "StepIndex"));
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

    /// <summary>1080p16:9, matching the operator's own curated photos.</summary>
    private const int SwatchWidth = 1920;
    private const int SwatchHeight = 1080;

    /// <summary>Rendered swatches are as cheap to keep hot as the real photos are, and there are only six of them.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> SwatchCache = new();

    /// <summary>
    /// Serves a rendered flat swatch at the given step's own canonical hue
    /// (see <see cref="CalibrationWizard.SwatchColours"/>), same-origin,
    /// sampled by the TV page's own canvas exactly like a photo would be, so
    /// the calibration measures precisely what the real edge-sampling
    /// pipeline sees. 404 while no session is armed, like the rest of this
    /// anonymous surface, and for any name that is not one of the wizard's
    /// own colours.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("Photo/{colour}")]
    public IActionResult GetPhoto(string colour)
    {
        if (!_outputService.Wizard.IsArmed)
        {
            return NotFound();
        }

        if (!CalibrationWizard.SwatchColours.TryGetValue(colour, out var swatch))
        {
            return NotFound();
        }

        var png = SwatchCache.GetOrAdd(
            colour,
            _ => Core.Color.SolidColourImage.CreateFlatPng(swatch.Red, swatch.Green, swatch.Blue, SwatchWidth, SwatchHeight));
        return File(png, "image/png");
    }

    /// <summary>
    /// Read by the TV pattern page every 1.5 s, and by the settings page once
    /// on load. 404 while unarmed, like the rest of this anonymous surface.
    /// Only the TV's own poll (<paramref name="tv"/>) can restart the wizard:
    /// a poll gap over 20 s means the link was just (re)opened, so
    /// <see cref="CalibrationWizardState.NoteTvPoll"/> resets to step one.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("WizardState")]
    [ProducesResponseType(typeof(CalibrationWizardStateResponse), StatusCodes.Status200OK)]
    public ActionResult<CalibrationWizardStateResponse> GetWizardState([FromQuery] bool tv)
    {
        if (!_outputService.Wizard.IsArmed)
        {
            return NotFound();
        }

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
        _outputService.Wizard.MoveTo(request.StepIndex);
        return Ok(BuildWizardStateResponse());
    }

    /// <summary>
    /// Uploaded by the TV page after it draws the current photo to a canvas:
    /// full-range sRGB RGBA, downsized client-side. Anonymous like the rest of
    /// this page's own endpoints, 404 while unarmed, and bounded the same way
    /// every calibration write is -- playback always wins, checked inside the
    /// service.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("PhotoFrame")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PostPhotoFrameAsync([FromQuery] int width, [FromQuery] int height, CancellationToken cancellationToken)
    {
        if (!_outputService.Wizard.IsArmed)
        {
            return NotFound();
        }

        if (width < 16 || height < 16 || (long)width * height > 2_000_000)
        {
            return BadRequest("Frame dimensions are out of range.");
        }

        var expectedLength = checked(width * height * 4);
        if (Request.ContentLength is { } declaredLength && declaredLength != expectedLength)
        {
            return BadRequest($"Expected {expectedLength} RGBA bytes for {width}x{height}.");
        }

        // Content-Length is optional on an HTTP request -- chunked transfer
        // omits it entirely -- so the check above alone does not bound how
        // many bytes get read here. This endpoint is [AllowAnonymous] and
        // reachable by anything on the network once a calibration is armed,
        // not only the TV that is supposed to be the caller, so the read
        // itself is capped explicitly rather than trusting a header the
        // sender was never required to send: CopyToAsync alone would have
        // buffered as much as the caller cared to transmit (bounded only by
        // whatever the hosting server's own default request-body limit
        // happens to be, comfortably larger than the ~8 MB this endpoint
        // ever legitimately needs).
        using var buffer = new MemoryStream(expectedLength);
        var readChunk = new byte[Math.Min(expectedLength, 81920)];
        int bytesRead;
        while ((bytesRead = await Request.Body.ReadAsync(readChunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + bytesRead > expectedLength)
            {
                return BadRequest($"Expected at most {expectedLength} RGBA bytes for {width}x{height}.");
            }

            buffer.Write(readChunk, 0, bytesRead);
        }

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

        return new CalibrationWizardStateResponse(
            wizard.StepIndex,
            CalibrationWizard.TuningOrder.Count,
            wizard.IsLastStep,
            colourName,
            $"/RealtimeAmbilight/Calibration/Photo/{colourName}",
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

    /// <summary>Whether <see cref="TestVideoLibrary.LibraryName"/> is currently registered, so the settings page knows whether to offer "set up" or the clip list.</summary>
    [HttpGet("TestVideos/Status")]
    [ProducesResponseType(typeof(TestVideoLibraryStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<TestVideoLibraryStatusResponse> GetTestVideoLibraryStatus()
        => Ok(new TestVideoLibraryStatusResponse(_testVideoLibrary.IsSetUp));

    /// <summary>Extracts the embedded test clips and registers them as a real Jellyfin library, so the operator never has to do that by hand.</summary>
    [HttpPost("TestVideos/Setup")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetupTestVideosAsync(CancellationToken cancellationToken)
    {
        await _testVideoLibrary.SetupAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Removes the test-video library and deletes the extracted files, leaving no trace behind.</summary>
    [HttpPost("TestVideos/Remove")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveTestVideosAsync()
    {
        await _testVideoLibrary.RemoveAsync().ConfigureAwait(false);
        return NoContent();
    }
}

/// <summary>Moves the wizard, or retunes it, carrying the sliders' current values either way.</summary>
public sealed class CalibrationWizardMoveRequest
{
    public int StepIndex { get; set; }

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
            Value("LeftBrightnessPercent"), Value("LeftRedGainPercent"), Value("LeftGreenGainPercent"), Value("LeftBlueGainPercent"),
            Value("RedHueShiftDegrees", 0), Value("RedBrightnessPercent"), Value("RedIntensityPercent"),
            Value("GreenHueShiftDegrees", 0), Value("GreenBrightnessPercent"), Value("GreenIntensityPercent"),
            Value("BlueHueShiftDegrees", 0), Value("BlueBrightnessPercent"), Value("BlueIntensityPercent"),
            Value("YellowHueShiftDegrees", 0), Value("YellowBrightnessPercent"), Value("YellowIntensityPercent"),
            Value("CyanHueShiftDegrees", 0), Value("CyanBrightnessPercent"), Value("CyanIntensityPercent"),
            Value("MagentaHueShiftDegrees", 0), Value("MagentaBrightnessPercent"), Value("MagentaIntensityPercent"));
    }
}

public sealed record CalibrationPreviewResponse(bool Active, string Message);

public sealed record TestVideoLibraryStatusResponse(bool IsSetUp);

/// <summary>The wizard state the TV pattern page polls for and the settings page reads back after moving it.</summary>
public sealed record CalibrationWizardStateResponse(
    int StepIndex,
    int StepCount,
    bool IsLastStep,
    string ColourName,
    string PhotoUrl,
    bool PreviewActive,
    bool TvConnected);
