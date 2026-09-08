# Current state

## Last known commit and worktree

`main` is at `87abe1a` and pushed to `origin/main` (as of 2026-09-08); the
worktree carries further **uncommitted** work described below (tabbed
settings page, black level floor, temporal dithering, wall colour presets,
guarded WLED control). The earlier note that `.git` was read-only no longer
holds -- it is writable, and commits from `16b02fa` onwards carry the whole
implementation, the CI workflow, the packaging script and this handover.

This dev environment *is* the live host (`10.0.0.31`), not a separate box --
no SSH/remote hop is needed, only `sudo`. A prior session left the repo
working tree (including `.git`) owned by `root`; if `git`/`dotnet` commands
fail with permission errors, `sudo chown -R <user> /opt/dev/jellyfin-ambilight-realtime`
first, and use fresh `DOTNET_CLI_HOME`/`NUGET_PACKAGES` tmp dirs rather than
reusing ones a different user created.

Pushing needs the `workflow` OAuth scope because the repository contains
`.github/workflows/ci.yml`; the operator granted it with
`gh auth refresh --hostname github.com -s workflow`, which needs a real
terminal. Pushes use `git -c credential.helper='!gh auth git-credential' push`.
The registered SSH key is not associated with the GitHub account, so HTTPS via
`gh` is the only working path today.

## Handoff discipline

This file is the persistent session record. Update it after every meaningful
implementation unit, build/test result, architecture decision, environment
change, blocker, or change to the next-action order. Before ending a session,
record the exact validation commands/results and all uncommitted work so another
engineer can continue without relying on chat history.

## Build and test status

**PASS (2026-09-08, colour-tuning wizard).**

```bash
DOTNET_CLI_HOME=/tmp/jfar2-dotnet-cli NUGET_PACKAGES=/tmp/jfar2-nuget-packages \
dotnet build src/Jellyfin.Plugin.RealtimeAmbilight/Jellyfin.Plugin.RealtimeAmbilight.csproj \
    --configuration Release --no-incremental -p:UseSharedCompilation=false
DOTNET_ROLL_FORWARD=Major dotnet test \
tests/Jellyfin.Plugin.RealtimeAmbilight.Tests/Jellyfin.Plugin.RealtimeAmbilight.Tests.csproj \
    --configuration Release -p:UseSharedCompilation=false
```

Zero warnings/errors; **80/80** (no new tests: the wizard's own pure logic --
`CalibrationWizard.PhotoAt`'s wraparound, `CalibrationWizardState.MoveTo`'s
clamping -- lives in the Plugin project's root namespace like
`CalibrationReferenceColour` before it, which the test project has never
referenced; it only references Core, deliberately, so it builds without
Jellyfin.Controller/Model. Adding a test would mean adding that reference for
one file, an inconsistency not worth introducing this late in a session).

Replaces the old single side+colour dropdown pair with a seven-step wizard
(`CalibrationWizard.ColourOrder`: White, Blue, Red, Green, Yellow, Purple,
Orange) driven from the **Ambilight** tab. Mechanism:

- `CalibrationWizardState` (a field on `JellyfinWledOutputService`, alongside
  the existing `_calibrationPreview`) holds `{StepIndex, PhotoIndex, Side}`
  in memory only -- a restart returns to step 0, which is harmless.
- `GET RealtimeAmbilight/Calibration/WizardState` is `[AllowAnonymous]`
  (same precedent as `Pattern`) so the already-open TV page can poll it every
  1.5 s without a login, and updates its DOM in place -- no navigation, no
  query parameters -- when the step/photo/side key changes.
- `POST RealtimeAmbilight/Calibration/WizardState` (admin) moves the wizard
  and, only when `IsCalibrationPreviewActive`, restarts the LED preview with
  the new step's colour, mirroring the old side/colour-dropdown-change
  behaviour instead of replacing it.
- The centred photograph and its Wallhaven credit are purely decorative:
  Ambilight only ever samples the solid edge glow (unchanged mechanism), and
  the photo's CSS inset matches the old decorative `.art` div's, so it can
  never reach the sampled edge band.

**Wallhaven sourcing.** 17 photos across the six non-white colours, picked by
hand this session using Wallhaven's public API `colors` filter (a fixed
32-swatch palette -- arbitrary hex values return zero results, see
`WledDiscoveryService`-adjacent research if this needs redoing) combined with
`q=nature`/`q=sunflower`/`q=lavender` etc., `categories=100` (General only,
deviating from the operator's own example link's `101` to exclude the People
category), `purity=100` (SFW only, deviating from the example link's `110`
"sketchy" for a shared living-room TV -- flagged to the operator, not
silently decided). Each candidate's thumbnail was downloaded and visually
checked before inclusion; two initially-strong sunflower results (`48ey9k`,
`lqx9dq`) were dropped because their Wallhaven uploader accounts are
`"deleted"` -- there is no one to credit, and crediting is the whole point of
including them. Hotlinked at `thumbs.large` size (not the full original,
which can exceed 10 MB) directly from `th.wallhaven.cc`; confirmed reachable
with no hotlink/referer protection from both no-referer and a foreign
(Jellyfin-origin) referer.

