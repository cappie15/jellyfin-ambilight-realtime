# Jellyfin Realtime Ambilight

![Television with blue and coral Ambilight matching a mountain lake at sunset](docs/images/ambilight-hero.png)

*AI-generated concept illustration; actual output depends on your screen, LEDs and room.*

**Turn the wall behind your TV into part of the picture, on hardware you already
own.** Point an LED strip or a set of Philips Hue lights at your Jellyfin library
and the colour on screen spreads onto the room in real time — matched to the
exact frame playing, not a webcam or HDMI capture guessing a few hundred
milliseconds behind.

- **Built for your room, not a demo reel.** A guided calibration wizard walks
  your actual strip through six colour anchors and a white-balance pass, so the
  wall matches the screen on *your* hardware — not a generic factory profile.
- **Two independent outputs, at once if you want.** Drive a [WLED](https://kno.wled.ge)
  LED strip, a Philips Hue Entertainment area, or both together, each tuned
  and calibrated on its own.
- **Never touches your stream.** Runs its own, independent decode on the
  server purely to sample colour — the client's actual playback is never
  captured, delayed or re-encoded because of this plugin.
- **HDR handled on the GPU**, not guessed at: hardware tone-mapping brings
  HDR10/Dolby Vision content down to LED-friendly colour without crushing
  highlights or blowing out whites.
- **Free, open source, self-hosted.** Nothing leaves your server; no cloud
  account, subscription or companion app required.

> **Status: pre-alpha overall.** The WLED output has been tuned and validated
> against real hardware across multiple hands-on rounds and works correctly
> with the current defaults; **Hue Entertainment is pre-alpha and not yet
> reliable** — treat it as something to help test, not a lighting setup to
> depend on. Both have been verified on exactly one Jellyfin server, one WLED
> controller and one Hue bridge — that server has itself been run against
> both Jellyfin 10.11.x (releases up to 0.3.0) and Jellyfin 12 (0.4.0
> onward); compatibility beyond this one setup, on either version line, is
> not established. Build and packaging scripts are available — see
> [Installing](#installing).

## Vibe coding disclosure

This is an experimental, AI-assisted hobby project developed with coding agents,
including Claude and Codex. Generated code and documentation can contain bugs,
incorrect assumptions and incomplete edge-case handling. Passing unit tests and
working on the developer's hardware do not establish reliability on your setup.

Use at your own risk. Back up your Jellyfin configuration before installing and
keep your LED power supply, wiring and WLED current limits appropriate for your
hardware. The software is provided without warranty under the terms of the
[GPL](LICENSE). Bug reports and human code review are welcome; include versions,
reproduction steps and redacted logs, never API keys or credentials.

This is an independent community project, with no affiliation with or endorsement
from Jellyfin, WLED or Philips/TP Vision.

## How it works

```mermaid
flowchart LR
    A["Jellyfin<br/>playback events"] --> B["Playback<br/>coordinator"]
    B --> C["Independent<br/>FFmpeg decode"]
    C --> D["Edge sampling<br/>per zone"]
    D --> E["Linear-light<br/>interpolation"]
    E --> F["DDP / Raw RGB<br/>over UDP"]
    F --> G["WLED"]
```

Jellyfin reports what is playing and where the user is in it. The plugin opens
its own FFmpeg decode of that same source, scaled down to a small analysis frame
(160 × 90 by default), samples the edge zones, interpolates them across the
physical LED strip in linear light, and streams RGB24 frames to WLED.

Because the analysis is a second, independent decode, the client's playback is
not modified. Frames are scheduled against Jellyfin's playback clock; a
configurable output delay compensates for the television's display pipeline.
The extra decode consumes server CPU/GPU resources and can compete with playback
on a busy host.

## Requirements

| | |
|---|---|
| Jellyfin | **10.11.9** (releases up to 0.3.0) or **12.0** (0.4.0 onward) — the only two versions this has been run against, on the same physical server upgraded from one to the other |
| Build tools | .NET 10 SDK (0.4.0 onward; earlier releases were built with .NET 9); Bash and Python 3 for packaging |
| Analysis | Server-readable media and an FFmpeg installation compatible with the plugin's decode/filter path |
| WLED | tested against firmware **16.0.1** |
| Network | the server must reach WLED over UDP; mDNS for auto-discovery |

## Installing

### Via the plugin repository (recommended)

Add this URL under **Dashboard → Plugins → Repositories**:

```
https://raw.githubusercontent.com/cappie15/jellyfin-ambilight-realtime/main/manifest.json
```

Then install **Realtime Ambilight** from the catalog and restart Jellyfin. This
one URL always resolves to the file committed at the repository root on `main`,
so adding it once is enough -- every future release updates that same file
(see "Cutting a release" below), rather than requiring the URL to be re-added
each time. The manifest lists every published version, newest first, so
Jellyfin can also offer a downgrade if a release turns out to have a problem.

### Build from source

Installation from a checkout is manual. For a conventional Linux Jellyfin
service, build and copy **both** assemblies. Adjust the path and service
account for your installation; the install/restart commands require
administrator privileges. Container installations need their own mounted
plugin path and container restart instead.

Back up your existing plugin/configuration before replacing it. From the
repository root:

```sh
dotnet build src/Jellyfin.Plugin.RealtimeAmbilight/Jellyfin.Plugin.RealtimeAmbilight.csproj \
  --configuration Release --no-incremental

# <version> and <tfm> match plugin-package/meta.json's "version" and
# Directory.Build.props's TargetFramework -- 0.4.0 and net10.0 as of this
# writing, but check both files rather than trusting this comment to stay
# current.
sudo install -d -o jellyfin -g jellyfin \
  "/var/lib/jellyfin/plugins/Realtime Ambilight_<version>/"

sudo install -o jellyfin -g jellyfin \
  src/Jellyfin.Plugin.RealtimeAmbilight/bin/Release/<tfm>/Jellyfin.Plugin.RealtimeAmbilight.dll \
  src/Jellyfin.Plugin.RealtimeAmbilight/bin/Release/<tfm>/Jellyfin.Plugin.RealtimeAmbilight.Core.dll \
  "/var/lib/jellyfin/plugins/Realtime Ambilight_<version>/"

sudo systemctl restart jellyfin
```

Copying only the plugin assembly and leaving a stale `*.Core.dll` behind causes a
`MissingMethodException` at startup. Always deploy the pair.

### Build an installable archive

```sh
./build/package.sh
```

This recreates `artifacts/` and produces a versioned ZIP (e.g.
`realtime-ambilight_0.4.0.zip`) containing both assemblies and `meta.json`,
plus a `manifest.json` whose default download URL points at a matching
versioned GitHub release -- it only resolves once that ZIP has actually been
published there. Pass your own release base URL as the script's first
argument when hosting elsewhere.

The [CI workflow](.github/workflows/ci.yml) builds, tests and packages the project,
then uploads the ZIP and manifest as the `realtime-ambilight-plugin` artifact --
that CI artifact is a build check, not a publish step; it still has to be
attached to a GitHub release by hand (see below) to actually become installable.

### Cutting a release

1. Bump `version` (and `timestamp`) in `plugin-package/meta.json`.
2. Run `./build/package.sh`; it writes `artifacts/realtime-ambilight_<version>.zip`,
   `artifacts/ambilight-logo.png` and `artifacts/manifest.json`.
3. Fill in that version's `changelog` in `artifacts/manifest.json`, and copy the
   *previous* release's own version entry into the same `versions` array (newest
   first) -- the published manifest is the full, standing history of installable
   versions, not just the newest one.
4. Tag (`git tag vX.Y.Z`, `git push origin vX.Y.Z`) and create a GitHub release
   from that tag, uploading the ZIP, icon and manifest as release assets.
5. Copy the same (multi-version) `artifacts/manifest.json` to `manifest.json` at
   the repository root and commit it to `main` -- this is what the stable
   `raw.githubusercontent.com/.../main/manifest.json` URL above actually serves,
   so a release is not visible to existing repository subscribers until this
   step lands on `main`.
Packaging implementation: [build/package.sh](build/package.sh).

## Configuration

Everything is configured from the plugin's own page in the Jellyfin dashboard.
Each field carries its own explanation and names its default.

### Live tuning

A dedicated panel below the signal chain holds the fields worth adjusting
while actually watching the result: every one of them applies to the running
picture immediately and saves itself a moment after you stop moving it, no
restart and no Save button.

Every field is named **Ambilight ...** or **Hue ...** to say which output it
actually affects — the two run independent pipelines end to end (see
[How it works](#how-it-works)), so a change to one never touches the other.

| Setting | Default | Notes |
|---|---|---|
| Ambilight brightness | 130% (1-200%) | Above 100% pushes already-dim scenes brighter than the source picture — physical LEDs next to a bright HDR screen otherwise always read as dim, since nothing else in the pipeline can push a pixel brighter than it already is. |
| Ambilight colour intensity | 185% | Pushes each colour away from its own luminance, deepening it without changing brightness. |
| Ambilight black level floor | 3% | Picture edges darker than this go fully off instead of glowing dim; measured perceptually (what "3%" means on a waveform monitor), not as a raw linear-light fraction. |
| Ambilight white LED strength | 20% | Only shown once [RGBW](#rgbw-strips-send-a-real-white-signal) is on. |
| Ambilight sampling edge range | 27% | Fraction of each axis sampled inward from the active-picture edge. Lower is more literal, higher is calmer. |
| Ambilight smoothing | 150 ms (0-2000 ms) | Eases each physical LED toward a newly sampled colour instead of snapping to it. |
| Ambilight minimum colour hold | 100 ms | A colour must persist in the picture at least this long before the LEDs follow it, so a single flash frame cannot snap the wall to a colour barely there. |
| Hue brightness | 188% (1-200%) | Same reasoning as Ambilight brightness, independent of it. |
| Hue responsivity | 0 (0-100) | 0 is very reactive, 100 is beautifully smooth. |

These defaults reflect a validated real-room baseline rather than the
original untested guesses; every field can still be moved from there.

### Everything else

| Setting | Default | Notes |
|---|---|---|
| Playback device | *not bound* | Binds one Jellyfin device to this installation. Unbound means every device drives the LEDs. |
| WLED controller | `10.0.0.8`, HTTP port 80 | Developer defaults: select your own controller with mDNS discovery or manual entry. **Restart Jellyfin** after changing. |
| Realtime protocol | Auto | Raw RGB up to 490 LEDs, DDP above it; the protocol actually resolved from Auto shows on the WLED overview card. Advanced, rarely needed — restart after changing. |
| LED delay | 0 ms | Raise only when the LEDs visibly run ahead of the picture. Applies immediately. |
| LED updates | 30 fps | The output rate; **restart Jellyfin** after changing. Analysis itself is no longer separately configurable — it always follows the source's own frame rate. |
| Hold while paused | on | Holds the last colours; keepalive frames are sent every second. Off releases via WLED's timeout. Applies immediately. |
| Fade out / fade in | fixed, 2.5 s each | Fades to black before transmission ceases at stop, and fades up from black over the same span at the start of a new session — not a picture-relative fade (nothing to fade toward yet at start), a brightness ramp applied to whatever real content is already arriving. Not user-configurable. |
| LED counts | 265 / 150 / 266 / 150 | Clockwise from the top-left corner, viewed from the front. **Restart Jellyfin** after changing. |
| Analysis width | 160 px | Height follows from 16:9, so 160 × 90. Takes effect on the next playback session, no restart needed. |
| Ignore black borders | on | Detects letterbox/pillarbox bars before sampling. Applies immediately. |
| RGB gains, per-side trims, wall colour, hue-anchor curve | 100% / white / untouched | Calibration, not day-to-day tuning — see [Room calibration](#room-calibration). Applies immediately. |
| Send a real white signal (RGBW) | off | Uses the strip's own white LED for grey/white content instead of mixing it from red, green and blue. See [RGBW strips](#rgbw-strips-send-a-real-white-signal) below. Changes the wire format itself, so this one **does** need a Jellyfin restart. |
| Automatic LED gamma detection | on | Reads WLED settings at startup; falls back to the manual correction setting. **Restart Jellyfin** after changing. |

Select your playback device and controller, set the actual LED counts, save and
restart Jellyfin before the first playback test. The default 831-LED layout is
specific to the developer's installation.

### Room calibration

The **Ambilight** card's "Color calibration" section walks a nine-step
wizard -- white, then every RGB primary and secondary (red, yellow, green,
cyan, blue, magenta) -- each against a plain, rendered solid-colour swatch at
that colour's own canonical hue, with three sliders per colour: a **hue**
nudge (how far this colour actually sits from where the strip should show
it, e.g. "a touch pinker"), its own **brightness**, and its own
**intensity**. Those six calibrated points build one smooth correction curve
around the whole colour wheel -- shown back to the operator as an actual
chart once the wizard finishes, not just numbers -- so a colour between two
calibrated points is corrected smoothly too, not just the six points
themselves. White keeps its own, separate colour-temperature control
(warmer/cooler).

Click **Start calibration** first -- the TV link is only reachable while a
calibration is actually running. On an internet-facing Jellyfin an always-on,
unauthenticated page is its own exposure however little it can do, so the
whole TV-facing surface answers as if it did not exist until Start is
pressed, again the moment the wizard finishes or **Stop & release LEDs** is
clicked, and on its own after 20 minutes of inactivity even if nobody clicks
either -- an admin who closes the tab mid-calibration does not leave that
surface reachable forever. Open the short link **once**, full-screen, in the
TV's own browser -- it is deliberately as short as an address can be,
because a remote control types it one arrow key at a time, and it is always
plain `http://`: typed bare, some TV browsers guess `https://` first, which
fails with no certificate on a local address. The TV page polls the plugin
every 1.5 s and shows a small "Continue on your phone" hint throughout; on the
last step, the settings page's button becomes **"Done — finish calibration"**,
and the TV shows a clear finished screen once pressed.

Every step is a rendered flat swatch, sampled by exactly the same code path
real Jellyfin playback uses (the browser draws it to a canvas and the plugin
runs it through the ordinary edge-sampling and colour pipeline), shown
**full-screen**. One continuous LED strip usually runs the whole way round a
TV, so the wizard tunes brightness, saturation and white balance for **all
four sides at once**, not one side at a time; a separate, explicitly
optional "fine-tune each side" section still exists below it for the rare
strip that genuinely needs it.

Every slider updates the LEDs live as you drag it -- no Save, no round trip
-- and each one has small **&minus;/+** buttons beside it, in a proper row
even on a phone, so a step can be repeated by tapping the same spot while
watching the TV instead of the phone. The preview never writes WLED
configuration, and real playback always takes priority over it.

Once the nine steps are done, confirm against real content the same way you
would watch anything else: add whatever real content you already own the
rights to (a sunrise, sports, a film, motorsport, nature footage) to an
ordinary Jellyfin library, play it, and adjust the always-on **Live tuning**
panel while it plays (see [above](#live-tuning)) -- no plugin-specific step
needed, it works exactly like any other playback. A "Test videos" toggle on
that same panel additionally sets up two short synthetic clips (4:3 and
21:9 aspect-ratio bars) for the one thing real footage can't reliably
provide: exact, known black-bar geometry to test the black-border detector
against. Purely synthetic, ffmpeg-generated (no real footage, so no
licensing question), extracted from the plugin itself and registered as
their own temporary Jellyfin library with one click; a matching button
removes that library and deletes the files again once you are done. The
same click also creates an empty folder next to those two clips (its exact
path is shown in the panel) for your own real clips -- this plugin can't
ship or download third-party video for you, but anything you already hold
the rights to and drop in there is picked up by the same "Rescan" button, so
every installer can build the same richer tuning set from their own content.
If the
strip ever seems to read left and right swapped, that is far more likely the
physical strip's own wiring direction (routing the data cable from behind the
TV reverses left/right versus the viewer's own front-facing view) than a
plugin bug -- a quick way to confirm is to play any clip with clearly
different content on each side and watch which edge of the picture each end
of the strip actually follows.

Output is temporally dithered: WLED drives its LEDs straight from the byte
value, and linear light gives the darkest tones the fewest of the 256
available steps even though the eye resolves the most detail there, which
otherwise reads as visible "steps" on a slow fade to black. The encoder carries
each channel's rounding error into the next frame instead of discarding it, so
the strip alternates between two adjacent byte values in the right proportion
-- far above flicker fusion at any output rate this plugin uses -- instead of
holding one brightness for several frames and then jumping to the next.

### RGBW strips (send a real white signal)

Some strips have a fourth, dedicated white LED alongside red, green and blue.
Turning on **Send a real white signal (RGBW)** sends grey/white content to
that channel instead of mixing it from red, green and blue — less current
draw and a more neutral white, on a strip that actually has the fourth die.
Off by default, since not every strip does; restart Jellyfin after changing
it, like the other WLED connection settings.

A single white die cannot reach the combined peak brightness of red, green
and blue lit together, confirmed live via flicker photometry (alternating
the same physical LEDs between a mixed-RGB white and the white channel alone
at ~12.5 Hz, which cancels the two's considerable difference in colour
temperature and isolates a genuine brightness gap): the gap held completely
flat between white values of 100 and 230 out of 255, so no drive value closes
it. **Ambilight white LED strength** (default 20%) caps how much of a bright/near-white
pixel's shared grey is allowed onto the white die before the rest stays on
red, green and blue instead — those three, lit together, can reach brightness
the one white die cannot. Dim greys are unaffected either way, since the
die's own peak was never the limiting factor for them. Lower this further if
white content still looks dim on your strip; every RGBW die is different, and
50% is a reasoned starting point, not a universal constant.

### Controlling WLED from the plugin

The **WLED** tab is read-only by default: the plugin only ever reads WLED's
configuration to show its status and warn about settings that fight the
Ambilight. Checking **"Allow this plugin to fix WLED settings"** lets it also
turn off WLED's "force max brightness" for realtime data with one click,
instead of requiring a separate login to WLED's own interface. This is the
only WLED setting the plugin can write, and the request never names the ABL
power budget (`maxpwr`) or anything else -- that value is only ever displayed,
never changed by this plugin.

### Philips Hue Entertainment (optional) — pre-alpha

**Pre-alpha: not yet working reliably.** WLED is the finished, primary output
and works correctly with the current defaults; Hue does not yet. Enable it
to help test it, not for a lighting setup you depend on -- the settings page
itself carries the same warning on the Hue card and its modal.

The **Hue** tab adds Hue lights as a second, fully independent realtime output
alongside WLED, following the same picture on the same bound TV. Off by
default, and harmless when off -- nothing about WLED changes. Version 1
supports one paired bridge and one existing entertainment area (created and
edited only in the Hue app, never by this plugin); the square Bridge and
Bridge Pro are supported, the original round Bridge v1 is not, since it has
no Entertainment API at all. Entertainment streaming changes a light's
colour but never its power state, so a light that is off when playback
starts is turned on first — otherwise it would stay dark for the whole
session regardless of what is streamed to it; a light the session found off
is turned back off again once playback ends. See
[`docs/architecture/adr/ADR-011-hue-entertainment-integration.md`](docs/architecture/adr/ADR-011-hue-entertainment-integration.md)
for the full design, transport choice, and known limitations.

### Transports

| Protocol | Port | Limit |
|---|---|---|
| Hyperion Raw RGB | 19446 | one datagram, ≤ 490 LEDs |
| DDP | 4048 | multi-packet, PUSH on the final packet only |

## What it will not do to your WLED

This matters when a controller is driving several hundred LEDs on a real power
supply, so it is enforced rather than merely intended:

- **No persistent WLED configuration is ever written.** The plugin contains no
  WLED control-plane writer at all — only realtime UDP frames and the read-only
  `/json/info` endpoint used to confirm a discovered controller and
  `/json/cfg` used to read gamma/brightness settings.
- **Current limits are never touched.** `maxpwr` and the brightness limiter stay
  exactly as configured on the device.
- **Control is returned by ceasing transmission.** WLED's own realtime timeout
  (2.5 s on the reference controller) restores its previous effect. Measurements
  behind that choice are in
  [ADR-004](docs/architecture/adr/ADR-004-wled-realtime-protocol-strategy.md).
- **Discovery is read-only** and restricted to administrators.

## Known limitations and troubleshooting

- **Media and HDR:** local-media playback is the implemented path. SDR and one
  HDR10 setup have been observed working; HLG and Dolby Vision need separate
  visual validation. Source-profile detection and hardware-path selection are
  incomplete, so this is not a general HDR compatibility claim.
- **Timing:** the SDR analysis decode now uses VAAPI hardware acceleration
  when the configured device path exists on disk (falling back to software
  otherwise), matching the HDR path's own long-standing VAAPI graph. Measured
  on the reference host against a 4K HEVC source: software SDR decode ran at
  184% CPU and 0.3× realtime; VAAPI ran at 68% CPU and 15× realtime. This was
  the actual cause of the analysis decoder repeatedly losing its lead and
  restarting during a real test — not a regression in colour processing,
  which costs negligible CPU by comparison. A host with no VAAPI device node,
  the wrong driver, or missing permissions can still fall behind; start with
  0 ms delay and increase only if the LEDs visibly lead the picture.
- **No light:** check the enable switch, bound device, controller address and
  server-to-controller UDP access. Inspect Jellyfin logs for `playback start
  received`, `resolved source`, `processed its first decoded frame` and
  `sent its first WLED frame` to locate the failing stage.
- **Too bright or washed out:** check the Ambilight brightness control (1-200%, see
  Configuration above) and the settings page's brightness warning. On an
  RGBW strip, also check **Ambilight white LED strength** — content on the white channel
  has a lower physical ceiling than red, green and blue combined, so a value
  too high there can look dim rather than too bright. Gamma detection happens
  at startup; restart Jellyfin after changing WLED's gamma configuration. The
  plugin does not change it.
- **Old settings page or startup errors:** rebuild the plugin explicitly with
  `--no-incremental`, deploy both DLLs together and restart Jellyfin.
- **Controller outages:** richer recovery/retry handling and automated hardware
  integration tests are still outstanding.

## Development

```sh
dotnet build Jellyfin.RealtimeAmbilight.sln --configuration Release --no-incremental
dotnet test Jellyfin.RealtimeAmbilight.sln --configuration Release --no-build
```

Build the whole solution: the test project references Core only, so running tests
alone does not validate the plugin assembly or its embedded settings page.

The unit tests cover the boundaries where a silent mistake would be expensive:
transports that must never truncate a frame, clockwise layout and linear-light
interpolation, playback coordination and the latest-frame handoff, SDR and HDR
FFmpeg argument construction, sampling, and the mDNS parser — the last verified
against a real 192-byte packet captured from a WLED controller, including every
truncation of it and a forged compression-pointer loop.

The project is deliberately split so that everything decidable is testable
without Jellyfin:

| Project | Contains |
|---|---|
| `Jellyfin.Plugin.RealtimeAmbilight.Core` | Pure logic: protocols, sampling, layout, colour, discovery parsing |
| `Jellyfin.Plugin.RealtimeAmbilight` | Jellyfin surface: plugin, config page, hosted services, API |

Architecture decisions and the measured research behind them live in
[docs/architecture/adr](docs/architecture/adr/). They record what was actually
measured against real hardware, including the corrections — several decisions
here reversed an earlier assumption once it was tested. The current project
handover is [docs/handoff/CURRENT.md](docs/handoff/CURRENT.md).

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).
