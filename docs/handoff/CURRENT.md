# Current state

## Last known commit and worktree

Base commit: `16b02fa8f8bcfed698cdb46daa9d8054b96560fb` (`main`, one commit
ahead of `origin/main`). The implementation is intentionally still an
uncommitted worktree checkpoint: the workspace exposes `.git` read-only, so
`git add` cannot create `.git/index.lock`.

Uncommitted work includes the solution, plugin projects, tests, package
metadata, handover/review documents and updates to ADR-004, ADR-009 and the ADR
index. `git diff --check` passed on 2026-09-06.

## Handoff discipline

This file is the persistent session record. Update it after every meaningful
implementation unit, build/test result, architecture decision, environment
change, blocker, or change to the next-action order. Before ending a session,
record the exact validation commands/results and all uncommitted work so another
engineer can continue without relying on chat history.

## Build and test status

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
**53/53**. It covers DDP/Raw-RGB packet semantics, layouts/interpolation,
playback coordination, latest-frame handoff, SDR/HDR FFmpeg argument building,
sampling, WLED protocol selection, keepalive, fade, release-by-ceasing-
transmission and mDNS discovery parsing (against a captured real WLED packet). The target is
`net9.0`; this host uses .NET 10, so tests require `DOTNET_ROLL_FORWARD=Major`.

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

WLED is `10.0.0.8`, firmware 16.0.1, 831 LEDs. Its observed state after the
latest deployment was `live=false` and `maxpwr=40000`. **Never write WLED's
persistent configuration or alter `maxpwr`; it must remain 40 A.** Runtime
realtime frames and runtime release are permitted and are all this plugin uses.

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
- Earlier testing showed pause holds the live colours initially and resume
  restarts output. Do not assume a universal default is correct: tune the delay
  against the actual TV pipeline. The saved live value is currently **125 ms**
  (with keepalive 10 s, stop fade 2500 ms, analysis 60 fps); the code default is
  now **0 ms**, matching the documented "start at zero" guidance, which the
  configuration class previously contradicted with 250 ms.
- **Discovery verified end to end on the live host (2026-09-06).** With the
  configured host deliberately set to an unreachable `192.168.254.254`, the
  endpoint still returned the real controller in ~1.6 s, proving the result came
  from mDNS rather than the configured-host fallback. The log line
  `WLED discovery probed 2 candidate(s) and confirmed 1` records that the
  unreachable candidate was correctly discarded.
- **The stop/release defect is fixed** (see below and ADR-004 Amendment 4).

## Important operational notes

- Structural changes — WLED endpoint/protocol, physical LED counts and output
  FPS — are read when the hosted output service starts. Save them in the page,
  then restart Jellyfin before judging them. Delay, enable/disable, keepalive
  and fade values are read dynamically.
- Use the installed plugin's log markers to distinguish event/source/decoder
  failures from a WLED delivery failure:

  ```text
  Realtime Ambilight playback start received
  Realtime Ambilight resolved source
  Realtime Ambilight processed its first decoded frame
  Realtime Ambilight sent its first WLED frame
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

## Partially implemented / needs validation

- Final TV calibration: determine the useful output-delay range on the TCL/TV
  path; leave the live setting at 0 ms until an observed test justifies a
  positive delay. A UI slider exists (0–2000 ms, 25 ms increments).
- **Pause is now a switch, not a duration (2026-09-06).** `HoldWhilePaused`
  (default on) holds the paused frame on the LEDs until playback resumes or
  stops. The old `PauseKeepAliveSeconds` was a *resend interval* rather than a
  hold duration, and the live value of 10 s exceeded WLED's realtime timeout, so
  the LEDs would have dropped back to WLED's own effect and been yanked in again
  mid-pause. The resend cadence is now a fixed 1 s constant, chosen against
  measured release times of 2.46 s for DDP and 2.25 s for Raw RGB. The old
  property is retained unused so existing configurations keep deserializing.
- Pause semantics still need a final *visual* confirmation on the TV: colours
  should hold for the configured keepalive duration, then release as intended.
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
- Colour calibration, user-configurable logical sampling layout, black-bar
  handling refinements and smoothing are not exposed as product settings.

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
- HDR/DV must not silently fall back to an invalid colour path; treat the
  currently working live pipeline as SDR unless logs/source metadata prove
  otherwise.

## Next actions (ordered)

1. Run a final TV checklist while tailing Jellyfin logs: SD and HD start,
   pause, resume, seek and stop. Record whether all four pipeline markers
   appear and whether `live` returns false after release; confirm
   `maxpwr=40000` each time.
2. Calibrate `OutputDelayMilliseconds` using the settings slider and a
   repeatable motion scene. Start from 0 ms, adjust in small increments only
   when the LEDs are demonstrably ahead of the image, and restart Jellyfin only
   for structural settings.
3. **Done (2026-09-06):** the stop/release defect is fixed and recorded in
   ADR-004 Amendment 4. What remains is the visual confirmation in step 1 that
   the configured 2500 ms fade is actually seen on the LEDs, and that WLED's own
   effect returns about 2.3 s after the fade ends.
4. Bind the TV under "1. Bij welke tv horen deze leds?", save, then verify that
   playback on a *different* device leaves the LEDs dark and playback on the
   bound TV still drives them.
5. Add source-profile detection and separately validate HDR10/HLG/Dolby Vision
   before claiming HDR support.
6. Publish a release: tag it, attach the packaged zip and host the manifest, so
   `sourceUrl` resolves. The build and manifest generation already exist.