**Caught before deployment, not after: a JSON-casing bug in the embedded TV
page's own polling script.** It was first written reading `state.stepIndex`
etc. (camelCase), copying the assumption from `config.js`'s defensive
`?? camelCase` fallbacks elsewhere in this file. But `curl
localhost:8096/System/Info/Public` -- Jellyfin's own core API, sharing this
plugin's ASP.NET pipeline -- returns **PascalCase**
(`"LocalAddress"`, `"ServerName"`, ...), confirming PascalCase is what this
host actually serializes, not camelCase. Fixed with a `pick(state, "Name")`
helper trying PascalCase first, camelCase second, inside the embedded script
too. `config.js`'s own wizard code already did this correctly by following
existing convention; only the newly hand-written TV-page script had the bug.
**This is the same class of mistake as the `hostName`/`host` fix earlier
today** -- verify the actual wire format instead of assuming it, especially
in hand-written JSON consumers that are not the shared `config.js` module.

**Not yet exercised end-to-end in a browser** (see the standing item below):
built, unit-tested where the architecture allows it, and the Wallhaven URLs
were curl-verified reachable, but nobody has clicked Next on the actual
settings page while watching the actual TV page update.

**PASS (2026-09-08, settings-page redesign and calibration follow-ups).**

```bash
DOTNET_CLI_HOME=/tmp/jfar2-dotnet-cli \
NUGET_PACKAGES=/tmp/jfar2-nuget-packages \
dotnet build src/Jellyfin.Plugin.RealtimeAmbilight/Jellyfin.Plugin.RealtimeAmbilight.csproj \
    --configuration Release --no-incremental -p:UseSharedCompilation=false

DOTNET_ROLL_FORWARD=Major dotnet test \
tests/Jellyfin.Plugin.RealtimeAmbilight.Tests/Jellyfin.Plugin.RealtimeAmbilight.Tests.csproj \
    --configuration Release --no-restore -p:UseSharedCompilation=false
```

Zero warnings/errors; test suite **80/80**. Deployed to the live host and
verified: plugin loads, config page returns 200, `maxpwr` unchanged. This
round was driven by direct operator feedback on the room-calibration UI from
the previous session:

- **Settings page reorganised into four tabs** (TV / WLED / Ambilight /
  Advanced), hand-rolled (no dependency on Jellyfin's own tab widget, which
  is not guaranteed present in a bare plugin config page), with the active
  tab remembered in `localStorage`. The old numbered-section scheme and the
  `arrangeSettingsSections()` DOM-reordering hack are gone; each element now
  lives directly under its tab in source order.
- **Black level floor** (`BlackLevelFloorPercent`, 0-20%, default 0):
  `PerimeterColourAdjustment.Apply` gates on the sampled pixel's own
  luminance *before* any brightness/saturation/gain, rescaling the headroom
  above the floor back to full range so there is no jump at the boundary and
  hue/saturation survive the fade to black. Applied globally, not per side.
- **Temporal dithering** (`DitheredRgb24Encoder`, wired into
  `AmbilightFrameProcessor`, replacing the static `Rgb24Encoder.Encode` call):
  carries each channel's rounding error into the next output frame. This is
  the fix for the reported "hobbyist-looking steps" on light-to-dark
  transitions -- not a WLED setting, root cause was linear-light 8-bit giving
  the darkest tones (where the eye is most sensitive) the fewest of the 256
  available steps, so a slow fade held one byte value for several frames and
  then jumped. See `Rgb24Encoder.ToTransferValue` (extracted, shared by both
  encoders) and the dithering tests for the exact behaviour.
- **Wall colour presets**: a curated list of common paint colours (greys,
  greige, anthracite, sage/hunter green, navy, terracotta, ...) in a
  dropdown, with "Custom…" at the end revealing the exact-colour picker only
  then. Values are approximate sRGB guesses, not tied to a specific paint
  brand.
- **Guarded WLED control**: `AllowWledControl` (default **off**) gates a new
  `POST RealtimeAmbilight/Discovery/FixForceMaxBrightness`
  (`WledDiscoveryService.TryDisableForceMaxBrightnessAsync`) that writes
  *only* `{"if":{"live":{"maxbri":false}}}` to WLED's `/json/cfg` -- nothing
  else is ever in that payload, so it structurally cannot touch the ABL power
  budget (`led.maxpwr`). The uses a fixed-length `StringContent`, not
  `PostAsJsonAsync`, for the same chunked-encoding reason the old realtime
  stop call once failed (see the HDR/colour history below). `maxpwr` is now
  also parsed and displayed read-only on the WLED tab
  (`WledRealtimeSettings.MaxPowerMilliamps`). This was verified live: the
  configured WLED already had `if.live.maxbri: false` and `led.maxpwr: 40000`
  before and after, and the write path itself was **not exercised against the
  live controller this session** -- there was nothing to fix, and flipping a
  live safety setting just to test it was avoided. Exercise it for real the
  next time a WLED reset or reconfiguration turns "force max brightness" back
  on.
- **Bug fix, found while wiring the above**: `currentWledConnection()` in
  `config.js` returned `{ hostName, port }`, but
  `WledDiscoveryController`'s `[FromQuery] string host` binds on `host`. Every
  call built from it (`Discovery/Settings`, `Discovery/Status`, and now
  `Discovery/FixForceMaxBrightness`) was therefore silently sending no host at
  all. This shipped in the previous session's calibration commit and was
  never caught because that work was validated at the HTTP/backend layer, not
  by exercising the actual settings-page JavaScript in a browser. Fixed by
  renaming the returned key to `host`. **This is exactly the risk the
  "verify a deployment by grepping the built assembly" rule doesn't cover: a
  frontend/backend contract mismatch needs the page actually clicked through
  in a browser, which has still not been done this session.**

**PASS (2026-09-06, room-calibration worktree changes).**

```bash
DOTNET_CLI_HOME=/tmp/jfar-dotnet-cli \
NUGET_PACKAGES=/tmp/jfar-nuget-packages \
dotnet build src/Jellyfin.Plugin.RealtimeAmbilight/Jellyfin.Plugin.RealtimeAmbilight.csproj \
    --configuration Release --no-restore --no-incremental -p:UseSharedCompilation=false

DOTNET_CLI_HOME=/tmp/jfar-dotnet-cli \
NUGET_PACKAGES=/tmp/jfar-nuget-packages \
DOTNET_ROLL_FORWARD=Major \
dotnet test tests/Jellyfin.Plugin.RealtimeAmbilight.Tests/Jellyfin.Plugin.RealtimeAmbilight.Tests.csproj \
    --configuration Release --no-restore -p:UseSharedCompilation=false
```

The plugin build completed with zero warnings/errors and the test suite passed
**75/75**. The worktree now contains an uncommitted room-calibration feature:
global wall-colour compensation, persistent brightness/RGB trims for top/right/
bottom/left, and an administrator-only preview endpoint plus anonymous static
TV test-pattern endpoint. The test pattern contains no credentials or server
data. The preview uses the normal temporary WLED realtime transport, never
writes WLED configuration, releases on stop, and yields immediately when
playback starts.

The settings page was subsequently reorganized for installation use: TV,
controller, timing, **Color calibration**, LED layout, then collapsed advanced
picture settings. Color calibration now contains global brightness/saturation/
white balance, wall compensation, the TV test-pattern preview and per-side
trims. The layout editor includes a live 16:9 diagram with the four LED counts
and the current sampled edge band. Every slider shows min/current/max values.
`GET /RealtimeAmbilight/Discovery/Status` reads WLED's public `/json/info` and
the page displays online, realtime-active or unreachable; it remains read-only.

**PASS (2026-09-06).**

```bash
DOTNET_CLI_HOME=/tmp/jfar-dotnet-cli \
NUGET_PACKAGES=/tmp/jfar-nuget-packages \
dotnet build Jellyfin.RealtimeAmbilight.sln --configuration Release --no-restore

DOTNET_CLI_HOME=/tmp/jfar-dotnet-cli \
NUGET_PACKAGES=/tmp/jfar-nuget-packages \
DOTNET_ROLL_FORWARD=Major \
dotnet test Jellyfin.RealtimeAmbilight.sln --configuration Release --no-restore
```

The Release build completed with zero warnings/errors. The test suite passed
**71/71**. It covers DDP/Raw-RGB packet semantics, layouts/interpolation,
playback coordination, latest-frame handoff, SDR/HDR FFmpeg argument building,
sampling, WLED protocol selection, keepalive, fade, release-by-ceasing-
transmission, mDNS discovery parsing (against a captured real WLED packet),
black-bar detection, media-position stamping, the two wire encodings and the
colour adjustments. The target is `net9.0`; this host uses .NET 10, so tests
require `DOTNET_ROLL_FORWARD=Major`.

**`dotnet test` does not build the plugin project.** The test project references
Core only, so a green test run says nothing about the plugin assembly, and the
settings page lives in that assembly as an embedded resource. Deploy with:

```bash
dotnet build src/Jellyfin.Plugin.RealtimeAmbilight/Jellyfin.Plugin.RealtimeAmbilight.csproj \
    --configuration Release --no-incremental
```

`--no-incremental` is not optional: incremental builds silently reused a stale
`config.html` twice in one session. Verify a deployment by grepping the built
assembly for a string you just added before copying it.

The plugin references Jellyfin.Controller/Model **10.11.9**, deliberately
matching the live Jellyfin host rather than the previously assumed 10.11.11.

## Live deployment and safety invariant

The live development host is Jellyfin **10.11.9** at `10.0.0.31`. Realtime
Ambilight **0.1.1** is installed and active in:

```text
/var/lib/jellyfin/plugins/Realtime Ambilight_0.1.1
```

Both `Jellyfin.Plugin.RealtimeAmbilight.dll` and
`Jellyfin.Plugin.RealtimeAmbilight.Core.dll` must be copied into that directory
on every deployment, followed by `systemctl restart jellyfin`. Installing only
the plugin DLL caused a `MissingMethodException` because the old Core DLL
remained loaded.

The previous `Ambilight_2.5.0` plugin and its XML configuration were moved
(not destroyed) to `/tmp/jellyfin-ambilight-backup-20260906` to avoid competing
output.

WLED is `10.0.0.8`, firmware 16.0.1, 831 LEDs, an SK6812 **RGBW** strip
(`type 30`) in RGB order with `rgbwm 0`, meaning manual white only. Its observed
state after the latest deployment was `live=false` and `maxpwr=40000`.
**Never write WLED's persistent configuration or alter `maxpwr`; it must remain
40 A.** Runtime realtime frames are all this plugin sends.

One exception, on explicit operator instruction on 2026-09-06:
`if.live.maxbri` was changed from `true` to `false`. With it enabled WLED
ignored its own brightness slider during realtime and drove the strip at full,
measured at 5.5x the current the same colour drew as a static colour at the
operator's brightness of 80. A full configuration backup was taken first, the
diff was verified to contain that single field, and `maxpwr` was confirmed
unchanged at 40000 afterwards. The plugin itself still writes nothing.

The operator's Jellyfin API credential is not persisted in this repository or
this handover.

## Implemented and deployed

- GPL-3.0 license, solution/project structure, ADR/research set, core unit
  tests and a `net9.0` Jellyfin plugin compatible with the live 10.11.9 host.
- Immutable physical and logical clockwise perimeter layouts. The reference
  layout is 265 top / 150 right / 266 bottom / 150 left (= 831 LEDs), with
  endpoint-preserving per-side interpolation in linear-light RGB.
- A playback-event coordinator with single active-session ownership, monotonic
  playback correction, pause/seek cancellation, scrub debounce and a one-slot
  latest-frame buffer.
- Jellyfin session callbacks adapted to the coordinator, plus a local-media
  source resolver. Host callbacks enqueue quickly; source lookup and decoding
  occur in the worker path.
- FFmpeg child-process decoder using safe `ArgumentList` construction,
  input-side seek, fixed-size packed BGRA frames, bounded stderr drain and
  process-tree cancellation. SDR works live; command builders exist for HDR10,
  HLG and Dolby Vision but source-profile detection is still incomplete.
- Frame processing: active-picture edge sampling, BT.709 limited-range BGRA
  conversion, linear-light reduction, physical interpolation and RGB24 output.
- A hosted latest-only output pump. It samples/sends at the configured output
  rate, catches/logs output errors, sends active keepalives while paused, fades
  and releases on a playback stop, and uses a bounded delay queue when delay is
  configured.
- WLED realtime output via Auto / Hyperion Raw RGB / DDP. Auto selects DDP for
  the 831-LED reference installation; no persistent WLED JSON write occurs.
- Runtime logging sufficient for live diagnosis: playback start, resolved
  source, first decoded frame, first processed frame, first WLED frame and
  output-pump/fade/release errors.
- A native Jellyfin settings page through `IHasWebPages`, verified from
  `/web/configurationpages` and `/web/configurationpage`. It exposes the
  enable switch, WLED host/port/protocol, all physical side counts, output
  delay/FPS, pause keepalive, stop fade, analysis size and analysis FPS.
- **Rewritten settings page for a human reader (2026-09-06).** Five numbered
  sections, an explanation under every field and the sane default named in the
  text. The analysis height is no longer an input: 16:9 is assumed and the
  height is derived from the width and shown live (160 -> 160 x 90).
- **Device binding.** `TargetDeviceId` binds one Jellyfin playback device to
  this WLED installation; an empty value keeps the legacy "any session"
  behaviour and is offered explicitly as "Alle apparaten - niet gekoppeld".
  The page lists `GET /Devices` sorted by `DateLastActivity`, newest first.
  It must not use `/Sessions`: a switched-off TV has no session at all. On the
  live host `/Sessions` returned 1 entry (the configuring browser) while
  `/Devices` returned 31 including both TCL 85C8L registrations, so the TV
  could not have been bound from a session list.
- **WLED finder over mDNS** (`GET /RealtimeAmbilight/Discovery/Wled`,
  admin-only). Manual host/port entry remains and is revealed automatically
  whenever the scan returns nothing. See ADR-009 Amendment 3.

## Live validation completed

- Jellyfin loaded the plugin successfully (`Status=Active`).
- A full local SDR/HD playback path has been observed: Jellyfin event → source
  resolver → independent FFmpeg decode → sampling → WLED realtime output.
- The settings page and its JavaScript resource are registered and loadable in
  Jellyfin. The user visually confirmed that the page now appears to work.
- **HDR playback reached the LEDs on 2026-09-06**: `processed its first decoded
  frame` and `sent its first WLED frame` were logged for a 2160p HDR10 HEVC
  remux and the strip lit. That is the first end-to-end HDR success.
- Do not assume a universal delay is correct: tune it against the actual TV.
  The saved live values are delay **0 ms**, stop fade 2500 ms, analysis 30 fps,
  sampling edge range 10%, brightness and saturation 100%.
- **Discovery verified end to end on the live host (2026-09-06).** With the
  configured host deliberately set to an unreachable `192.168.254.254`, the
  endpoint still returned the real controller in ~1.6 s, proving the result came
  from mDNS rather than the configured-host fallback. The log line
  `WLED discovery probed 2 candidate(s) and confirmed 1` records that the
  unreachable candidate was correctly discarded.
- **The stop/release defect is fixed** (see below and ADR-004 Amendment 4).

## Important operational notes

- Structural changes -- WLED endpoint/protocol, physical LED counts, output FPS,
  sampling resolution, sampling edge range and the gamma detection -- are read
  when the hosted output service starts. Save them in the page, then restart
  Jellyfin before judging them. Delay, enable/disable, hold-while-paused, fade,
  brightness, saturation, white balance and black-bar handling are read per
  frame and take effect immediately.
- Use the installed plugin's log markers to distinguish event/source/decoder
  failures from a WLED delivery failure:

  ```text
  Realtime Ambilight playback start received
  Realtime Ambilight resolved source
  Realtime Ambilight processed its first decoded frame
  Realtime Ambilight sent its first WLED frame
  ```

- Three further log lines exist precisely because their failures used to be
  silent, and each names its own remedy:

  ```text
  ignored playback on device ... because it is bound to ...
  analysis failed; the LEDs will stay dark until the next playback event
  is N ms behind the picture; the analysis decoder is not keeping its lead
  ```

- Prefer `systemctl restart jellyfin` for deployment. The Jellyfin HTTP restart
  endpoint was less reliable during development.
- The current configuration is additive (`ConfigSchemaVersion` defaults to 2)
  and the saved live configuration may still report schema 1 until the settings
  page is saved once.

## Why the LEDs stayed dark (2026-09-06, fixed)

Three independent defects, found after the operator reported that playback
produced no Ambilight at all. Each is worth knowing about on its own.

**1. Failures were invisible.** `PlaybackEventCoordinator.RunWorkerAsync` ended
in a bare `catch { }` whose comment deferred reporting to "the future host
integration". FFmpeg's stderr is carried on that exception, so every analysis
failure was discarded together with the only evidence of its cause. The
coordinator now raises `AnalysisFailed`, which the adapter logs; the coordinator
still takes no logging dependency. Two silent rejections in the playback-start
handler were logged for the same reason.

**2. The HDR path could never have produced a frame.** The reference film is
HDR10 (`smpte2084`, bt2020, 10-bit), so it takes the HDR graph, which was
written but never executed against real media. libplacebo is a Vulkan filter and
no Vulkan filter device was ever created, so `hwupload` targeted the VAAPI device
and FFmpeg could not negotiate the chain: *"Impossible to convert between the
formats supported by the filter 'Parsed_libplacebo' and the filter
'auto_scale'"*. Adding `-init_hw_device vulkan=vk -filter_hw_device vk` fixes it;
the graph is otherwise unchanged, so ADR-006's measured Dolby Vision decision
stands. Deriving the Vulkan device from VAAPI (`vulkan=vk@va` with `hwmap`) is
**not** viable here: it fails with `VK_ERROR_OUT_OF_DEVICE_MEMORY` at every frame
size down to 320x180, because Mesa cannot import these multi-planar formats with
DRM modifiers. Routing through system memory avoids the import. Verified: first
frame in ~1.1 s as the `jellyfin` user.

**3. The device binding could never match.** The settings page offered devices
from `/Devices`, but playback events carry the *session's* device id, and those
sets do not coincide: the live TV session reported
`96d21932a3badbe4fb4231645ec7e57a098ecdbe`, an id absent from `/Devices`, which
listed two other ids for the same television. Binding by id alone therefore
produced a filter that silently discarded every event. The page now merges
`/Devices` with `/Sessions`, and `TargetDeviceName` was added as a fallback
match, because one physical television was observed under three different ids.

## The binding decays unless it repairs itself (2026-09-06)

The HDR fix above worked on first contact: `processed its first decoded frame`
and `sent its first WLED frame` were logged at 20:04:53 and the LEDs lit. The
next item produced nothing, because at 20:08:32 a settings-page save wrote an
empty `<TargetDeviceName />` over the good value -- almost certainly a page that
had been open since before the binding was set, saving its stale copy. With the
name gone and the id already stale, the filter rejected every event again.

Two changes make that class of failure self-correcting rather than terminal:

- The settings page carries each device's plain name on its `<option>`, so a
  save can never write an empty name for a bound device, whatever the page's
  copy of the configuration says.
- On a matched playback start, the adapter writes back the session's actual id
  and name when either has drifted, logging `refreshed its device binding`. Once
  anything matches, both fields are known good, so the binding converges instead
  of decaying. Note the limit: if *both* fields are wrong, nothing can match and
  no repair happens -- rebind from the settings page.

## Output was an open loop until 2026-09-06

The operator reported the LEDs running 2 s early at the start of a film, and
late again a minute later. The cause was structural, not a tuning problem.

Analysis frames carried no media timestamp, and output scheduled them as
"arrival time plus a fixed delay". The playback clock *was* corrected from the
client's authoritative progress reports, but nothing connected that correction
to what was sent, so the LEDs followed the decoder rather than the picture. Two
errors then accumulated freely: FFmpeg was seeked to exactly the reported
position, leaving it behind by its own start-up cost (about 1.1 s for the HDR
graph) and re-applying that lag on every restart, and any difference between
decode pace and playback pace drifted without bound.

Now closed:

- `AnalysisFrame` carries the media position it depicts. FFmpeg emits a
  constant-rate stream from the seek point, so the position follows from the
  frame index and costs nothing to compute.
- The decoder starts `FfmpegAnalysisWorker.DecoderLead` (2 s) *ahead* of the
  reported position, so it has slack to be held rather than lag to be endured.
- Output computes each frame's due time from the playback clock, so a clock
  correction immediately reshapes what is pending. `OutputDelayMilliseconds` now
  means only what its label claims: compensation for the television's own
  pipeline. The live value of 2000 ms was compensating this defect and has been
  reset to 0; it must be re-tuned from there.
- `DriftTolerance` drops from 1 s to 250 ms. It is now a direct upper bound on
  visible synchronisation error, and a correction only reshuffles pending frames
  instead of restarting the decoder.
- Falling behind is reported: `is N ms behind the picture`, rate-limited to once
  per 30 s.

Measured while diagnosing: the HDR analysis graph sustains about 101 fps on the
reference host, so throughput was never the limit at 60 analysis fps.

## The HDR graph never pinned its frame rate (2026-09-06)

Reported as a colour fault: brown ground on screen, green LEDs. It was not a
colour fault. The frame at 2:00 is pink sky over brown ground with no green in
it at all, and running that frame through the real sampling pipeline produced
correct warm values (top 241/133/107, bottom 75/22/2). But **t=90 s is a green
scene** -- the LEDs were showing the right colours from the wrong moment.

The SDR graph pins its rate with `fps={FramesPerSecond}`; the HDR graph never
did, so it emitted at the source rate of 23.976 fps while output counted frame
indices at the configured 60. Every stamped position therefore advanced 0.4 s
per real second, and the LEDs fell behind by 0.6 s for every second played. The
warning added earlier caught it exactly: 518 ms, then 9.5 s, 11.6 s, 17.6 s.
At the two-minute mark that is roughly the 30 s offset to the green scene.

Three changes:

- The HDR graph pins `fps=` as the SDR graph does. A test now asserts it,
  because this failure looks like a colour bug and costs an evening to find.
- Its intermediate scale drops from 960x540 to 320x180. Nine times fewer pixels
  cross hwdownload/hwupload for no loss, since analysis output is 160x90:
  measured 2.75x -> 6.28x real time at 60 fps on the reference host.
- Output can now call `PlaybackEventCoordinator.RequestAnalysisResync()`. A
  decoder that has lost its lead cannot regain it, because it runs at playback
  speed and not faster, so beyond 1 s late it is restarted at the current
  position, at most once every 20 s.

Live analysis fps was reduced from 60 to 30: the source is 23.976 fps, so 60
duplicated every frame and doubled the tone-mapping work for no information.

## Deployment trap: incremental builds miss embedded resources

Twice now a deployment shipped a stale settings page because `dotnet build`
reported success without re-embedding a changed `config.html`. Worse, `dotnet
test` does not build the plugin project at all -- the test project references
only Core -- so a green test run says nothing about the plugin assembly. Before
deploying, build the plugin project explicitly with `--no-incremental`, and
verify by searching the assembly for a string you just added.

## Sampling controls (2026-09-06)

- Bands are the outer tenth of **each axis**, not of the short edge, so the top
  and bottom no longer weigh differently from the left and right on the same
  scene. `SamplingDepthPercent` exposes it: 2-50, recommended 10.
- `IgnoreBlackBorders` (default on) detects letterbox and pillarbox bars.
  Detection is deliberately slow to change: a result must repeat across frames
  before it is adopted, and one that would leave too little picture is rejected
  so a fade to black keeps the last known geometry instead of cropping the
  picture out of existence.
- Analysis size is now a list of six 16:9 presets from 96x54 to 480x270 rather
  than a free number, with sampling rate beside it. A stored width outside the
  list is kept as a custom entry rather than silently rounded to a preset.
- The LED layout section collapses, since it is set once, and its summary keeps
  showing the four counts and the total while closed.

## The colour path was wrong end to end (2026-09-06, fixed)

Reported as two separate faults -- "it only looks at colour, not brightness"
during a dark scene, and washed-out colours minutes later. One cause.

`Rgb24Encoder` applied the BT.709 transfer function, the encoding a *display*
expects. WLED drives realtime data straight to the LEDs: this controller has
`if.live.no-gc true`, and exempting realtime is WLED's own default. Every value
was therefore driven far above its intended light output -- about thirteen times
for a dark scene, twice for a mid tone -- so dark scenes stayed lit, and colours
paled because the weakest channel of a colour is lifted the most and saturation
is exactly what that costs.

`Rgb24Encoding` is now explicit: `Linear` sends light-proportional bytes,
`Bt709` keeps the old behaviour for a controller that applies its own gamma.
`AutoDetectLedGamma` (default on) reads `/json/cfg` at startup and picks from
`light.gc.col` and `if.live.no-gc` rather than guessing, logging what it found
and falling back to `CorrectLedGamma` when the controller cannot be reached.
Restart Jellyfin after changing that setting in WLED.

**Ruled out with measurements, so nobody repeats the work.** The white channel
was not involved: `rgbwm` is manual-only, and estimated power across black,
greys and saturated primaries scaled exactly with the sum of the RGB values
(half grey to saturated red measured 1.51, the ratio pure RGB predicts).
libplacebo's dynamic peak detection was not involved either: `peak_detect=0`
changed frame brightness by at most three units out of 255.

## Colour and brightness controls (2026-09-06)

`ColourAdjustment` applies, in linear light, saturation about the colour's own
luminance, then per-channel gains, then brightness. Linear light matters: half
the brightness is half the light, which is not true of encoded values.

- `BrightnessPercent` (1-100, default 100). Needed because WLED's realtime path
  bypasses its master brightness when "force max brightness" is on.
- `SaturationPercent` (50-200, default 100). Above 100 deepens a colour without
  making it brighter; greys and whites are untouched at any setting, which a
  test pins because the obvious implementation tints them.
- `RedGainPercent`, `GreenGainPercent`, `BlueGainPercent` (50-150, default 100).

**A gain cannot add a colour that is absent.** On a pure night sky (R=0), red
100% and red 140% produce identical output; on the sunrise a minute later the
same setting moves red from 131 to 170. Warmth that survives a pure blue would
be a tint, which trades fidelity to the picture, and is deliberately not
implemented. The operator was shown both on the strip and has not yet chosen.

## Operator calibration aid

Candidate colours can be put on the top edge as labelled blocks so the operator
picks by eye, using the real pipeline to compute each block from a real setting.
The scratch script sends DDP directly at 20 Hz for a fixed duration.

**Run one at a time.** Starting a second while the first was still running made
the strip alternate between the two, which reads exactly like a plugin fault --
it was reported as "flickering red-blue" and cost a round trip to explain.

## Partially implemented / needs validation

- Final TV calibration: determine the useful output-delay range on the TCL/TV
  path. A UI slider exists (0-2000 ms, 25 ms increments) and shows its value as
  you drag. The live value is **0 ms**: the earlier 2000 ms was compensating the
  open-loop scheduling defect and was reset when that was fixed.
- **Brightness, saturation and white balance are unchosen.** Defaults are all
  100%, which is faithful to the picture but, on this installation, brighter
  than the operator wants. Candidate values were put on the strip; the operator
  has not yet reported which block they prefer.
- **Pause is now a switch, not a duration (2026-09-06).** `HoldWhilePaused`
  (default on) holds the paused frame on the LEDs until playback resumes or
  stops. The old `PauseKeepAliveSeconds` was a *resend interval* rather than a
  hold duration, and the live value of 10 s exceeded WLED's realtime timeout, so
  the LEDs would have dropped back to WLED's own effect and been yanked in again
  mid-pause. The resend cadence is now a fixed 1 s constant, chosen against
  measured release times of 2.46 s for DDP and 2.25 s for Raw RGB. The old
  property is retained unused so existing configurations keep deserializing.
- Pause semantics still need a final *visual* confirmation on the TV: colours
  should hold while paused, and resuming should pick the picture back up without
  WLED's own effect appearing in between. The gap bridge now covers that: it
  measured from the last real frame, so a pause longer than the bridge had
  already spent it, and nothing covered the decoder restart on resume.
  The underlying stop/release defect behind the earlier symptom is now fixed:
  the explicit `{"live": false}` control call was failing with `400 Bad Request`
  (`PostAsJsonAsync` frames the body as chunked, which WLED's ESPAsyncWebServer
  rejects) and the exception escaped `StopAsync`, which Jellyfin logged as
  `[FTL] Error while starting server` during shutdown. Measurement then showed
  the call was harmful even when framed correctly: ceasing transmission returns
  control in **2.26 s** median, while additionally posting `{"live": false}`
  re-arms the realtime lock and delays it to **5.06 s**. The call is removed,
  `IWledControlClient`/`WledJsonControlClient` are deleted, and `StopAsync` now
  swallows and logs any failure so it can never abort Jellyfin's shutdown.
- HDR/Dolby Vision command graphs are implemented, but automatic source profile
  detection, hardware-path selection and visual HDR/DV validation are not.
- WLED outage recovery/retry policy, richer admin diagnostics and automated
  live-controller integration tests are still absent.
- Colour calibration is now exposed (brightness, saturation, per-channel gains);
  a user-configurable logical sampling layout and temporal smoothing are not.
- **Not understood: the analysis decoder loses its lead during real playback.**
  Run standalone it holds a five-second lead within 30 ms over 100 seconds, and
  sustains exactly 30 fps with `-re` over a minute, so neither FFmpeg nor the
  graph is the limit. Under real playback it still falls behind by one to two
  seconds within a few seconds of a restart. The consequences are contained --
  the lead absorbs it, the bridge covers gaps, and a restart is a last resort
  with a 60 s cooldown -- but the cause is unknown and worth finding.

## Packaging and CI (added 2026-09-06)

- `build/package.sh` builds, stages both assemblies with `meta.json`, writes a
  deterministic zip and emits a Jellyfin plugin repository manifest. The
  manifest field names and the MD5 checksum were taken from Jellyfin's own
  `PackageInfo`/`VersionInfo` DTOs by reflecting over the installed
  `MediaBrowser.Model.dll`, not from a guessed schema.
- The produced package was extracted into the live plugin directory and loaded
  as `Active`, so the artifact is known to install and not merely to build.
- `.github/workflows/ci.yml` restores, builds, tests and packages on push and
  pull request, and uploads the zip plus manifest as an artifact. Because
  `Directory.Build.props` sets `TreatWarningsAsErrors`, a warning fails CI.
- **Reproducibility, precisely.** The archive is deterministic (fixed entry
  timestamps, sorted entries) and repeated builds from the same checkout give an
  identical checksum. Two different checkout directories still differ in 72
  bytes -- PE timestamp, MVID and PDB signature, all content-hash derived --
  and `PathMap` did not close that gap. Treat the CI artifact as canonical.

## Not implemented

- No published release or hosted repository manifest: `sourceUrl` points at a
  GitHub release tag that does not exist yet, and nothing is signed.
- Setup/discovery wizard beyond the settings page.
- A disposable multi-version Jellyfin integration fixture and automated TV/WLED
  end-to-end test harness.

## Known risks

- The deployment has been verified on Jellyfin 10.11.9 only. Keep the package
  references aligned with each target Jellyfin host.
- Manual deployment works, but there is no distributable repository artifact.
- WLED firmware/state can change outside the plugin. Check relevant runtime
  state at the start/end of a test, especially `maxpwr=40000`.
- HDR10 is now confirmed working end to end; HLG and Dolby Vision are not, and
  must not be assumed from it. The graphs share a code path, so a DV failure
  would look like an HDR10 success until someone plays DV.
- The colour path depends on a WLED setting the plugin only reads. If
  `if.live.no-gc` is changed and Jellyfin is not restarted, the plugin keeps the
  encoding it detected at startup and everything will be twice-corrected or
  not corrected at all. The page says so; the log line says which was chosen.
- The plugin now writes back a device binding it has repaired. That is the only
  configuration it writes on its own, and only after an event has matched.

## Settings on the page today

Five numbered sections, every field explained with its default named. Section 4
collapses and keeps its counts and total in the summary.

1. Device binding (merged from `/Devices` and `/Sessions`, connected first),
   enable switch.
2. WLED finder over mDNS, manual host/port fallback, realtime protocol.
3. LED delay, LED updates per second, hold while paused, stop fade.
4. LED counts per side with a live total, checked against the LED count the
   discovered controller reports.
5. Sampling resolution (six 16:9 presets, 96x54 to 480x270), sampling rate,
   sampling edge range (1-30%, recommended 10), LED brightness, colour
   intensity, white balance, gamma handling, ignore black borders.

A warning appears above the brightness slider when the controller reports
"force max brightness", read live from `GET /RealtimeAmbilight/Discovery/Settings`.

## Next actions (ordered)

1. **Actually click through the redesigned settings page in a browser.**
   Everything in the 2026-09-08 entry above was verified at the HTTP/backend
   layer and by static ID cross-checks, not by loading the page and using it
   -- which is exactly how the `hostName`/`host` mismatch shipped undetected
   last time. Check all four tabs render, the wall colour preset dropdown
   populates and round-trips through save/load, the black level floor slider
   updates its live preview, and (only if there's a reason to, since it
   writes to the real controller) the "Turn it off in WLED" button.
2. **Finish the colour calibration.** Brightness, colour intensity and white
   balance are all at 100% and the operator finds that too bright on this
   installation. Use the per-side preview flow to pick real values and save
   them.
3. Decide whether a warm **tint** is wanted. A gain cannot add red to a pure
   blue sky; only a tint can, and it deviates from the picture. Not implemented
   pending that decision.
4. Run a full TV checklist while tailing the log: start, pause, resume, seek and
   stop, on SD, HD and HDR. Confirm all four pipeline markers appear, that the
   configured fade is visible on stop, that resuming does not show WLED's own
   effect in between, and `maxpwr=40000` each time.
5. Verify the device binding filters: play on a device other than the bound TV
   and confirm the LEDs stay dark, then play on the TV and confirm they do not.
   The `ignored playback on device` log line reports both sides of any mismatch.
6. Find out why the decoder loses its lead under real playback when it holds it
   perfectly standalone. Instrument the gap between stamped frame position and
   clock over a whole film rather than reasoning from restarts.
7. Calibrate `OutputDelayMilliseconds` from 0 ms upwards, only when the LEDs are
   demonstrably ahead of the picture.
8. Add source-profile detection and separately validate HDR10, HLG and Dolby
   Vision before claiming HDR support. The HDR graph now runs, but only HDR10
   has been seen working.
9. Publish a release: tag it, attach the packaged zip and host the manifest so
   `sourceUrl` resolves. Build and manifest generation already exist.
