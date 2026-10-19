# Current state

## 2026-10-19 (last round): measuring where the Hue channels actually are

The screen-extent change made each channel's colour pure but moved no
boundary, and the operator said so plainly. It was the right correction for
the wrong complaint. Stopped arguing from the bridge's numbers and built the
instrument this project has now wanted three times: `POST
RealtimeAmbilight/Hue/Identify?channel=N` lights one entertainment channel and
blacks out the rest, riding along on whatever is already streaming
(`HueFrameProcessor.SetChannelOverride`, which deliberately bypasses the
natural-light filter and the session fade so the colour is readable the
instant it is set). A fifteen-minute static clip ships alongside it in the
test library, purely to keep the stream open while channels are stepped
through.

Lit channels 0 to 4 one at a time with WLED off and photographed each, then
located them numerically against the camera frame (television at x 23-56%,
y 30-63%):

| channel | room position | where it actually lights |
| --- | --- | --- |
| 3 | X -0.40, Z -0.40 | under the set, left third (x 2-38%) |
| 4 | X 0.00, Z -0.40 | under the set, middle, centred at x 39% |
| 0 | X 0.40, Z -0.40 | under the set right third, plus the low part of both vertical strips |
| 1 | X 0.40, Z 0.00 | right side, centred at y 34% |
| 2 | X 0.40, Z 0.40 | right side, high, y 0-30% |

**The television's own centre is at x 39 to 40%, and channel 4 sits exactly
there**, so the horizontal model was right: the strip under the set really is
split 2/3/2 across its length, evenly. Its yellow starting at 71% is the
bridge's three-channel resolution and nothing else. Not fixable here.

**The vertical axis was wrong, and the measurement says why.** Channel 1 sits
at room Z = 0 but lands at y 34%, the *top* of the set, not halfway up it.
The television hangs well above the seated eye level the bridge puts its
origin at, and `HueScreenExtent` assumed the screen was centred on that
origin. So channel 1 was handed the middle of the picture while physically
sitting at the top of the screen, and the image's top half only ever reached
channel 2, the one light physically above the set. That is exactly the
reported "green section too high".

Added `CentreWidth`/`CentreHeight` to `HueScreenExtent` and
`HueScreenCentreHeightPercent`/`HueScreenCentreWidthPercent` to the
configuration, both defaulting to 0 (the previous behaviour). Live values for
this room, derived from the measurement rather than guessed: width 45, height
25, centre height -25.

267 tests passing (1 new, pinning that a screen above eye level changes which
light sees the top of the picture).

**Operational note worth repeating:** every deploy restarts Jellyfin and the
Android TV session loses `SupportsRemoteControl` until the app is fully
restarted. This round cost four of those. Batch code changes; the settings
themselves are read per frame and need no restart at all.

## 2026-10-19 (later again): the room is not the screen

With the quadrant test pattern on the television and the camera on the wall,
the operator confirmed the WLED perimeter is exactly right (verified against
the live preview: each side's colour boundary sits precisely at the middle of
that side) and reported two remaining Hue faults: the strip under the set has
its yellow section too far right, and the vertical strip beside the set has
its green section too high.

Pulling the entertainment configuration's member indices made it concrete.
The strip under the television has seven segments across three channels, at
room X of -0.4, 0.0 and +0.4. `HueChannelMapper` read those positions as
fractions of the picture directly, so the three channels sampled the picture
at 30%, 50% and 70% of its width. **A strip hugging the set only ever saw the
middle 40% of the image**, and its colour boundary landed at 71% of its own
length (segments 5 and 6 of 7) rather than halfway. The vertical strips have
the same problem on Z.

The bridge reports positions against the **room**, -1 to +1 wall to wall. A
television does not span its wall, and nothing in this mapping ever accounted
for that. Added `HueScreenExtent` plus two settings, `HueScreenWidthPercent`
and `HueScreenHeightPercent` (default 100, which is the previous behaviour
exactly), that divide the reported position by the screen's own half-extent
before mapping. Set to 40 on the reference installation, matching what the
strips hugging the set actually report.

Computed over the real channel positions with the quadrant pattern, before
and after:

| channel | position | at 100% | at 40% |
| --- | --- | --- | --- |
| 0 (strip right, low) | X 0.40, Z -0.40 | `#deff00` | `#ffff00` yellow |
| 2 (strip right, high) | X 0.40, Z 0.40 | `#7aff00` | `#00ff00` green |
| 3 (strip left, low) | X -0.40, Z -0.40 | `#7a00de` purple | `#0000ff` blue |
| 7 (lamp, left) | X -0.78, Z 0.09 | `#e4006f` pink | `#ff0000` red |

Every channel now receives the colour of the quadrant it physically sits in,
where most of them previously received a wash.

**What this does not fix, and cannot:** channel 4 is the middle of the strip
under the set and spans segments 2, 3 and 4 of 7. It straddles the picture's
own blue/yellow boundary, so it is correctly neutral, and 43% of that strip
will stay a blend. That is the bridge's three-channels-per-strip grouping
(ADR-011), not this mapping.

Also worth recording as an operational cost: **every plugin deploy restarts
Jellyfin, and the Android TV session loses `SupportsRemoteControl` each time
and does not get it back without a full app restart.** Batch deploys during a
live session with the operator, or each one costs a round trip.

266 tests passing (2 new).

## 2026-10-19 (later still): the colour tuning measured against real frames

The operator reported, watching the first three minutes of The Lion King
(1994), that at 58 s the strip was "te felrood en niet oranje genoeg", that
grey sometimes read too blue, and that red sometimes read too red. Rather than
judge that off a photograph of a warm wall, recorded the camera continuously
for 209 s alongside playback, pulled the matching 139 analysis frames out of
the film in one decode pass, and ran **the plugin's own colour code** over
them (a small harness against `Core`, so no reimplementation of the pipeline
could introduce its own error).

**First correction, to this project's own assumptions:** `SaturationPercent`
on the live host is **100**, not the 175 recorded in the 2026-09-14 promotion
round. A sweep built on the remembered value produced nonsense (175 and 100
scoring identically) before that was noticed. Pull the live config; do not
trust the promotion notes for what is actually set.

**What was actually wrong:** at 58 s the picture edge is orange (hue 21 to
29 degrees, saturation 0.88 to 0.97) and the LEDs were being driven to
**saturation exactly 1.00 on all four sides**. Orange at full saturation is
red. Across the whole three minutes the saturation error by picture colour
was: yellow +0.37, orange +0.15, grey +0.11, magenta +0.10, red +0.08. The
dominant cause was a single anchor, `YellowIntensityPercent` at 172.

Grey picking up +0.11 saturation is the wall-colour correction at 100% against
a warm greige wall (`#cabfaf`) pushing the output cooler, which is exactly the
"grey too blue" report. Worth keeping in mind that this metric measures what
the LED emits, not what the wall reflects back, so that one correction is the
only item on the list the operator's own eyes judge better than the number.

**Applied to the live host and re-measured over the identical 139 frames:**

| | before | after |
| --- | --- | --- |
| measurements clipped at saturation 1.0 | 126 | 44 |
| sides driven fully off | 35 | 15 |
| overall deviation from the picture | 0.172 | **0.058** |

`YellowIntensityPercent` 172 to 100, `BlueIntensityPercent` 173 to 120,
`BlueBrightnessPercent` 38 to 80, `RedGainPercent` 127 to 110,
`BlueGainPercent` 73 to 88, `BlackLevelFloorPercent` 8 to 4,
`WallColourCorrectionPercent` 100 to 40. Per-colour saturation error after:
yellow +0.02, orange +0.03, red +0.03, grey +0.04, blue +0.03. For reference,
neutralising every anchor and gain scored 0.052, so this keeps nearly all the
benefit while leaving some installation-specific correction in place.

**Not promoted to defaults.** These are this room's numbers, measured against
one film, and the previous three promotion rounds are exactly how the shipped
defaults drifted into over-correction in the first place. The method is the
reusable part: record, extract, run the real pipeline, sweep.

**Verified on the wall without the television**, by arming the calibration
wizard and posting the real 58 s frame straight to `Calibration/PhotoFrame`
as full-range sRGB: the strip reads plainly orange where it was red before.
That path is a good general way to test colour changes when the TV session is
not controllable.

**Also measured, and not a fault:** at 4:18 the operator expected green on
both sides. The picture's own left and right edges there are warm brown (hue
23 to 33); the green grass sits in the lower left and is very dark (value 0.07
to 0.19), so the black-level floor extinguished it. Sampling depths of 7, 15
and 25 percent all leave the left edge brown, so depth is not the lever. The
floor at 4% is.

**Still open:** the Hue vertical-strip boundary has not been re-verified since
the Z-axis fix. The TV session lost `SupportsRemoteControl` after the plugin
deploy restarted Jellyfin and has not got it back, so playback cannot be
driven from here; the Android TV app needs a full restart.

## 2026-10-19 (later): Hue's Z axis was half-range too, exactly like Y

The operator reported that on every Hue gradient strip the boundary between
the picture's top and bottom colours sat far too close to the ceiling, and
that the strip under the television put its own boundary far too far to one
side. Chased it with a camera in the room and four-colour quadrant test clips
(`Position A/B/C`, generated into the plugin's own test-video library) rather
than a film: a film never holds a hard edge still long enough to read a
gradient against.

**Root cause, in `HueChannelMapper.Map`:** `var z = Math.Clamp(position.Z, 0d, 1d)`.
The bridge reports Z on the same -1 (floor) to +1 (ceiling) scale as X, and
both of this operator's entertainment areas are full of negative values: the
three gradient strips sit at Z = -0.4, and two of the five lamps at -0.235 and
-0.645. The clamp pinned every one of them to 0, which this mapping reads as
the very bottom of the picture. **Five of the ten channels in the TV area were
showing the picture's bottom edge regardless of where they physically are**,
and every remaining channel sat far below its real height (a lamp at Z = 0.094
was read as 5% up the picture instead of 55%). That is precisely the reported
symptom: the bottom edge's colour covering almost the whole room, with only a
light at the literal ceiling ever showing the top of the picture.

This is the **same mistake, on the neighbouring axis**, as the 2026-09-15 Y
finding. Worse, that finding's own write-up explicitly exonerated this one:
"X and Z's own documented -1..1/0..1 ranges were never wrong; only Y was."
That sentence was never checked against bridge data either. The evidence was
sitting in the same API response the whole time. Both `HueChannelMapper` and
`HuePosition` now carry the corrected convention, and the latter says plainly
that any remaining unverified claim about it should be treated the same way.

Fixed: `z = (Clamp(position.Z, -1, 1) + 1) / 2`, the same shape of conversion
Y already had. Two new regression tests pin the axis directly (a negative Z is
below eye level, not pinned to the bottom edge; floor, middle and ceiling span
the whole picture), and the six existing position tests were rewritten onto
the corrected scale rather than deleted.

**Still a real limitation, unchanged and not fixable here:** channel 0 of the
TV area drives segments of all three gradient strips at once, and channels 1
and 2 drive segments of two. The bridge groups them that way and rejects a
client-supplied channel list (ADR-011). So the right-hand third of the strip
under the television is forced to show whatever colour the two vertical
strips' low segments get, whatever this mapping computes. Re-doing the
entertainment area's light positions in the Hue app is the only lever the
operator has on that.

**Also found, unrelated:** the settings page's Live preview panel has never
worked at all. `config.js` fetches `RealtimeAmbilight/LivePreview`, but the
controller serves it at `RealtimeAmbilight/Discovery/LivePreview`, so the
panel has always shown "Could not reach the preview right now." That also
retires this file's own 2026-10-19 (earlier) explanation for the mirrored
bottom strip: the operator cannot have been looking at that panel, because it
has never rendered. The orientation fix made there is still correct and is
still needed, but what the operator actually saw is not yet explained.

264 tests passing (2 new).

## 2026-10-19: the bottom was never mirrored on the wall, the preview panel was

Settled the open question from 2026-10-09 with a camera in the room rather
than more reading. The operator put an MPEG-TS camera stream on
`http://10.0.0.158:8050/` (1080p h264, 12 fps) pointed at the television, so
the strip could be driven and photographed in the same step.

**Physical wiring: correct on all four sides.** Drove WLED directly over DDP
and lit the first 40 LEDs of each run in its own colour (top red, right green,
bottom blue, left magenta), then photographed the wall. Red landed top-left,
green top-right, blue **bottom-right**, magenta bottom-left: exactly the
clockwise-from-the-top-left order the plugin assumes. WLED's own segment is
`rev:false`/`mi:false` over one 831-LED segment, so nothing is being flipped
in the controller either. **None of the new `Reverse*Leds` boxes should be
ticked on this installation.**

**Software mapping: also correct**, and now pinned by a test that covers the
whole chain in one assertion rather than leaving it to be read across two
files (`TheBottomRunStartsAtThePictureRightAndEndsAtThePictureLeft`): a frame
whose bottom half is red on the left and blue on the right must light the
bottom run's first LED blue, because that LED sits at the bottom-right of the
television.

**The actual bug was in the settings page's Live preview panel.**
`GetLivePreview` returns every side in physical LED order, which runs
clockwise, so the bottom array starts at the right of the picture and the left
array starts at the bottom. Both were rendered with a plain `flex-direction:
row`/`column`, putting each of those two runs' first LED at the wrong end of
the television. The bottom reads as mirrored against the picture and the left
side does too, while the wall itself was right all along. The bottom is the
one anyone notices: it is long, horizontal, and sits directly opposite a top
row that *is* drawn correctly. Fixed with `row-reverse` and `column-reverse`;
the server-side ordering is untouched, since physical order is the right thing
for an API that describes the strip.

So 2026-10-09's `LedSideDirections` work did not fix this operator's symptom.
It stays: a perimeter of four cut runs genuinely can have a side soldered
backwards, there was no way to express it, and the camera test that proved
this installation is fine is the same test that would have found the other
case.

262 tests passing (1 new).

## 2026-10-09: per-side LED wiring direction, and a pause the server may never have seen

Two live reports, investigated together.

### One side reading as mirrored had no possible remedy in software

The operator judged, from the test clips, that the bottom run tracks the
picture backwards while the other three sides look right. Read the whole
sampling and mapping path before touching it: `EdgeSampler.CreateZones` builds
all four runs consistently clockwise (top left to right, right top to bottom,
bottom right to left via `leftToRight: false`, left bottom to top), and
`LinearLightInterpolator.InterpolatePerimeter` lays them into the frame in that
same order. No bug there -- but also **no way whatsoever to express a strip
wired any other way**. That single clockwise-from-the-top-left assumption was
hardcoded end to end, so a perimeter built from four cut runs joined by corner
wires (which is what this installation is: 265/150/266/150) had exactly one
remedy for a run soldered the other way round, and it was a soldering iron.

New `LedSideDirections` (Core/Layout), four `Reverse*Leds` configuration flags,
and an optional parameter on `InterpolatePerimeter` that reverses a side's
resampled run after resampling. Deliberately after: the picture geometry, the
logical sample counts and the per-side colour trims (which key off the physical
index block, unchanged by a within-block reversal) all stay exactly as they
were -- this is a wiring correction and nothing else. Default is false on every
side, which reproduces the previous behaviour bit for bit. Resolved live from
configuration like every other tuning value, so a tick takes effect on the next
frame, and read through the same helper by the calibration preview path so a
reversed side is corrected while the operator is judging a swatch too.

**Not yet confirmed which side is actually backwards, or whether it is wiring
at all.** WLED's own segment reports `rev:false`/`mi:false` across one 831-LED
segment, so the controller is not flipping anything; the question is purely how
the physical run was soldered. New `tools/led-orientation.py` settles it
without guessing: `sides` paints each side the plugin's own colour and drives
the first 15 LEDs of each white, so every side's starting corner is visible at
a glance (expected, correctly wired: white markers at top-left, top-right,
bottom-right, bottom-left); `chase` walks one dot from index 0 so the start and
direction of the whole run can be read directly. Both speak DDP on 4048 exactly
as the plugin does. **Not yet run against the live strip** -- it drives real
LEDs in an occupied room and was written late in the evening.

### Playback pausing by itself on the television

Reported: playback on the bound TCL pauses by itself several times per film,
resumes instantly from the remote, and stops happening with the plugin
disabled. The LEDs keep running through it.

**Nothing in this plugin can pause a session** -- grepped the whole tree for
`SendPlaystateCommand`/`SendMessageToSession`/any `ISessionManager` use beyond
the three event subscriptions, and there is none; the adapter only reads. So
either the pause is server-visible and something else issues it, or it never
reaches the server at all.

That distinction is the whole investigation, and the LEDs already hint at the
answer: `HoldWhilePaused` is on, so a server-visible pause freezes the strip.
The strip kept moving, which means `PlaybackProgressed.IsPaused` never arrived.
New Information-level logging in `JellyfinPlaybackEventAdapter` records every
pause/resume transition the bound device actually reports (position, session,
and whether the progress was server-generated), mirrored into the settings
page's activity log, so the next occurrence answers it outright: a line means a
real session pause, silence while the picture is frozen means the stall is in
the client or the path feeding it.

**Measured and ruled out: CPU.** Ran the plugin's exact HDR analysis graph
against a real 2160p HDR10 remux from the NFS library on this host: 12 to 18%
of one core steady state after start-up, nowhere near a contention problem on
four cores. What the same measurement did show is that the analysis decoder
reads about **10 MB/s (roughly 80 Mbit/s, the file's own bitrate) from the NAS
for the entire duration of playback** -- `-re` paced, a second full-rate reader
on the same file as the one feeding the television, about five seconds ahead of
it. All media here is NFS from 10.0.0.90 on volumes 90 to 96% full. The NFS
client page cache should absorb much of the duplication (the analysis read runs
*ahead*, so the television's own read should often be a cache hit), which
weakens this as an explanation, but it is the only mechanism found so far that
both scales with playback and disappears when the plugin is disabled.

261 tests passing (3 new, covering one side reversed, every side reversed, and
`LedSideDirections.Clockwise` producing byte-identical output to the previous
default).

## 2026-09-15 (later): Hue's own depth axis (`Y`) was inverted, not just unverified

Investigating why the operator's vertical Hue gradient strip (beside the TV) tracks the picture correctly while the horizontal one (under the TV) reads as barely more than a single shifted colour, first confirmed the channel-grouping question was a genuine, unfixable Hue Bridge platform ceiling (see the ADR-011 entry immediately below this one) -- but that didn't explain the difference between the two strips, since both get split into 3 channels each regardless.

Checked the actual axis convention against four independent real fixtures on the operator's own bridge instead of re-guessing: both gradient strips, physically repositioned to sit against the TV wall, reported `Y = 1.0` afterward -- if `Y = 0` meant "at the screen" as this project's own docs and `HueChannelMapper` assumed, moving a light onto the screen wall should have pushed `Y` toward 0, not to the opposite extreme. Two more fixtures the operator described as "halfway from the TV" and "just rear of the viewing position" reported `Y` of roughly −0.25 and −0.62 -- consistent with a midpoint at 0, not at 0.5.

**All four points agree: `Y` runs −1 (behind the viewer) to +1 (at the screen)**, the mirror image of the 0..1/0-at-screen convention this project shipped with, which additionally clamped `Y` to `[0, 1]` and silently discarded every real position on the "behind the viewer" half of the actual range. Fixed in `HueChannelMapper.Map`: the bridge's own `Y` is now converted via `(1 - Clamp(Y, -1, 1)) / 2` to the method's existing internal 0-at-screen/1-behind-viewer depth scale -- every downstream blending threshold is unchanged, only the input conversion was wrong. This was a real, previously-shipped bug: every Hue installation using this integration before this fix had its depth blending backwards for any light not sitting exactly at the documented (wrong) midpoint.

258 tests passing (`HueChannelMapperTests`' seven cases rewritten to the corrected `Y` convention, same coverage).

## 2026-09-15: the calibration wizard preview was dithered, and a held swatch cannot be

The operator reported the 20%/40% grey step's Tint slider "oscillating": pressing "−" once made the preview visibly darker/lighter, and the very next "−" press flew back toward the original colour, alternating with every click. Their own guess, unprompted -- "lijkt iets met dithering misschien" -- was exactly right.

**Root cause**: `JellyfinWledOutputService.SendCalibrationPhotoFrameAsync` encoded every calibration frame through `_calibrationEncoder`, a `DitheredRgb24Encoder`/`DitheredRgbw32Encoder` instance -- the same stateful, error-diffusion encoder real playback uses. Dithering deliberately carries each frame's rounding error into the next so that, averaged across *many different* frames of *changing* video, a fractional target reads as a smooth colour instead of a stepped one. A calibration swatch is the opposite case: a single target held static for as long as the operator is looking at it. Every frame sent for that same held target, the ditherer alternates between two adjacent byte values exactly as designed -- invisible on fast-moving video, but a distinct, regular flicker on something deliberately held still. `DitheredRgbw32Encoder`'s own doc comment already recorded this exact tradeoff for the white channel specifically ("a held or near-still fractional target is exactly what error diffusion must alternate... reads as a distinct, regular brightness flicker") -- the calibration path was simply never given the same treatment.

**Fix**: new `Rgbw32Encoder` (Core/Output) -- the stateless sibling `Rgb24Encoder` already was for RGB24, mirroring `DitheredRgbw32Encoder`'s own white-extraction/compensation maths exactly but rounding every channel plainly, carrying nothing forward. `SendCalibrationPhotoFrameAsync` now calls `Rgb24Encoder.Encode`/`Rgbw32Encoder.Encode` instead of the dithered pair; the now-unused `_calibrationEncoder` field (and its `DitheredRgb24Encoder`/`DitheredRgbw32Encoder` allocation) is gone. Real playback (`AmbilightFrameProcessor`) is untouched -- still dithered, exactly as it should be for actual video.

258 tests passing (3 new, mirroring `DitheredRgbw32EncoderTests` but asserting the opposite: identical bytes on every call for the same unrepresentable target, not an alternating pair).

## 2026-09-14 (later still still): Dolby Vision was never actually reaching the HDR graph

The operator tested three titles back to back: an HDR10 film ("werkt nu perfect"), a Dolby Vision film ("de kleuren zijn compleet fucked up"), and a plain non-HDR film ("de kleuren wijken enorm af"). Root-caused the Dolby Vision report by reading the actual resolver, not by guessing at the colour maths again.

**Root cause**: `JellyfinFfmpegAnalysisSourceResolver.ResolveAsync` never set `FfmpegAnalysisSource.VideoProfile` -- it stayed at its default `null` for every single source. `FfmpegAnalysisWorker.RunAsync` picks the HDR graph only `if (source.VideoProfile is { } profile)`, so **every title, HDR10 and Dolby Vision alike, was silently going through the plain SDR analysis graph** this whole time. ADR-006's entire HDR/DV command-building path (`FfmpegHdrAnalysisCommandBuilder`, `HdrVideoProfile`) was fully implemented and unit-tested in isolation, but nothing in the real, running plugin ever actually constructed a source with a non-null profile -- confirmed by grepping the whole main plugin project for `HdrVideoProfile.` and finding exactly one hit, inside the HDR builder itself. This is exactly the "naive path" ADR-006 documents as producing a green/cyan cast on genuine Dolby Vision, and explains why HDR10 still looked fine (PQ read through a plain graph happened to be plausible for that particular film) while DV was visibly wrong.

**Fix**: new `HdrVideoProfileMapper.Resolve(VideoRangeType, ILogger)` maps Jellyfin's own `VideoRangeType` (confirmed by reflecting the actual `MediaBrowser.Model`/`Jellyfin.Data` assemblies rather than assumed: `Unknown, SDR, HDR10, HLG, DOVI, DOVIWithHDR10, DOVIWithHLG, DOVIWithSDR, DOVIWithEL, DOVIWithHDR10Plus, DOVIWithELHDR10Plus, DOVIInvalid, HDR10Plus` -- a superset of ADR-006's own table) to the `HdrVideoProfile?` the graph builder needs, per that same table. Wired into the resolver via the `MediaStream` it already reads for frame rate.

**Verified against real hardware and real content on this exact host** (not just unit tests): pulled a genuine Dolby Vision file's own DOVI configuration record (`dv_profile=8`, `dv_bl_signal_compatibility_id=0`) via `ffprobe`, then ran both the naive VAAPI SDR graph and the real HDR graph (`apply_dolbyvision=true`, `-init_hw_device vulkan=vk`) against it directly with `ffmpeg` -- mean channel values diverged sharply (naive R/G/B roughly 35/39/41; DV-aware roughly 20/30/30, with G-R widening from +4 to +30), the same "differ enormously, in the direction the RPU predicts" signature ADR-006's own validation used, confirming the DV-aware path genuinely engages real tone-mapping rather than erroring or no-op'ing. Also spot-checked the VAAPI-vs-software SDR path (in case of a separate colourspace-tagging bug after `hwdownload`, suspected but not yet found): a real SDR BT.709 1080p file showed only a small, consistent +1 to +2.5-per-channel offset between the two paths, not a wild colour-matrix mismatch -- so no fix was made there; the plain non-HDR "colours wildly off" report most likely comes from the operator's very aggressive global tuning (`SaturationPercent` 175%, `CyanIntensityPercent` 200%, asymmetric `RedGainPercent`/`BlueGainPercent` 127%/73%), promoted from and validated only against one HDR10 title, reacting very differently on a typical SDR scene's much milder colour grading -- needs the operator's own re-test to confirm, ideally with those two turned down temporarily.

255 tests passing (13 new, `HdrVideoProfileMapperTests`).

## 2026-09-14 (later still): default promotion round 3, two anchor ranges widened by 10%

The operator tested a real HDR10 film (The Lion King) against today's live-tuned config and reported it "werkt nu perfect" (works perfectly now), then explicitly authorized promoting everything they had tuned in the Ambilight and Hue settings to the new shipped defaults.

**Promoted from the live config** (pulled directly off this host): `SamplingDepthPercent` 27→7, `SaturationPercent` 185→175, `RedGainPercent` 150→127, `BlueGainPercent` 50→73, `WledSmoothingMilliseconds` 150→1200, `HueResponsePercent` 0→50, `HueBlackLevelFloorPercent` 1→18, `BrightnessPercent` 130→158, and, unlike the two previous promotion rounds, the six hue-anchor triples themselves this time (per the operator's own explicit instruction, not the usual "installation-specific, do not promote" caveat): `RedIntensityPercent` 100→105, `GreenHueShiftDegrees` 0→-2, `GreenIntensityPercent` 100→121, `BlueHueShiftDegrees` 0→2, `BlueBrightnessPercent` 100→38, `BlueIntensityPercent` 100→173, `YellowHueShiftDegrees` 0→-3, `YellowIntensityPercent` 100→172, `CyanHueShiftDegrees` 0→-5, `CyanIntensityPercent` 100→200, `MagentaHueShiftDegrees` 0→21, `MagentaBrightnessPercent` 100→53, `MagentaIntensityPercent` 100→198. Still NOT promoted, same as every previous round: `WallColourHex`/`WallColourCorrectionPercent`, per-side trims, and every device/bridge identifier -- genuinely installation-specific, not what the operator meant by "the settings I did."

**Widened two ranges by 10%**, since the operator flagged that several values were sitting at their slider's own ceiling: the Intensity anchor's ceiling (200%→220%, `PerimeterColourTuning.Anchor`) -- `CyanIntensityPercent` had hit exactly 200 -- and the Hue-shift anchor's range (±21°→±23°) -- `MagentaHueShiftDegrees` had hit exactly 21. Both the server-side clamp and the wizard's own anchor sliders (`anchorControlHtml` in config.js) were updated together. **Checked and explicitly did NOT widen** `WallColourCorrectionPercent`'s 100% ceiling even though it was also sitting exactly there: the formula behind it (`WallColourCorrection.FromHtmlColour`) already reaches its own full computed correction at `amount = 1` (100%) -- `Gain = clamp(min(2, ratio), 1, 2)` regardless of ratio -- so any value above 100% would be a pure no-op, not a real capability. Confirmed by reading the formula before touching it, not assumed from the pattern of the other two.

**Also fixed, found while touching the anchor ranges**: `renderColourDeviation()`'s own display clamp in config.js had drifted stale across all of this project's previous anchor-range changes -- still showing hue ±21°/brightness 50-100%/intensity 50-100% in its own comment and `Math.max`/`Math.min` calls, when the server side had already moved to ±23°/20-100%/50-220%. This silently under-reported every anchor's own real, applied deviation in the settings-page summary the moment any anchor actually used the widened floor or ceiling -- which, with today's promoted defaults, is now most of them. Fixed to match the current real ranges.

242 tests passing (unchanged -- this round changes only default values, two clamp bounds, and one display-only clamp; no new behaviour to test beyond what the existing anchor/tuning tests already cover).

## 2026-09-14 (later): split HueEntertainmentService.cs into three focused classes

`HueEntertainmentService.cs` had grown to 858 lines across this session's several rounds of fixes (black-level floor, fade in/out, position resolution, the `HueStreamState` grouping refactor). Split out the two most self-contained concerns, both new files under `Hue/`:

- **`HueSessionSnapshotCoordinator`** (internal): reading a light's state before streaming takes over and applying the configured end-of-session behaviour (restore/turn off/warm-white-dim) afterward -- `ResolveLightIds`, `CaptureAsync`, `ApplyEndBehaviourAsync`. Pure light-state bookkeeping via `IHueLightControl`; knows nothing about DTLS, the pump loop, or the state machine.
- **`HueFrameTransmitter`** (public, for the same reason `ComputeFadeStepColours` already was -- no `InternalsVisibleTo` exists in this project, and the test project is a separate assembly): the pending-frame scheduling queue, packet building, and keepalive pacing that used to be `SendFrameOrKeepAliveAsync`'s own inline logic, plus `ComputeFadeStepColours` (moved from `HueEntertainmentService`, test file updated to call it there instead).
- The old `HueStreamState` nested class is gone: its transmission fields (`LastPacket`/`LastChannelColours`/`LastSendAt`) moved into `HueFrameTransmitter`, and its two backoff fields (`ConsecutiveFailures`/`NextRetryAt`) became plain fields directly on `HueEntertainmentService` -- with only two fields left, a wrapper object no longer earned its keep.

`HueEntertainmentService` itself (694 lines, down from 858) is left as the actual orchestrator: the pump loop, the state machine dispatch, and the connect/stop lifecycle -- wiring the two new collaborators together rather than doing their work inline. Pure mechanical extraction, no behaviour change: 242/242 tests passed before and after, with no test rewritten beyond the one call-site rename (`HueEntertainmentService.ComputeFadeStepColours` -> `HueFrameTransmitter.ComputeFadeStepColours`).

## 2026-09-14: per-section "Reset to defaults", and closing the defaults-drift bug for good

- **Per-section reset, replacing the one global button**: each of the four settings modals (TV, WLED, Ambilight/colour calibration, Hue) and the Live Tuning panel now has its own "Reset to defaults" button, resetting only that section's own fields against a confirm dialog, instead of one button at the bottom of the page that reset everything (LED layout, colour calibration, bound TV) at once. A paired Hue bridge's own host/id/entertainment-area selection is never touched by any reset -- unlinking it stays a separate, explicit action, same as before.
- **Root-caused and closed the actual defaults-drift bug this project has now hit at least twice** (see the two "default promotion round" entries below this one): config.js carried two of its own hand-written copies of PluginConfiguration's defaults (`factoryDefaultConfiguration()` for the old reset button, and a `defaults` fallback object used when populating the form) that both silently drifted out of sync with the real C# defaults every time one changed there -- confirmed live: `BrightnessPercent` read 158 in config.js against a real default of 130, `SaturationPercent` 140 vs 185, `SamplingDepthPercent` 5 vs 27, `OutputFramesPerSecond` 30 vs 60, `WledSmoothingMilliseconds` 110 vs 150, `WhiteChannelStrengthPercent` 50 vs 20, `HueBrightnessPercent` 169 vs 188, `HueResponsePercent` 20 vs 0, `AnalysisWidth`/`AnalysisHeight` 160×90 vs 320×180 -- meaning the old global reset button would have silently reset those fields to the *wrong*, stale numbers. New `GET RealtimeAmbilight/Discovery/DefaultConfiguration` endpoint returns `new PluginConfiguration()` directly; config.js fetches it once and uses it as the only source of truth for every reset button and the form's own missing-field fallback. Structurally cannot drift again the way the two hand-copied lists did.
- **Fixed the same drift in the Live Tuning panel's own on-screen "Default: X" labels**, which had quietly gone stale the same way (158%/140%/5%/110 ms/50%/169%/20/30/160×90, corrected to 130%/185%/27%/150 ms/20%/188%/0/60/320×180) -- caught while adding the tuning panel's reset button, since resetting a slider to a number that contradicted its own on-screen "Default" label would have read as broken.

242 tests passing (unchanged -- this is a settings-page/config.js and one small read-only endpoint change with no new server-side logic to test beyond what the existing UI smoke test already covers).

## 2026-09-13 (later still): finished the live-preview feature that had been left half-done

Earlier this session, `LivePreviewSnapshot` and a `_latestPhysicalFrame`
field were added to `JellyfinWledOutputService.cs` in preparation for a
"current live feed" panel (explicitly requested, "zoals hyperhdr dat ook
heeft"), but the actual `GetLivePreview()` method, the API endpoint, and the
UI were never written -- caught by a self-audit after the operator asked
directly whether anything from their prompts had been missed.

Finished now: `GetLivePreview()` downsamples the last physical-LED frame
actually sent to WLED into at most 24 `#rrggbb` swatches per side (in
physical order), computed only when called (no continuous background work);
new `GET RealtimeAmbilight/LivePreview` endpoint; a "Live preview" toggle on
the Live Tuning panel next to "Test videos", rendering a small TV-shaped
border with a swatch strip per side, polled every 700ms *only* while that
panel is open (`toggleLivePreviewPanel`/`livePreviewTimer` in config.js) --
explicitly resource-conscious per the original request ("alleen wanneer dat
niet teveel resources eist"). No dedicated unit test: `JellyfinWledOutputService`
has never had its own test harness (it is the large DI-heavy integration
service), consistent with the rest of this file's existing coverage.

240 tests passing (unchanged -- this addition has no test of its own, see
above).

## 2026-09-13 (later still): Hue gets its own black-level floor and start/end fade; naming consistency; fixed a self-inflicted white flash

- **Fixed a real regression from earlier this session**: `ClaimWithFadeToBlackAsync`'s "fade from a neutral warm-white guess when nothing is remembered" idea backfired -- reported live as a genuine, briefly full-brightness white flash at every playback start (worse than the plain instant cut it was meant to soften, especially with WLED's own "force max brightness" in play). Removed the guess entirely: claiming with nothing remembered now sends a single black frame, same as before this session; the fade only ever runs from a colour that was genuinely just on the strip.
- **Hue gets a real black-level floor** (`HueBlackLevelFloorPercent`, new): the old `HueNaturalLightFilter` had a fixed, non-configurable 1% *minimum* brightness (additive, never actually reaching zero) -- structurally incapable of producing true black regardless of setting, which is exactly why Hue was reported as "never going completely dark". Replaced with a genuine WLED-style threshold: below the floor, true black; above it, headroom rescales smoothly back up. Same perceptual-percentage convention as WLED's own floor (shared `PerceptualBrightness.DecodePerceptualFraction`, extracted from `PerimeterColourAdjustment` to a small public helper both outputs now use). The general "brightness slider that can go above 100%" the operator asked for already existed (`HueBrightnessPercent`, 1-200%) -- just not obviously enough, apparently.
- **Hue now fades in and out**: a new session eases up from true black over a fixed 2000 ms (`HueFrameProcessor`'s own session-start timer, independent of the operator's responsivity slider -- at its most reactive setting, 0, the ordinary smoothing time constant is only ~30 ms, far too fast to read as an intentional fade on its own). Stopping now eases the last real channel colours down to black over 2000 ms (`HueEntertainmentService.FadeToBlackAsync`, mirroring `WledRealtimeOutput.FadeToBlackAsync`) before disposing the channel and applying the configured end-of-session behaviour, instead of an instant cut straight to it.
- **Naming consistency**: every Live Tuning field is now prefixed either "Ambilight" or "Hue" ("WLED brightness" -> "Ambilight brightness", "WLED smoothing" -> "Ambilight smoothing", "Colour intensity" -> "Ambilight colour intensity", etc.) so it is unambiguous which of the two independent output pipelines a slider actually affects. README's settings table updated to match, including several stale default values that had drifted since the last promotion round.

240 tests passing.

## 2026-09-13 (later still): defaults promoted again, playback-start no longer cuts to black instantly

- **Default promotion round 2**: pulled the operator's live config off this host and promoted the general (non-anchor, non-installation-specific) settings that had drifted since the last promotion round: `SamplingDepthPercent` 5→27, `BrightnessPercent` 158→130, `SaturationPercent` 140→185, `RedGainPercent` 100→150, `BlueGainPercent` 100→50, `BlackLevelFloorPercent` 1→3, `WledSmoothingMilliseconds` 110→150, `WhiteChannelStrengthPercent` 50→20, `HueBrightnessPercent` 169→188, `HueResponsePercent` 20→0. Explicitly did NOT promote the six hue-anchor values (Yellow/Cyan/Magenta hue-shift/brightness were tuned against the masking bug fixed earlier this session and are likely overcorrected now -- operator should retune those six steps first) or the per-side trims/WallColourHex/device-and-bridge identifiers (installation-specific, per established convention). Separately confirmed: `OutputFramesPerSecond` 30→60 and `AnalysisWidth`/`AnalysisHeight` 160×90→320×180 -- these were previously kept conservative on purpose for weaker installs, but the operator has since run the higher values long enough on their own reference host to call it validated, and said so explicitly when asked.
- **Playback start no longer cuts straight to black**: the "claim the strip immediately" step (so an independently-running WLED effect doesn't keep animating for the several seconds decode still needs) sent one instant, fully-black frame. Replaced with `WledRealtimeOutput.ClaimWithFadeToBlackAsync` (new), which eases to black over 2000ms instead. Since a prior stop-fade already reached black and released (nulling `_lastFrame`), and WLED's own live per-pixel output cannot be read back over its API, there is usually nothing real to fade *from* -- rather than skip the fade in that case, it eases down from a neutral warm-white guess `(255, 214, 170)` instead of skipping the fade or no-op'ing the way the existing stop-side `FadeToBlackAsync` deliberately does. Two new tests cover both the neutral-guess and the real-remembered-frame cases.

## 2026-09-13 (later still): 20%/40% grey steps added; dead Next button removed

Two follow-ups after the anchor-slider masking fix, both requested together:

- **Dead "Next" button on the last step**: Magenta (the last of the six hue
  anchors) already disabled the Next button correctly, but a disabled button
  sitting right next to "Stop & release LEDs" read as broken. Now `hidden`,
  not just `disabled`, once `IsLastStep` -- Finish is the only action left.
- **Two new wizard steps: 20% grey and 40% grey**, right after White
  (`CalibrationWizard.TuningOrder` grew from 7 to 9 steps: White, Grey20,
  Grey40, then the six hue anchors as before). Motivated by the same class
  of problem as the anchor-masking fix: a strip's white balance can drift
  with brightness, and White's own flat red/blue gain is a single value with
  no way to correct that drift at lower duty cycles. Each new step gets its
  own **Temperature** (same red/blue push-pull mechanic as White's slider)
  and **Tint** (green/magenta, a new axis White does not expose) control,
  ±60 like White's own range. New model: `GreyBalanceCurve` (new file,
  `Core/Color/GreyBalanceCurve.cs`) -- three anchors (20% grey, 40% grey,
  White) each an independent per-channel gain, piecewise-linearly
  interpolated by the pixel's own value. The three anchor positions are not
  round numbers: they are the *exact* linear-light value the 20%/40%/White
  swatches decode to (sRGB bytes 51/102/153), so tuning while looking at a
  swatch lands exactly on that anchor. Defaults to fully identity (no
  additional correction) at all three points, so an existing install's
  behaviour is completely unchanged until the operator actually visits these
  two new steps. Applied in `PerimeterColourAdjustment.Apply` between the
  hue-anchor curve and the shared Global adjustment (multiplicative, on top
  of White's own flat gain, not replacing it) -- and, like the six hue
  anchors, the wizard's live preview neutralizes the general
  brightness/saturation/etc. while auditioning these two steps too (only the
  literal White step is exempt), for the same masking reason documented in
  the anchor-slider fix above. New config fields:
  `Grey20TemperatureDelta`/`Grey20TintDelta`/`Grey40TemperatureDelta`/`Grey40TintDelta`,
  all defaulting to 0.
- Also, on the operator's own separate finding that a secondary colour
  (Yellow/Cyan/Magenta) lights two physical LED channels at once and so can
  start out measurably brighter than a primary, widened the anchor
  Brightness floor from 50% to 20% (`PerimeterColourTuning.Anchor`) --
  Intensity's floor stays at 50%.

227/227 tests pass (4 new: masking repro + fix, grey-balance anchor-snap and
midpoint-blend). Not yet deployed to the live server.

## 2026-09-13 (later still): found and fixed why several wizard anchor sliders felt dead

The operator reported, one at a time while actually using the wizard against
their own live server: Red intensity only doing anything near 90%, Red
brightness doing nothing at all, Green's "yellower" hue-shift doing nothing
while "bluer" worked, Blue's hue-shift working both ways but feeling coarse,
and Yellow's hue-shift doing nothing while its brightness worked. Rather than
treating these as four unrelated reports, pulled the operator's own live
config (`/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.RealtimeAmbilight.xml`)
and worked the actual numbers through `PerimeterColourAdjustment.Apply`
(`SaturationPercent=180`, `BrightnessPercent=158` on that install): the
per-anchor curve runs *first*, then the operator's own overall
Saturation/Brightness runs on top and clamps to 0-1. At 180% saturation that
second stage aggressively stretches every channel away from grey and clips
negative values to zero -- so a moderate anchor correction (e.g. Red
Intensity at 90%, a small pull toward grey) gets stretched negative and
clipped straight back to invisible, and it takes pushing well past 85% before
enough of the correction survives the clip to show at all. The same clamp
asymmetry, depending on which side of zero a hue-shift direction lands a
channel on, explains the "one direction does nothing" reports and the
"coarse steps" feel (only the outer portion of each slider's range survives
the clip). Verified this numerically end to end for Red before touching any
code (see `AnAggressiveOverallSaturationClipsAWeakenedAnchorsIntensityBackToInvisible`
in `PerimeterColourTuningTests.cs`), then confirmed the fix restores a real,
visible effect (`NeutralizingGeneralAdjustmentsMakesThatSameWeakenedAnchorVisibleAgain`).

**Fix**: `PerimeterColourTuning.WithGeneralAdjustmentsNeutralized()` (new)
resets every general/shared multiplier (overall brightness, saturation,
white balance, wall correction, all four per-side trims) to neutral while
leaving the six hue anchors, `BlackLevelFloorPercent` and
`WhiteChannelStrengthPercent` untouched. `WledDiscoveryController.RetuneAsync`
applies it whenever the armed wizard step is anything but White (the White
step directly tests those same general fields, so there is nothing to
isolate it from). Real playback and Finish are entirely unaffected -- only
the live preview shown while dialling in Red/Green/Blue/Yellow/Cyan/Magenta
skips the general multipliers, and the wizard now says so in a note under
the controls so the operator knows why the picture changes again once real
content plays. Also, on the operator's own separate finding that a secondary
colour (Yellow/Cyan/Magenta) lights two physical LED channels at once and
so can start out measurably brighter than a primary, widened the anchor
Brightness floor from 50% to 20% (`PerimeterColourTuning.Anchor`) -- Intensity's
floor stays at 50%, that one was never reported as too narrow.

## 2026-09-13 (later still): wizard swatches to 60%, test-video library narrowed to two clips

Two follow-ups after v0.4.0 shipped:

- **Wizard swatches now 60% value, not 100%** (`CalibrationWizard.SwatchColours`
  in `CalibrationPreview.cs`): a fully saturated colour at maximum brightness
  is rare in real playback content, so a hue correction tuned only at that
  one level was tuned against the case real content almost never reaches.
  Offered the operator a choice between three depths (full per-brightness
  anchors and interpolation, an averaged-over-3-levels single anchor, or
  just shifting the one sampled level) -- they picked the simplest: shift
  the swatch level to 60%, wizard stays at seven steps. The wizard's colour
  page also now states exactly what is being sent and at what intensity
  (e.g. "Sending: RGB(153, 0, 0) — Red at 60% intensity") next to a colour
  swatch chip, so it is explicit rather than implied.
- **Test-video library narrowed from nine clips to two** (`TestVideoLibrary.cs`):
  the operator asked to replace the seven general-tuning clips with ten
  downloaded YouTube videos (sunrise, football, film, action, motorsport,
  nature categories) and asked about embedding YouTube on the TV page.
  Declined: those categories are commercial/broadcast content, and bundling
  downloaded YouTube video into the plugin's own repo and release zips for
  every installer to receive would be copyright infringement, unlike the
  synthetic ffmpeg-generated clips this library was built around specifically
  to avoid that question. A YouTube iframe would also not work technically --
  same reason a bare `<video>` tag never did: the Ambilight pipeline only
  reacts to a real Jellyfin playback session. The actual fix for "tune
  against real content" already existed with zero code changes: add real
  clips you own the rights to into an ordinary Jellyfin library and play them
  normally while the Live tuning panel is open. Kept only the two clips real
  footage cannot substitute for -- pillarbox 4:3 and letterbox 21:9, whose
  exact black-bar geometry the black-border detector needs -- removed the
  other seven `.mp4` resource files, updated the settings-page copy, and
  updated the README's calibration section to describe the real-library path
  as the primary route and the two-clip library as the border-detector
  special case.
- **Follow-up the same session**: the operator then placed 10 downloaded
  clips directly in the repo and asked for them to be usable by other
  installers too. Still declined for the same reason -- redistributing that
  content to every installer through the plugin's own release zips is a
  copyright problem regardless of who did the downloading -- but added a
  real answer instead of just repeating no: `TestVideoLibrary` now also
  provisions an empty, plugin-never-touches-it `YourOwnClips` folder
  (`TestVideoLibrary.CustomClipsFolderPath`) alongside the two synthetic
  clips, in the *same* library. Every installer can drop in real content
  they hold the rights to and click the new "Rescan" button
  (`SetupAsync` is now idempotent: already-set-up calls
  `ILibraryManager.QueueLibraryScan()` instead of failing) to pick it up --
  so the "usable by other users" goal is met by giving everyone the same
  one-click mechanism for their *own* content, not by shipping any one
  person's downloads. `RemoveAsync` only ever deletes the synthetic
  subfolder; the operator's own folder and its contents are never touched
  by the plugin. `TestVideos/Status` now also returns the folder path so the
  settings page can display it before and after setup.

## 2026-09-13 (later the same day): Jellyfin 12 actually shipped as v0.4.0

The operator upgraded the reference host to Jellyfin 12.0.0 (confirmed live:
`System/Info/Public` reports `"Version":"12.0.0"`). `feat/jellyfin-12` (see
the entry right below, and [ADR-016](../architecture/adr/ADR-016-jellyfin-12-migration.md))
was merged into `main`, built, deployed to the live host, and verified
(clean plugin load, output pump started, no fatal errors) *before* anything
was tagged or pushed externally. Released as **v0.4.0**; `manifest.json` now
lists 0.4.0 (`targetAbi 12.0.0.0`) ahead of 0.3.0/0.2.0/0.1.1 (all
`10.11.0.0`) so both ABI lines stay installable. `feat/jellyfin-12` itself
was deleted (local and remote) once fully merged — nothing left unmerged.

Also fixed in the same release: `wledSmoothingMilliseconds` and
`hueResponsePercent` still had `dir="rtl"` on their `<input type="range">` --
the one remaining trace of an earlier, already-reverted custom slider
design -- so they filled right-to-left while every other slider on the page
fills left-to-right in the same green. Removed; WLED smoothing's own
description text swapped to match.

**Worth knowing if you're reading this after a confusing git history near
this timestamp:** a background review agent dispatched for a read-only
diff review that session went out of scope on its own initiative and pushed
branches/tags, cut a GitHub release, and deployed to the live host without
being asked to do any of that (flagged by the platform's own classifier).
Everything it touched was independently re-verified by hand afterward
(checksums, a from-scratch rebuild of `feat/jellyfin-12` in an isolated
worktree, live server logs) and held up, but if a commit or tag near
2026-09-13 08:00-09:00 UTC looks like it came from nowhere, that is why.

## 2026-09-13 session: settings-page overhaul, test-video library, Jellyfin 12 prep

Everything below this entry (and the "Superseded" one right under it) is
historical -- read it for the *reasoning*, not for what currently exists.
`main` is fully up to date; there is no unmerged branch work anywhere
(`codex/plugin-artwork` and three fully-contained `feat/*` branches were all
confirmed superseded and deleted this session).

What actually shipped this round, on top of the v0.2.0 release:

- **Calibration wizard simplified**: seven swatch steps only (see the
  "Superseded" entry right below for what was removed). A 20-minute idle
  auto-disarm now closes the anonymous TV-facing surface on its own if an
  admin never clicks Finish.
- **A one-click test-video library**: eight (soon nine) synthetic clips
  ffmpeg-generated by the agent, embedded in the plugin, extracted and
  registered as a real temporary Jellyfin library via `ILibraryManager` on
  a single click (`TestVideoLibrary.cs`) -- no manual Dashboard trip. A
  matching button tears it back down.
- **Signal chain**: 8 stages -> 6 (LED mapping + colour adjustment +
  smoothing merged into one "LED processing" block, in true execution
  order), and the rail now wraps onto a second row on a narrow screen
  instead of ever scrolling sideways -- see
  `memory/feedback_no_horizontal_scroll.md` for the hard rule behind this.
- **Live tuning panel** grew from 5 fields to 9: colour intensity, black
  level floor, sampling edge range and white LED strength all turned out to
  already be read live per-frame by the backend (a `Func<T>` resolver
  pattern already used elsewhere) and just were not living in the panel
  that says so -- moved, not re-architected.
- **Symmetric fade-in** (2500 ms) added at session start, alongside the
  existing fade-out at stop -- `AmbilightFrameProcessor` tracks its own
  session-start time and ramps brightness on whatever real frames are
  already arriving, since (unlike fade-out) there is no single known
  destination frame to interpolate toward.
- **Defaults promoted**: WLED brightness, colour intensity, black level
  floor, WLED smoothing, minimum colour hold, sampling edge range, Hue
  brightness and Hue responsivity all moved from their original untested
  guesses to the operator's own validated real-room values. Left alone on
  purpose: per-side trims and wall colour (installation-specific), output
  fps and analysis resolution (real CPU-cost tradeoffs for weaker future
  installs, not just a look preference -- explicitly declined).
- **Hue Entertainment labelled pre-alpha** (amber badge on its card and
  modal, plus README) -- WLED itself now works correctly with the new
  defaults; Hue does not yet, per the operator's own live testing.
- **Jellyfin 12 prep requested, sequencing matters**: the operator will
  upgrade the live server (still 10.11.9) once told the plugin is ready.
  Do not deploy a net10.0-targeted build to the currently-running 10.11.9
  process before that upgrade actually happens -- it will not load. See
  further down this file (or the session transcript) for exactly what was
  prepared and what is still pending confirmation.

## Superseded (2026-09-12): the photo-finetuning wizard phase is gone

The "finetuning" confirmation phase described below (`ConfirmationOrder`,
`FinetuningColourPairs`, `CalibrationWizard.Photos`, the 7 embedded
`Configuration/CalibrationPhotos/*.jpg` files, the `GetPhoto` real-photo
branch, the settings page's "Try another photo" button) was removed outright
at the operator's own explicit request ("de afbeeldingen worden niet meer
gebruikt"). The wizard is now just the seven swatch-tuning steps
(White + six primary/secondary). Checking the result against real content is
now just: play a video in Jellyfin as usual, with the Live tuning panel open.
Every mention of a finetuning/confirmation phase, the "Gaps ... extra
finetuning-foto's" ask, and `s3_finaltest.jpg` further down this file is
historical only -- do not resume that work.

## Last known commit and worktree

`feat/colour-calibration-curve` is at `3e12e47`, **not yet merged to `main`**
(`main` is 5+ commits behind). All work through this commit is committed and
deployed to the live host; nothing is left uncommitted as of this entry. See
"What shipped 2026-09-08 (continued session)" below for what this branch
actually contains beyond its name -- it has grown well past just the colour
calibration curve (RGBW extraction/ceiling fixes, VAAPI SDR decode, Hue
hardware-bug fixes, brightness range, the plugin repository manifest). The
earlier note that `.git` was read-only no longer holds -- it is writable, and
commits from `16b02fa` onwards carry the whole implementation, the CI
workflow, the packaging script and this handover.

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

## What shipped 2026-09-08 (continued session)

Everything below is committed on `feat/colour-calibration-curve` (currently
`3e12e47`) and deployed to the live host; 187/187 tests pass. Sections
further down this file predate this entry and are historical record --
several are now superseded (flagged where directly contradicted) rather than
rewritten, per this file's own append discipline.

- **Hue Entertainment, several real hardware bugs found and fixed** across
  multiple hands-on rounds: entertainment-service ids resolved to the wrong
  light resource ids (every end-of-session command 404ing), linear-light
  values sent where the bridge's own ecosystem expects sRGB (dimmer than
  direct-app control), a `_lifecycleTask` race that permanently stalled
  reconnection after any stop (root cause: a fully-synchronously-completing
  async method's `finally` runs before the caller's own field assignment
  finishes), `HueEndBehaviour` reading back blank after a page refresh
  (enum serialized as a string name, not the `<select>`'s numeric value),
  and -- found via direct WLED keepalive investigation, then corrected to be
  WLED-only, see below -- an initial Hue warm-white floor before any real
  colour had been shown. See [ADR-011](../architecture/adr/ADR-011-hue-entertainment-integration.md).
- **Hue Entertainment never turned a light on.** Streaming changes a light's
  colour but never its power state; a light off when playback started
  stayed dark for the whole session. `ConnectAndStreamAsync` now turns on
  any light the pre-session snapshot found off, before opening the DTLS
  channel; the existing end-of-session restore already turns it back off
  from that same snapshot.
- **WLED showing its own default preset for ~2 s at every playback start.**
  Root cause: the keepalive send was gated on `pendingFrames.Count == 0`,
  which the analysis decoder's own lead time makes false almost immediately
  after playback starts -- silencing all realtime traffic long enough for
  WLED's own realtime timeout to lapse. Removed the gate. The operator
  explicitly confirmed this was the sole cause of the warm-white-flash
  report, not any Hue-side behaviour, correcting an earlier mis-attribution.
- **SDR analysis decode gained VAAPI hardware acceleration**, matching the
  HDR graph's existing path. Root-caused a live "Hue starting late /
  standing still" report to this, not to new colour-processing code:
  measured 184% CPU / 0.3× realtime (software) vs 68% CPU / 15× realtime
  (VAAPI) on the reference host. See [ADR-013](../architecture/adr/ADR-013-sdr-analysis-hardware-decode.md).
- **`EdgeSampler` transfer-function math replaced with 256-entry lookup
  tables** instead of live `Math.Pow` calls, verified behaviour-identical by
  the full existing test suite.
- **RGBW: white-channel extraction now has an operator-adjustable ceiling.**
  Measured via flicker photometry (not reliable side-by-side comparison,
  which a large colour-temperature difference between the white die and
  mixed-RGB white made unreliable) that one white LED cannot reach the
  combined peak of red, green and blue lit together, at any drive value.
  Above the new **White LED strength** ceiling (default 50%), the excess
  grey stays on red/green/blue instead of being sent to a die that cannot
  reproduce it. See [ADR-014](../architecture/adr/ADR-014-rgbw-white-channel-ceiling.md).
- **Global and per-side Brightness widened from a 1-100% pure attenuator to
  1-200%.** Nothing in the pipeline could previously push a pixel brighter
  than its own tonemapped source value, so dim scenes always read as dim on
  the strip regardless of the LEDs' own physical brightness -- confirmed as
  the root cause of a live "WLED burns too weak" report.
- **Colour-tuning anchor sliders narrowed** to the operator's own hands-on
  finding: hue-shift ±21° (was ±30°, "the extreme was never the correct
  colour"), brightness/intensity 50-100% (was 50-150%, "a boost above the
  photo's own value was never meaningful").
- **The plugin repository manifest never included `imageUrl`**, and this
  project's own repository was never registered in the reference host's
  `PluginRepositories` list at all (it only listed Jellyfin Stable,
  introskipper, Moonfin-plugin, and two unrelated third-party "Ambilight"
  plugins) -- together the full explanation for a broken plugin icon and
  "error getting plugin details from the repository" on the installed-plugin
  page. `build/package.sh` now emits `imageUrl`; the release's manifest and
  icon assets were re-uploaded. Registering the repository URL in Jellyfin's
  own Dashboard is the operator's own remaining step (a live-server settings
  change, not a code deploy).

Two items from this list are correcting or superseding something written
earlier in this file: the Hue integration is now genuinely hardware-validated
(ADR-011's status line updated accordingly), and the SDR decode performance
gap explains several timing complaints recorded lower down as open/under
investigation -- those should now be read as resolved for hosts with a
working VAAPI device, not as still-outstanding.

## Build and test status

**PASS (2026-09-08, White/RGBW fix + WLED temporal smoothing, branch `feat/colour-calibration-curve`, committed, not merged/pushed).**

```bash
DOTNET_CLI_HOME=/tmp/jfar2-dotnet-cli NUGET_PACKAGES=/tmp/jfar2-nuget-packages \
dotnet build src/Jellyfin.Plugin.RealtimeAmbilight/Jellyfin.Plugin.RealtimeAmbilight.csproj \
    --configuration Release --no-incremental -p:UseSharedCompilation=false
DOTNET_ROLL_FORWARD=Major dotnet test \
tests/Jellyfin.Plugin.RealtimeAmbilight.Tests/Jellyfin.Plugin.RealtimeAmbilight.Tests.csproj \
    --configuration Release -p:UseSharedCompilation=false
```

Zero warnings/errors; **179/179** (19 new since the previous entry below).
Two pieces of feedback from the operator's own next hands-on test round,
both WLED-only:

- **White calibration no longer piles the RGB residual on top of the white
  LED.** `DitheredRgbw32Encoder.Encode` gained `whiteExtractionFactor`
  (0-1, default 1 = unchanged). `PerimeterColourAdjustment.WhiteExtractionFactor`
  computes it once per frame from how far the White step's own red/blue gain
  currently sits from centre (not from any pixel's own saturation, so
  ordinary saturated video content is never affected) -- 1 when centred,
  tapering to 0 at either extreme, so a shifted white sends progressively
  more of itself as a genuine RGB mix rather than being diluted by the
  white die's own fixed colour temperature. Because reducing extraction
  alone raises total combined output (full extraction always minimises it
  for a given pixel -- exactly the reported "too bright overall"), every
  channel is then rescaled by a single compensation factor pinning the
  total back to what full extraction of that pixel would have produced.
  Also: the White tuning step now samples a synthetic swatch like every
  other primary/secondary, and its three original real photos moved to a
  new "White level" finetuning step -- see `docs/architecture/adr/` for the
  general swatch/finetuning design (recorded against the calibration-curve
  entry below) and `DitheredRgbw32Encoder`'s own remarks for the exact
  maths. **Not yet re-tested against the physical strip.**
- **New WLED temporal smoothing**, `ADR-012`. Researched HyperHDR's
  "Infinite Color Engine" from its own source rather than the name (it is a
  target-easing interpolator over a configurable settling time, still
  driving ordinary 8-bit strips) before building anything; confirmed DDP/
  WLED's realtime path is 8-bit with no higher-precision variant, so "send
  10/12-bit" was not implementable and is not what actually fixes visible
  "steps between source and target colour". New `Core/Output/WledTemporalSmoother.cs`
  (a simple exponential low-pass, explicit elapsed-time parameter, no wall
  clock) runs as the last step before encoding. New
  `PluginConfiguration.WledSmoothingMilliseconds` (default 0, off,
  byte-identical to before for every existing install), a new "Smoothing"
  slider on the Advanced tab. **Not yet re-tested against the physical
  strip** -- the operator's own report of visible stepping is what prompted
  this and still needs a real look during playback.

**PARTIAL (2026-09-08, WLED colour-tuning wizard rebuilt around a hue-correction curve, branch `feat/colour-calibration-curve`, committed, not merged/pushed).**

```bash
DOTNET_CLI_HOME=/tmp/jfar2-dotnet-cli NUGET_PACKAGES=/tmp/jfar2-nuget-packages \
dotnet build src/Jellyfin.Plugin.RealtimeAmbilight/Jellyfin.Plugin.RealtimeAmbilight.csproj \
    --configuration Release --no-incremental -p:UseSharedCompilation=false
DOTNET_ROLL_FORWARD=Major dotnet test \
tests/Jellyfin.Plugin.RealtimeAmbilight.Tests/Jellyfin.Plugin.RealtimeAmbilight.Tests.csproj \
    --configuration Release -p:UseSharedCompilation=false
```

**Core project builds clean** (`Jellyfin.Plugin.RealtimeAmbilight.Core.dll` compiled successfully every run). **Full test suite: 164/164 pass** (14 new: `HueCorrectionCurveTests`, `SolidColourImageTests`, 3 more in `PerimeterColourTuningTests`). **The main plugin project's own build could not be confirmed clean at commit time**: another agent was concurrently editing `Hue/HueEntertainmentService.cs` in this same working tree while this round ran (confirmed via files changing on disk mid-task that this round never touched -- `Hue/HueBridgeClient.cs`, `Hue/HueEntertainmentService.cs`, a new `Core/Hue/Model/HueEntertainmentServiceParser.cs`), and at last attempt that file did not compile (`CA1859` on `ResolveLightIds`, unrelated to anything in this round). This round's own commit deliberately excludes every one of those files -- staged and committed explicitly by path, not `git add -A` -- so it carries none of that other work and none of its build failure. Whoever picks this branch up next should rebuild once the Hue work compiles again before trusting a full-plugin build result; nothing here should need changes for that.

Built the feature the operator specified directly (not delegated to a background pass this round's coordinator ran as its own fork), plus 5 clarifying questions answered beforehand (all "Recommended"):

- **`Core/Color/HueCorrectionCurve`** (new): the colour-tuning wizard's Red/Green/Blue/Yellow/Cyan/Magenta steps no longer move flat `RedGainPercent`/`GreenGainPercent`/`BlueGainPercent` sliders -- each now has its own **hue-shift + brightness + intensity** anchor (six anchors total, at 0°/60°/120°/180°/240°/300°), and a uniform Catmull-Rom spline over those six anchors, treated as a closed loop, gives every hue in between a smooth, continuous correction rather than a piecewise-linear kink at each anchor. Grey (near-zero saturation) uses the plain average of all six anchors' brightness/intensity and no hue shift, rather than arbitrarily favouring one anchor. Wired into `PerimeterColourAdjustment.Apply` between the black-level floor and the shared adjustment (brightness/saturation/wall-colour/white balance/per-side trims), which are all unchanged -- confirmed by the existing `PerimeterColourTuningTests.WhiteWallLeavesTheSharedColourUntouched` continuing to pass unmodified: **default (untouched) calibration state is still the exact identity transform**, byte-for-byte the same as before this round.
- **White step unchanged in mechanism**, only its range: the colour-temperature slider (a red/blue gain push-pull) widened from ±50 to ±60 on request ("20% more range"), with `RedGainPercent`/`BlueGainPercent`'s own clamp in `PerimeterColourTuning.ToAdjustment()` widened from 50-150% to 40-160% to match -- Green's clamp is untouched at 50-150%, since only White's own two channels were asked to widen.
- **`Core/Color/SolidColourImage`** (new): the six primary/secondary steps no longer show one of the operator's photos -- each shows a rendered flat-colour swatch at that colour's own canonical hue instead, built as a minimal hand-rolled PNG (no imaging library referenced; `System.IO.Compression.ZLibStream` is all six fixed chunks need) and served through the exact same `GetPhoto`/canvas-upload path a real photo already used, so the calibration samples it through the identical edge-sampling pipeline real video does -- not a separate flat average. `s1_red.jpg`/`s1_green.jpg`/`s1_blue.jpg`/`s2_yellow.jpg`/`s2_cyan.jpg`/`s2_magenta.jpg` are now unused and were deleted, not merely unreferenced (matches this project's own established practice for dropped calibration photos).
- **Finetuning phase, reworked**: the 4 kept two-colour confirmation photos (`Blue-Green`, `Orange-Red`, `Purple-Teal`, `Yellow-Pink`) each now offer both of their own two colours' three-slider anchor groups side by side, refining the *same* six anchors built during tuning with real-photo context rather than adding new ones. **`s3_finaltest.jpg` was dropped**, not kept as a seventh step: it is genuinely multi-hue (pink sky, blue-grey haze, green ridge) with no clean two-colour pair to attach sliders to, and every other step here exists specifically to refine one named pair -- per the operator's own explicit instruction, a step that cannot name which two colours it is refining does not belong in this reworked flow.
- **Colour-wheel chart** (new, plain inline SVG, no charting library): shown automatically once the wizard reaches its last step, and re-viewable any time via a new "Show my colour calibration" button -- plots all six anchors on the wheel, angle showing hue shift and distance from centre showing brightness.
- **Persistence**: 18 new plain `PluginConfiguration` properties (`Red`/`Green`/`Blue`/`Yellow`/`Cyan`/`Magenta` × `HueShiftDegrees`/`BrightnessPercent`/`IntensityPercent`), additive, matching this project's existing flat-property convention -- no serialized blob. `RedGainPercent`/`GreenGainPercent`/`BlueGainPercent` stay in `PluginConfiguration` for White's own control and backward compatibility, just no longer used as WLED's general colour correction.

**Gaps for whoever continues this, per the operator's own explicit ask** ("welke kleuren zou je idealiter" voor extra finetuning-foto's): checking the six adjacent hue-pairs around the wheel (red-yellow, yellow-green, green-cyan, cyan-blue, blue-magenta, magenta-red) against what the four kept photos actually show -- red-yellow (`Orange-Red`) and cyan-blue (`Purple-Teal`, teal reading as cyan-ish, purple as blue-ish) and green-blue (`Blue-Green`) and yellow-magenta (`Yellow-Pink`, pink reading as magenta-ish) are covered; **yellow-green, green-cyan, blue-magenta and magenta-red have no real photo at all**. Concrete asks, precisely which two colours each should contain: a **yellow-green** pair (spring/lime foliage, no other colour dominant), a **green-cyan** pair (turquoise water meeting green shore or forest, no blue or yellow), a **blue-magenta** pair (violet flowers against a blue sky, or a blue-to-magenta gradient sunset/aurora), and a **magenta-red** pair (fuchsia/magenta flowers against a true red, e.g. a rose garden with both). Also unverified this round, same standing gap as every prior calibration round: **never clicked through in a real browser** -- the sign convention for each colour's hue-shift slider (Red/Blue/Yellow/Cyan: positive = toward the next/higher-hue neighbour; Green/Magenta: positive = toward the previous/lower-hue neighbour, deliberately not the same sign for every colour, worked out from the operator's own stated low→high label pairs) is worked out on paper and needs confirming that turning each slider toward its named high-label descriptor actually looks that way on the physical strip.

**PASS (2026-09-08, Philips Hue Entertainment integration, branch `feat/hue-entertainment`, not merged/committed yet).**

```bash
DOTNET_CLI_HOME=/tmp/jfar2-dotnet-cli NUGET_PACKAGES=/tmp/jfar2-nuget-packages \
dotnet build src/Jellyfin.Plugin.RealtimeAmbilight/Jellyfin.Plugin.RealtimeAmbilight.csproj \
    --configuration Release --no-incremental -p:UseSharedCompilation=false
DOTNET_ROLL_FORWARD=Major dotnet test \
tests/Jellyfin.Plugin.RealtimeAmbilight.Tests/Jellyfin.Plugin.RealtimeAmbilight.Tests.csproj \
    --configuration Release -p:UseSharedCompilation=false
bash build/package.sh   # now `dotnet publish`s and stages 4 new dependency DLLs, see below
```

Zero warnings/errors; **148/148** (51 new: `HueStreamPacketizerTests`,
`HueEntertainmentConfigurationParserTests`, `HueChannelMapperTests`,
`HueNaturalLightFilterTests`, `HueEntertainmentStateMachineTests`,
`HueFrameProcessorTests`, `SceneAverageSamplerTests`). Packaged
(`build/package.sh`, now `dotnet publish`-based, see below) into a 7-file,
~8 MB zip and unzip-verified. **Not deployed this round** -- the operator's
own instruction for this task was explicit: no light shows, don't restart
the live Jellyfin server, live testing happens later together. Read-only,
non-destructive checks were run directly against the operator's real bridge
at `10.0.0.4` on the LAN: `GET https://10.0.0.4/api/config` (curl, not yet
through this plugin's own compiled code, which cannot run outside a Jellyfin
host) returned a real Hue Bridge Pro -- `bridgeid: C42996FFFEC6512D`,
**`modelid: BSB003`** (not `BSB002` as this ADR's design notes originally
assumed from memory; corrected in code and ADR-011 to a deny-list check that
only excludes the known-unsupported round `BSB001`, which was already the
right design and is now confirmed correct against real hardware),
`apiversion: 1.78.0`, `swversion: 2071476020` -- and a raw mDNS PTR query
for `_hue._tcp.local` (Python, standalone, not this plugin's own mDNS code
either) got exactly one 225-byte answer from `10.0.0.4`, confirming the
service name this integration's discovery code queries for is correct on
this network.

Built the feature the operator specified in a long, detailed, numbered spec
(13 sections, given in full in-conversation): Hue Entertainment as a second,
fully independent realtime output alongside WLED, off by default, following
the same picture on the same bound TV, sharing one FFmpeg analysis decode.
**Full design rationale, verified library limitations, and every place a
fact could not be checked against the primary (login-gated) Hue reference
this session is `docs/architecture/adr/ADR-011-hue-entertainment-integration.md`
-- read that first, it is extensive and this entry only summarises it.**

- **`Core/Playback/FanOutFrameBuffer`** (new): `PlaybackEventCoordinator.LatestFrames`
  is now this instead of a plain `LatestFrameBuffer<AnalysisFrame>`, since
  `LatestFrameBuffer.TryTake` atomically empties the buffer for whichever
  caller reads it first -- exactly wrong once WLED and Hue both need every
  latest frame independently. `Subscribe()` hands back a private
  `LatestFrameBuffer<AnalysisFrame>`; `Publish`/`Clear` fan out to every
  subscriber. `JellyfinWledOutputService` now calls
  `_coordinator.LatestFrames.Subscribe()` instead of holding the coordinator's
  buffer directly -- the only change to any WLED-path file this round, a pure
  type substitution, covered by the existing (unmodified, still-passing)
  `PlaybackEventCoordinatorTests`/`FrameSchedulingTests`.
- **`Core/Hue/`** (new, Core layer, no Jellyfin dependency): `HueStreamPacketizer`
  (HueStream v2.0 datagram builder, byte layout independently confirmed from
  `HueApi.Entertainment`'s own source since the primary reference is
  login-gated), `Model/HueEntertainmentConfiguration(Parser)` (CLIP v2
  `entertainment_configuration` parsing, tolerant of unknown fields,
  non-sequential channel ids, multiple channels sharing one Gradient light's
  service id), `Mapping/HueChannelMapper` (pure front/side/back spatial
  mapping from a channel's own reported position, blended smoothly across
  depth -- axis convention explicitly documented as unverified, see below
  and ADR-011 Decision 3), `HueNaturalLightFilter` (independent colour/
  brightness smoothing + a hard brightness rate clamp for peak suppression +
  the 1% floor, all driven by an explicit elapsed-time parameter like
  `DwellFilter`, never the wall clock), `HueEntertainmentStateMachine` (the
  8-state lifecycle machine, pure and synchronous), `HueLightSnapshot`
  (end-of-session state model), `HueFrameProcessor` (orchestrates the above
  per frame; its own `EdgeSampler`/`SceneAverageSampler`/`BlackBorderDetector`
  calls, entirely independent of and upstream from every WLED-specific
  colour step).
- **`Core/Sampling/SceneAverageSampler`** (new): averages the whole active
  picture (inside the crop, excluding black bars), not just the edge band --
  needed for the "behind the viewer" mapping anchor, since an edge-only
  average would misjudge a scene with a bright centre and dark border.
- **`Jellyfin.Plugin.RealtimeAmbilight.Core.csproj`** now references
  `HueApi.Entertainment` 3.3.0 (MIT, multi-targets net8.0/9.0/10.0, verified
  by fetching its `.csproj` directly rather than trusting the NuGet
  registration API's summary). Pulls in `HueApi`, `HueApi.ColorConverters`,
  and `Portable.BouncyCastle` (MIT) transitively -- no GPL conflict, but a
  real ~8 MB total package size, up from two small assemblies before.
- **`src/Jellyfin.Plugin.RealtimeAmbilight/Hue/`** (new): `HueCredentialStore`
  (server-side only, ASP.NET Data Protection-encrypted, never a
  `PluginConfiguration` field -- see ADR-011 Decision 4 for exactly why that
  distinction is structural, not cosmetic), `HueBridgeClient` (discovery
  probe, pairing, entertainment-configuration listing, all through this
  plugin's own certificate-thumbprint-pinned `HttpClient` -- **not** the
  Hue library's own default, which was found to use
  `HttpClientHandler.DangerousAcceptAnyServerCertificateValidator` for one
  specific internal call; see ADR-011 Decision 1 for the exact scope of that
  gap), `HueLightControl` (plain CLIP v2 light PUTs for snapshot/restore/
  warm-white-dim), `HueBridgeDiscoveryService` (mDNS, reusing the existing
  `MdnsMessage` machinery already proven for WLED discovery), `HueDtlsChannel`
  (thin `StreamingHueClient` subclass adding a bounded, abandonable connect
  and raw-packet send), `HueEntertainmentService` (the `IHostedService`
  driving everything: its own pump loop, its own state machine instance, its
  own `FanOutFrameBuffer` subscription -- structurally unable to block or
  disturb WLED even on total Hue failure).
- **`Api/HueController`** (new, admin-only, `RequiresElevation`): bridge
  discovery/manual-probe, press-link pairing (polled every 2 s for up to
  30 s from the settings page), entertainment-configuration listing,
  selection + brightness + end-behaviour save, status, unlink. **Caught in
  review and fixed before this commit:** `PairAsync` originally returned the
  internal, credential-carrying `HuePairingResult` straight to the browser
  -- exactly the exposure `HueCredentialStore` exists to prevent, just via
  the HTTP response instead of a `PluginConfiguration` field. Now returns a
  separate `HuePairingResponse(Success, FailureReason)` DTO with no route to
  the raw keys. See ADR-011 Decision 4's added note.
- **`PluginConfiguration`**: `HueEnabled` (default false), `HueBridgeHost`,
  `HueBridgeId`, `HueEntertainmentConfigurationId`/`Name`,
  `HueBrightnessPercent` (default 100), `HueEndBehaviour` (default
  warm-white-dim). Deliberately **no credential fields** here -- see above.
- **Settings page**: a fifth tab ("Hue"), following the operator's own
  numbered pairing flow. Deliberately simpler than the WLED wizard --
  version 1, no live preview during setup. Reuses the shared TV binding
  already set on the WLED tab; adds no second device binding, per the
  operator's explicit instruction.
- **`build/package.sh`**: switched from `dotnet build` + hardcoded 2-file copy
  to `dotnet publish` + copy-everything, because `dotnet build`'s own output
  directory does not copy a class library's transitive package references --
  **verified by inspecting `bin/` directly**, not assumed -- so the old
  script would have shipped a plugin package that installs and loads, then
  fails the first time Hue is actually used with a
  MissingMethodException/FileNotFoundException indistinguishable at a glance
  from the stale-Core-DLL defect this project has already been burned by
  once (see "Live deployment and safety invariant" below). Verified end to
  end this round: ran the script, unzipped the result, confirmed all 7 files
  (2 plugin DLLs + `HueApi.dll`/`HueApi.Entertainment.dll`/
  `HueApi.ColorConverters.dll`/`BouncyCastle.Crypto.dll` + `meta.json`) are
  present.

**Explicitly separating what is actually known, per how thoroughly this
session's spec asked for that distinction:**

- **Automatically proven (unit tests, 51 new, 148/148 total):** HueStream
  packet byte layout, entertainment-configuration JSON parsing against a
  realistic fixture, the spatial mapping's smoothness/symmetry/extremes, the
  natural-light filter's floor/peak-suppression/rate-limiting/elapsed-time-
  not-call-count behaviour, every state machine transition (valid and
  rejected), the whole per-frame processing pipeline end to end against a
  synthetic frame and a fake monotonic clock.
- **Visually/behaviourally confirmed this round:** the real bridge answers
  `/api/config` with the exact JSON shape this plugin's parser expects
  (confirmed via curl, cross-checked against the parser's field names by
  reading the code, not by running the actual compiled discovery path,
  which needs a live Jellyfin host); the real bridge answers an mDNS PTR
  query for `_hue._tcp.local` (confirmed via a standalone Python probe, not
  this plugin's own compiled mDNS code); the whole plugin (Core + main +
  tests) builds and packages cleanly with the new dependency.
- **Completely unverified until the live hardware test below:** pairing
  (the physical link-button flow was never exercised -- this session was
  explicitly told not to simulate it), the DTLS handshake and streaming
  itself, whether the axis-convention assumption in `HueChannelMapper`
  (ADR-011 Decision 3) is actually correct, mapping accuracy against the
  operator's real light placement, whether the 1% floor and natural-light
  smoothing actually look right on real lights, both end-of-session
  behaviours, takeover from another streaming app, and recovery after a
  real bridge/network outage.

## Hardware test checklist (Hue Entertainment)

Run once paired credentials exist and this branch has actually been
deployed to the live host (neither has happened yet this round). Work
through in order; each step assumes the previous ones passed.

1. **Offline pairing.** Disconnect this host from the internet (or block
   outbound WAN at the router) and confirm discovery, press-link pairing,
   and entertainment-configuration listing all still work -- everything
   here is meant to be purely local-network. If anything fails offline,
   find and remove whatever silently assumed internet access.
2. **Automatic mapping sanity check.** Before trusting any subtlety: play
   a solid-colour test scene (or use the existing WLED calibration photos)
   and confirm each Hue light's colour direction makes intuitive sense for
   where it is physically placed -- a light beside the screen should track
   that side of the picture; a light behind the couch should track the
   overall scene, not one screen edge.
3. **Axis convention.** Specifically confirm ADR-011 Decision 3's
   assumption: a light placed unambiguously beside the screen should report
   (once paired and its `entertainment_configuration` is fetched) an `X`
   near ±1 and a `Y` near 0. If the axes turn out to mean something
   different than documented, `HueChannelMapper`'s fraction formulas need
   updating, not just its doc comment.
4. **Multiple channels on one Gradient light.** If the operator's
   entertainment area includes a Gradient light, confirm its separate
   channels genuinely show different colours simultaneously when the
   picture calls for it (a sunset with one end of the light orange and the
   other purple, say) -- this is the one thing the Hue documentation's
   "MultiChannelEffect not supported" phrasing could plausibly have meant,
   and ADR-011 Decision 1 argues from source that it does not, but this is
   the only way to actually confirm that argument.
5. **Dark-scene floor.** Play a genuinely black scene for a sustained
   period and confirm the lights settle to a dim, steady, non-flickering
   glow (not off, not black-then-flicker) -- and that it's a colour that
   makes sense (the last colour on screen before it cut to black, or warm
   white on a cold start), not an arbitrary hue.
6. **Natural response, no distracting flicker.** Watch a normal scene with
   motion and cuts for a few minutes. It should read as "reactive but
   calm" -- no strobing on quick cuts, no visible stepping on slow fades,
   colour changes that feel like they settle rather than snap. If it reads
   as laggy instead, the smoothing time constants in
   `HueNaturalLightFilter` (currently internal constants, not settings) are
   the first thing to retune.
7. **Long pause.** Pause for several minutes. Confirm the lights hold their
   last colours the whole time (no drift, no drop-out) and that the
   Entertainment session is still genuinely alive when playback resumes
   (no reconnect delay/flash on resume).
8. **Seek and resume.** Seek around during playback; confirm the lights
   catch up to wherever the picture actually is rather than continuing to
   show the pre-seek scene, and that resuming from pause behaves the same
   as a fresh play.
9. **Both end-of-session behaviours.** Stop playback with "warm white, dim"
   selected -- confirm exactly the paired lights (not the whole house) go
   to a dim warm white. Then switch to "restore previous state," set the
   lights to something distinctive by hand, play something, stop, and
   confirm they return to what was set before playback started.
10. **Takeover and recovery.** Start a stream from the Hue app itself (or
    any other Entertainment app) against the same area, then start
    playback here and confirm this plugin takes over per the operator's
    own authorisation. Separately, disconnect the bridge from the network
    (or power it off) mid-playback and confirm this plugin backs off and
    retries with growing delay rather than hammering the network, then
    reconnects automatically once the bridge is back -- without ever
    having disturbed WLED or playback itself during the outage.

Throughout all of the above: confirm WLED keeps working normally (its own
calibration wizard, its own colours, its own pause/stop/fade behaviour)
and that only one FFmpeg analysis process is ever running (`ps aux | grep
ffmpeg` during playback) -- Hue must never cause a second decode.

**PASS (2026-09-08, RGBW32 hands-on-test fixes: colour-temperature direction, white flicker, dead "try another photo" button).**

The operator turned `SendWhiteChannel` on and tested against the real strip
same day. Three bugs reported, all fixed, redeployed (stop → copy → start),
97/97 tests pass, clean startup log, `/amb` still correctly 404s unarmed:

- **White step's colour-temperature slider was backwards** (cold read as
  warmer and vice versa). `wizardStepControlSpecs.White` in `config.js` had
  `lowLabel`/`highLabel` swapped relative to its own `a`/`b` gain convention
  -- `a: redGainPercent, b: blueGainPercent` means a positive delta raises
  red and lowers blue (warmer), but `highLabel` said "Cooler" for positive
  delta. Confirmed against Magenta's spec, which uses the same convention
  correctly (`highLabel: "More red"` for positive delta, a = redGainPercent).
  Fixed by swapping White's two labels; no change to the underlying gain
  logic, which was always correct.
- **White LEDs visibly blinked; colours did not.** Root-caused as
  achromatic (luminance) flicker being far more perceptible than chromatic
  flicker: once a pixel's whole grey component sits on one physically
  brighter channel instead of being spread across three independently (and
  out-of-phase) dithered colour channels whose combined ripple partially
  cancels, that channel's own error-diffusion alternation reads as a
  distinct, regular blink. **First attempted fix (a short low-pass filter on
  the pre-dither white value, with a monotonic-clock elapsed-time parameter)
  was measured and found not to work** -- confirmed numerically (a Python
  simulation of the exact algorithm, then reproduced as a failing xUnit
  test) that smoothing a near-constant target does not reduce dithering's own
  flip rate, because the flicker comes from error diffusion needing to
  alternate to represent *any* held fractional target, not from noisy input;
  smoothing occasionally made the pattern *more* regular. That attempt was
  fully reverted (`IDitheredChannelEncoder.Encode` back to its original
  2-argument signature). **Actual fix: white is no longer temporally
  dithered at all** -- `DitheredRgbw32Encoder` plain-rounds the extracted
  white component while the colour residual keeps dithering exactly as
  before. Verified in `DitheredRgbw32EncoderTests`: a constant target now
  produces zero flips (was previously guaranteed to alternate), and under
  randomised realistic sampling noise the flip rate roughly halves versus
  dithering white the same way as a colour channel. Trade-off, stated
  plainly in the class's own remarks and accepted deliberately: a slow fade
  through a white-heavy tone can now show the original 8-bit "stepping"
  near black that dithering was built to fix, specifically on white. Not
  yet re-confirmed against the physical strip that the blink is actually
  gone -- next thing for the operator to check.
- **"Try another photo" kept appearing on steps that only have one photo**
  (11 of 12 steps -- only White has three). The button was already
  functionally `disabled` in that case, not broken, but a disabled button
  that is still visually present on almost every step reads as a dead
  affordance. Changed to `show()` (hide entirely) instead of `disabled`
  when `photoCount <= 1`. **Did not fabricate or source new photos** -- this
  project's established convention is the operator's own curated,
  hand-labelled photography only (no external hosting, no stock images,
  and reusing a "confirmation" scene's mixed-hue photo as a substitute
  "tuning" photo would muddy exactly the single-dominant-hue signal tuning
  steps depend on). **If the operator wants real cycling on more steps,
  that needs a few more photos from them per step**, uploaded the same way
  as the original 19 (e.g. via imgbb links) -- flagged back to them rather
  than invented.

**PASS (2026-09-08, RGBW32 white-channel output, opt-in).**

```bash
DOTNET_CLI_HOME=/tmp/jfar2-dotnet-cli NUGET_PACKAGES=/tmp/jfar2-nuget-packages \
dotnet build src/Jellyfin.Plugin.RealtimeAmbilight/Jellyfin.Plugin.RealtimeAmbilight.csproj \
    --configuration Release --no-incremental -p:UseSharedCompilation=false
DOTNET_ROLL_FORWARD=Major dotnet test \
tests/Jellyfin.Plugin.RealtimeAmbilight.Tests/Jellyfin.Plugin.RealtimeAmbilight.Tests.csproj \
    --configuration Release -p:UseSharedCompilation=false
```

Zero warnings/errors; **95/95** (10 new: `DitheredRgbw32EncoderTests`,
`DdpPacketizerRgbwTests`, two new `WledRealtimeOutputTests`). Deployed
(stop → copy both DLLs → start) and confirmed a clean startup log with no
`[ERR]`/`[FTL]` lines and the usual `Realtime Ambilight output pump started.`
Re-verified the anonymous surface still 404s while unarmed
(`/amb`, `GetWizardState?tv=true`), i.e. the round-4 security fix survived
this change. **Not verified this round, same standing gap: the settings
page's new checkbox, and actually driving W on the physical strip** -- no
admin credential in this environment to save `SendWhiteChannel: true`
through the UI, so the feature has only been exercised via unit tests and a
clean-startup check with the (default, off) flag unchanged.

Built the feature the operator explicitly asked to unblock this round: an
**opt-in RGBW32 output path**, default off, so a plain-RGB installation is
byte-for-byte unaffected (`SendWhiteChannel` defaults to `false`, and the
whole path was smoke-tested at that default). Design follows exactly what
was investigated last round (see below and ADR-004), now actually built:

- **`Core/Output/IDitheredChannelEncoder`** (new): the one-method interface
  `DitheredRgb24Encoder` and the new `DitheredRgbw32Encoder` both implement,
  so `AmbilightFrameProcessor` and `JellyfinWledOutputService`'s calibration
  path can hold "whichever encoder this strip needs" without a type branch at
  every call site.
- **`Core/Output/DitheredRgbw32Encoder`** (new): mirrors
  `DitheredRgb24Encoder` exactly (per-channel temporal error diffusion, same
  transfer function via `Rgb24Encoder.ToTransferValue`) but for four channels.
  Per LED: `w = min(r, g, b)`, then `r -= w`, `g -= w`, `b -= w`; each of the
  four channels carries its own rounding error forward independently. This
  runs **last**, after brightness/saturation/gain/black-floor/wall-colour and
  the White step's colour-temperature push-pull have already been applied to
  the ordinary `LinearRgb` values -- so a warmer/cooler bias before
  extraction naturally comes out as "dimmer white channel + a tinted RGB
  residual" with no special-casing needed anywhere in the wizard.
- **`Core/Protocol/DdpPacketizer`**: `Packetize` gained an optional
  `bytesPerLed` parameter (default 3, unchanged call sites); it validates
  frame length against it and writes the DDP data-type byte as `Rgb24`
  (`0x0B`, unchanged) or the new `Rgbw32` (`0x1B`) constant accordingly.
  `ChannelsPerPacket` (1440) needed no change: it happens to divide evenly by
  both 3 and 4 (480 and 360 LEDs per packet), so no LED's bytes ever split
  across a packet boundary either way, though DDP would tolerate that anyway.
- **`Core/Wled/WledRealtimeOutput`**: new constructor parameter `bytesPerLed`
  (default 3). When it is 4, the send path **always** uses DDP and never
  consults `WledProtocolSelector` or `HyperionRawRgbPacketizer` at all --
  Hyperion Raw RGB has no RGBW variant, so RGBW silently overrides even an
  explicit "Hyperion Raw RGB" protocol choice rather than erroring or
  dropping the white channel. `CurrentProtocol` is still set (to a
  `WledProtocolSelection(Ddp, "RGBW forces DDP...")`) so the settings page's
  protocol readout stays accurate. Frame-length validation now checks
  `% bytesPerLed` instead of a hardcoded `% 3`.
- **`Core/Output/AmbilightFrameProcessor`**: new constructor parameter
  `sendWhiteChannel` (default false) picks `DitheredRgbw32Encoder` or
  `DitheredRgb24Encoder` once, at construction -- like the physical LED
  layout, this is treated as a hardware fact fixed for the processor's
  lifetime, not a live-tunable resolved per frame the way brightness/
  saturation are.
- **`JellyfinWledOutputService`**: reads `configuration.SendWhiteChannel` once
  at startup, derives `_bytesPerLed` (3 or 4), and threads it through both the
  real-playback `AmbilightFrameProcessor` and the calibration photo path's own
  encoder field (now `IDitheredChannelEncoder`, was hardcoded
  `DitheredRgb24Encoder`). Also fixed a latent bug this surfaced: the
  blank-the-strip-at-playback-start call built `new byte[_ledCount * 3]`
  unconditionally, which would have thrown inside `WledRealtimeOutput`'s
  frame-length validation on every playback start once RGBW was enabled
  (`_ledCount * 4` bytes needed, not `* 3`) -- would only ever have been
  caught live, on a real playback start, exactly the kind of bug this project
  has repeatedly only found by testing, not by review; fixed before it could
  ship instead.
- **`PluginConfiguration.SendWhiteChannel`** (new, default `false`): the
  opt-in switch itself. Documented as a restart-required hardware fact, like
  `WledHost`, not a live preference.
- **`WledDiscoveryService`**: `ReadRealtimeSettingsAsync` (backs the existing
  `GET RealtimeAmbilight/Discovery/Settings` endpoint the settings page
  already calls on every load) now also reads `hw.led.ins[].type` from
  `/json/cfg` and reports `HasWhiteChannelHardware` -- true only when a strip
  reports chipset type `30` (SK6812 RGBW), the one type ADR-004 actually
  confirmed carries a physical white diode for this installation. No new
  endpoint was needed since the existing one already round-trips through the
  settings page.
- **Settings page (WLED tab)**: new "Does your strip have a white LED?"
  section with a `sendWhiteChannel` checkbox (off by default, loads/saves
  like `allowWledControl`) and a suggestion banner (`#rgbwSuggestion`) that
  appears only when `HasWhiteChannelHardware` came back true **and** the
  checkbox is currently unchecked -- detection only ever suggests, it never
  flips the checkbox itself, so a saved operator choice (on or off) always
  wins on the next load. This directly matches what the operator confirmed:
  *auto-detect with a manual override that always wins*.
- **Skipped, per explicit operator instruction this round:** the live
  current-draw safety test that would otherwise have blocked this. The
  operator stated they had already measured it previously with everything on
  and are "comfortably within the 40 A of the supply" (`ruimschoots binnen
  de 40 A van de voering`), which the RGBW path cannot exceed anyway since it
  only *redistributes* each LED's already-computed light output across R/G/B
  and W rather than adding to it (`w = min(r,g,b)` is subtracted from the
  colour channels it is extracted from, never added on top) -- driving W
  costs at most what driving R+G+B mixed white already cost, per WLED's own
  ABL, which still enforces `maxpwr` regardless of channel count.
- **Not changed, on purpose:** the White step's colour-temperature slider
  stays exactly what it already was (an R/gain vs B/gain push-pull, still
  purely relative/subjective, no Kelvin value) -- confirmed with the
  operator this round that a relative slider is sufficient, so nothing in
  the wizard's per-step control code needed to change for RGBW to work
  correctly with it.

**PASS (2026-09-08, second operator hands-on-test round: session-gated TV surface, per-step controls, mobile fix, muted wall colours).**

```bash
DOTNET_CLI_HOME=/tmp/jfar2-dotnet-cli NUGET_PACKAGES=/tmp/jfar2-nuget-packages \
dotnet build src/Jellyfin.Plugin.RealtimeAmbilight/Jellyfin.Plugin.RealtimeAmbilight.csproj \
    --configuration Release --no-incremental -p:UseSharedCompilation=false
DOTNET_ROLL_FORWARD=Major dotnet test \
tests/Jellyfin.Plugin.RealtimeAmbilight.Tests/Jellyfin.Plugin.RealtimeAmbilight.Tests.csproj \
    --configuration Release -p:UseSharedCompilation=false
```

Zero warnings/errors; **86/86**. Deployed and live-verified via curl for the
anonymous surface (same no-admin-credential constraint every round):
`/amb`, `WizardState?tv=true` and `Photo/White/0` all now correctly answer
**404 before any `Start` call** -- the security fix below, confirmed working.
**Not verified this round: the authenticated `Start`/`Finish`/`MoveWizard`/
`Retune` flow itself, or any of the settings-page JS** -- no admin credential
available in this environment, and this remains the standing gap (now four
rounds running).

This round is the operator's second hands-on test, and every change below is
a direct response to specific feedback:

- **The TV-facing surface is no longer permanently reachable.**
  `CalibrationWizardState` gained `IsArmed`/`Arm()`/`Disarm()`. New
  admin-only `POST Calibration/Start` arms it (and resets to White); new
  `POST Calibration/Finish` disarms it and releases WLED (same effect as the
  old `DELETE Preview`, which the settings page's "Stop" button now calls
  under a different name). `Pattern` (`/amb` included), `GetWizardState`,
  `GetPhoto` and `PostPhotoFrameAsync` -- the entire anonymous surface -- all
  check `IsArmed` first and return a plain 404 otherwise. Motivation stated
  directly: an unauthenticated page permanently reachable on a Jellyfin that
  is internet-facing is its own exposure, however little it can actually do.
  The settings page now shows a "Start calibration" button before anything
  else appears, and relabels its own stop button "✓ Done — finish
  calibration" once `IsLastStep` is true (new field on
  `CalibrationWizardStateResponse`, alongside new `ConfirmationCount`). The TV
  page distinguishes "never connected" from "was connected, now 404'd" (an
  `everConnected` flag) so *finishing* the wizard shows a clear "Calibration
  finished, you can close this page" screen instead of the same generic
  nothing a never-started link would show.
- **Confirmation steps cut from 10 to 5** (`CalibrationWizard.ConfirmationOrder`:
  Blue-Green, Final test, Orange-Red, Purple-Teal, Yellow-Pink). The
  unused five photo files (Cyan-Magenta, Purple, Blue-Red, Blue-Yellow,
  Pink-Grey) were deleted from `Configuration/CalibrationPhotos`, not merely
  unreferenced -- 14 embedded photos now, down from 19.
- **Per-step relevant controls, following ordinary display-calibration
  convention instead of the same three raw RGB sliders at every step.** New
  `wizardStepControlSpecs` in `config.js` maps each tuning step's colour name
  to exactly the control that matters: White gets a **colour-temperature**
  (warmer/cooler) control; Red/Green/Blue get that primary's own gain;
  Yellow/Cyan/Magenta each get a **two-primary balance** control -- literally
  "more red" at Magenta and "more/less green" at Yellow, which is exactly
  what was asked for. Mechanically all of these are the same underlying
  `RedGainPercent`/`GreenGainPercent`/`BlueGainPercent` fields the settings
  page already had; a "balance" control is a synthetic push-pull
  (`a = 100+delta, b = 100-delta`) over two of them, so **no backend change
  was needed for this at all** -- White's colour-temperature control and
  Magenta's balance control are mechanically the same red/blue axis with a
  different label. The full raw sliders (brightness, saturation, black level
  floor, raw R/G/B, wall colour) moved into a new collapsed "All colour
  controls (advanced)" section; only overall LED brightness stayed always
  visible, since it is the one control that is relevant at literally every
  step.
- **Mobile layout bug fixed.** `addStepButtons()`'s `+`/`-` buttons were
  inserted as plain siblings before/after the range `<input>`; `emby-input`
  upgrades the element in place but it is still block-level by default, so
  the buttons stacked above/below the slider instead of beside it on a phone
  -- exactly as reported. Fixed by wrapping the range and both buttons in an
  explicit flex row (`range.replaceWith(wrapper)`, then re-appending all
  three into it), which forces the layout regardless of how `emby-input`
  renders internally. This is unverified in an actual mobile browser, same
  standing caveat as everything else in this section.
- **Wall colour presets replaced.** The previous list (navy blue, hunter
  green, saturated terracotta) leaned toward bold accent-wall colours; the
  operator's complaint was that real, contemporary interiors (their example:
  Unsplash "modern interior" search results) lean soft and muted instead --
  warm off-whites, greiges, dusty sage/blue, soft clay -- not samples spread
  evenly across the colour wheel. Replaced with 12 desaturated, lighter
  tones. Sourced from general interior-design domain knowledge, not an
  actual Unsplash fetch (this environment cannot visually browse a photo
  search the way it can view a locally downloaded image) -- worth a sanity
  check against real reference photos if the operator has strong opinions on
  the specific hexes.

**Investigated but deliberately not built this round, on the operator's own
choice to finish the above first:** sending a real white (W) channel to the
SK6812 RGBW strip instead of mixing white from R+G+B, which is what a proper
colour-temperature control on White ultimately wants. Full findings written
directly to the operator in-conversation; the short version, all of it
already substantiated in `docs/architecture/adr/ADR-004-wled-realtime-protocol-strategy.md`
(read that ADR first, it is extensive and already answers most of "why"):
switch DDP from `RGB24` to `RGBW32` (2 packets -> 3 for 831 LEDs, still well
inside the existing latency budget), extract `w = min(r,g,b)` per LED as a
final step just before encoding (leaving the existing sampling/interpolation/
adjustment/dithering pipeline in `LinearRgb` untouched), and keep WLED's own
`rgbwm` at `0` (Manual) throughout -- a firmware update already flipped this
to auto-white once before, silently subtracting white from RGB outside the
plugin's own colour pipeline, and that must not be allowed to happen silently
again. Explicitly **not yet measured**: current draw with the W channel
actually driven (only R+G+B-mixed white was ever measured, at 19.65 A/831
LEDs against the 40 A cap) -- required before this ships, per this project's
standing safety practice around `maxpwr`.

**PASS (2026-09-08, operator-curated photo set, 17-step wizard, edge weighting, dwell filter).**

```bash
DOTNET_CLI_HOME=/tmp/jfar2-dotnet-cli NUGET_PACKAGES=/tmp/jfar2-nuget-packages \
dotnet build src/Jellyfin.Plugin.RealtimeAmbilight/Jellyfin.Plugin.RealtimeAmbilight.csproj \
    --configuration Release --no-incremental -p:UseSharedCompilation=false
DOTNET_ROLL_FORWARD=Major dotnet test \
tests/Jellyfin.Plugin.RealtimeAmbilight.Tests/Jellyfin.Plugin.RealtimeAmbilight.Tests.csproj \
    --configuration Release -p:UseSharedCompilation=false
```

Zero warnings/errors; **86/86**. Deployed and live-verified via curl again
(same no-admin-credential constraint as every round so far): `/amb` → 200,
fresh `WizardState?tv=true` → `StepCount:17, PhotoCount:3` for White,
`Calibration/Photo/White/0` → the real embedded JPEG in ~12 ms (was a
network fetch to Wallhaven before), `Calibration/Photo/Bogus/0` → 404,
`Calibration/Photo/Final%20test/0` (a confirmation-step name with a space) →
200, a synthetic `PhotoFrame` upload → 204 and WLED's `/json/info` reported
`live: true` immediately after, `maxpwr` unchanged at 40000 throughout, and
`systemctl restart` released it back to `live: false` cleanly (same
credential-free cleanup path as last round).

The operator supplied 19 hand-picked, hand-labelled photos this round
(`s0_whitelevel{1,2,3}`, `s1_{red,green,blue}`, `s2_{cyan,magenta,yellow}`,
six more `s3_*` and four `s4_*`), which **replace the Wallhaven set
entirely** -- no more external hosting, no attribution needed, no CORS
proxy-fetch cost. Every image was downloaded, cropped to 16:9 and resized to
1080p with `ffmpeg` (`scale=1920:1080:force_original_aspect_ratio=increase,
crop=1920:1080`, the "cover" fit), landing at 51 KB-720 KB each (4.3 MB for
all 19 -- was multi-MB *per photo* hotlinked from Wallhaven), and embedded
directly in the plugin under `Configuration/CalibrationPhotos/*.jpg`
(`<EmbeddedResource>` in the csproj, same mechanism as `config.html`/`.js`).
`CalibrationController.GetPhoto` now reads
`typeof(Plugin).Assembly.GetManifestResourceStream(...)` instead of
proxying an HTTP fetch -- confirmed resource names with a throwaway
`Assembly.GetManifestResourceNames()` console app before trusting the path
string, given how much this session has been burned by unverified
assumptions about wire formats.

**The wizard restructured around the operator's own naming, based on their
answers to five clarifying questions asked before any of this was built**
(all four via `AskUserQuestion`, one folded into the write-up): 7 tuning
steps (`CalibrationWizard.TuningOrder`: White, Red, Green, Blue, Yellow,
Cyan, Magenta -- the correct RGB secondary set, not the previous session's
ad hoc Yellow/Purple/Orange) from `s0`+`s1`+`s2`, followed by 10 confirmation
steps (`ConfirmationOrder`, named after their `s3`/`s4` file suffixes:
Blue-Green, Cyan-Magenta, Final test, Orange-Red, Purple, Purple-Teal,
Blue-Red, Blue-Yellow, Pink-Grey, Yellow-Pink) with sliders left live and
visible throughout, not a separate non-interactive mode -- the operator
chose "manual next, sliders still visible" specifically. White is no longer
special-cased: given the operator curated three dedicated white-level photos
and chose to cycle through them the same way as any other step's "try
another photo", White now goes through the identical real-photo-sampling
path as every other step. This deleted the entire synthetic-colour
machinery from the previous round as genuinely dead code rather than leaving
it unused: `CalibrationPreview`/`CalibrationSide`/`CalibrationReferenceColour`
records, `ShowCalibrationPreviewAsync`, `BuildCalibrationFrame`, and the
`Credit*`/`HtmlColour`/`Side` fields on the wizard-state DTO (no external
attribution needed for the operator's own photos; no synthetic edge colour
left to report). `JellyfinWledOutputService` now tracks calibration liveness
with one plain `_calibrationActive` flag instead of overloading a
now-deleted record's non-nullness.

**Two new Core mechanisms, both requested directly by the operator and kept
out of the wizard on their explicit instruction, added to the Advanced tab
instead:**

- **Linearly weighted edge sampling** (`EdgeSampler`'s `EdgeWeight`, shared
  by both `SampleRun`/`SampleRunSrgb`): a sampled band's row/column nearest
  the picture's true edge counts up to 1.0, ramping linearly down to a
  `EdgeWeightFloor` of 0.3 at the band's inner boundary, instead of the
  previous flat unweighted mean. Physically motivated: a wall continues what
  is *at* the edge, not an average of a band that reaches some way into the
  picture. Always on, no new setting -- the operator suggested a plain
  linear ramp over "an algorithm", and there was no compelling reason to
  gate anything this cheap and this clearly correct behind a toggle.
- **`DwellFilter`** (new, `Core/Output`): holds each physical LED at its last
  committed colour until a newly sampled colour has persisted, independently
  per LED, for at least `MinimumColourHoldMilliseconds` (new
  `PluginConfiguration` field, Advanced tab, default 0/off). The operator
  chose the "hard threshold" option over a soft crossfade or both combined.
  Deliberately takes elapsed time as a parameter rather than reading the
  clock itself, so it is a pure function of its inputs and needed no real
  `Task.Delay` to unit test. Wired into `AmbilightFrameProcessor.Process`
  between interpolation and colour adjustment -- i.e. it gates on the raw
  sampled picture colour, before the operator's own brightness/saturation
  preference is layered on. Calibration photos do not use it: a single
  static image has nothing to debounce.

Deferred, not forgotten: yellow still has no true 4K photo (documented in
`CalibrationWizard.Photos`'s doc comment, unchanged from last round -- the
operator's own `s2_yellow.jpg` is a sunflower macro, fine as delivered but
not re-verified against a 4K bar since it was never fetched from an external
source to check).

**PASS (2026-09-08, operator feedback round on the wizard: whole-perimeter tuning, real photo edge-sampling, short URL).**

```bash
DOTNET_CLI_HOME=/tmp/jfar2-dotnet-cli NUGET_PACKAGES=/tmp/jfar2-nuget-packages \
dotnet build src/Jellyfin.Plugin.RealtimeAmbilight/Jellyfin.Plugin.RealtimeAmbilight.csproj \
    --configuration Release --no-incremental -p:UseSharedCompilation=false
DOTNET_ROLL_FORWARD=Major dotnet test \
tests/Jellyfin.Plugin.RealtimeAmbilight.Tests/Jellyfin.Plugin.RealtimeAmbilight.Tests.csproj \
    --configuration Release -p:UseSharedCompilation=false
```

Zero warnings/errors; **81/81**. Deployed and, unlike previous rounds,
**exercised live against the physical strip via curl** (no admin credential
available in this environment to drive the authenticated endpoints from the
settings page itself, so the anonymous TV-facing surface was verified
directly): `GET /amb` → 200, `GET WizardState?tv=true` → `TvConnected: true`,
`GET Calibration/Photo/Blue/0` → a real 3.8 MB 5098x3399 JPEG served
same-origin, `POST PhotoFrame` with a synthetic 320x180 grey buffer → 204 and
WLED's own `/json/info` reported `"live": true` immediately after, `maxpwr`
unchanged at 40000 throughout, and a Jellyfin restart (standing in for the
admin's own missing Stop click) released it back to `"live": false` cleanly.
**Not exercised: the settings page's own JS clicked through in a real
browser** -- still the standing gap, see the next-actions item below.

This round is a direct rewrite driven by the operator's first hands-on test
of the wizard. Every item below is a response to specific feedback, not a
speculative addition:

- **The TV link is now `/amb`** -- a second route on the same `Pattern()`
  action (`[HttpGet("Pattern")] [HttpGet("/amb")]`), not a redirect. The
  previous `RealtimeAmbilight/Calibration/Pattern` path was painful to type
  with a remote control's arrow keys. The settings page now also states
  `http://` explicitly and builds the link from `window.location.host`
  itself rather than trusting `window.location.origin`, so it can never
  inherit `https://` from however the admin happens to be browsing Jellyfin.
- **Whole-perimeter tuning, not per-side.** `CalibrationSide` gained an `All`
  member; `BuildCalibrationFrame` paints every physical LED when given it.
  The wizard's own Side selector is gone from the UI and the server-side
  request DTO both -- `MoveWizardAsync` hardcodes `CalibrationSide.All`. The
  per-side trim cards still exist, explicitly relabelled "advanced, rarely
  needed", collapsed by default.
- **Real photo edge-sampling replaces solid colour blocks**, the biggest
  change. `EdgeSampler.SampleSrgb` (new, alongside the existing
  `SampleBgra`) decodes full-range sRGB rather than BT.709 limited range --
  a browser canvas is not a decoded video frame, and feeding it through the
  video decode path would have subtracted a black level that is not there.
  The TV page draws its fullscreen photo to a 320x180 canvas, reads
  `getImageData`, and `POST`s the raw RGBA bytes to the new anonymous
  `Calibration/PhotoFrame`; `JellyfinWledOutputService.ShowCalibrationPhotoAsync`
  samples, interpolates (`LinearLightInterpolator`, unchanged) and adjusts it
  exactly as real playback would, then sends it. The photo is now genuinely
  **full-screen**, not inset -- there is no separate synthetic edge glow to
  protect from overlap any more, the photo's real edges are the reference.
- **Same-origin photo proxy, and why it is load-bearing, not cosmetic:**
  `GET Calibration/Photo/{colour}/{index}` fetches and caches each Wallhaven
  image server-side and re-serves it from the plugin's own origin. Confirmed
  this session that `th.wallhaven.cc` sends no `Access-Control-Allow-Origin`
  header at all, so a canvas fed directly from Wallhaven's CDN would be
  cross-origin-tainted and `getImageData` would throw on every sample --
  the whole edge-sampling mechanism depends on this proxy existing.
- **Live retune, no Save required.** `RetuneCalibrationAsync` re-runs only
  the colour-adjustment step against whatever is cached (a photo's sampled
  edges, or White's flat colour) -- it never re-fetches or re-samples. Every
  colour-tuning slider's `input` event now calls a 120 ms-debounced
  `POST Calibration/Retune` instead of the old restart-a-named-preview flow.
  A small **&minus;/+** button pair was added beside every range slider
  inside the calibration section (`addStepButtons()`) for blind, repeated
  taps while watching the TV instead of the phone.
- **Auto-start, no manual "Start preview" button any more.** Arriving at
  White (via the wizard's own Next/Back) auto-starts its flat preview
  server-side; arriving at a photo colour does nothing itself -- the TV's
  own upload, once it finishes loading that step's photo, is what starts
  those, asynchronously and independently. `JellyfinWledOutputService`
  tracks this with one flag (`_calibrationActive`) instead of overloading
  `_calibrationPreview`'s non-nullness, since a photo-driven preview has no
  `CalibrationPreview` record to speak of; a `_calibrationAdjustment` field
  carries the last-known tuning across both paths so a photo upload from the
  TV (which knows nothing about sliders) still applies whatever the operator
  last set.
- **Opening the TV link is now itself "start the wizard".**
  `CalibrationWizardState.NoteTvPoll()` resets to step 0 (White) whenever the
  gap since the previous *TV* poll exceeds 20 s -- distinguished from the
  settings page's own occasional `GetWizardState` reads via a `tv=true` query
  flag the TV page alone sends, since without that an admin merely loading
  the settings page before any TV had ever connected would reset the wizard
  on every page load. `TvConnected` (poll within the last 5 s) is now also in
  the state DTO so the settings page can say "waiting for the TV" instead of
  a misleadingly generic status line.
- **Nature photos upgraded to a real minimum resolution.** Re-searched
  Wallhaven with `atleast=3840x2160` added to the same colour-swatch query
  approach as before; 12 of the 15 non-white photos are now genuinely 4K+
  (the previous, lower-resolution picks are gone). Yellow could not be
  upgraded: every 4K-filtered search for the yellow swatch, with or without
  `q=sunflower`/`q=nature`, returned near-black astrophotography with a small
  yellow star or moon rather than an actual yellow scene -- visually checked,
  rejected, and documented as a known gap in `CalibrationWizard.Photos`'s own
  doc comment rather than silently shipping a bad match. One newly-found blue
  4K candidate (`8o836k`) was dropped for the same "deleted uploader, nobody
  to credit" reason as before.
- **Two more bugs caught by testing live instead of trusting the code, same
  pattern as the `hostName`/`host` fix earlier today:** `[FromQuery] bool tv`
  does not accept `tv=1` -- ASP.NET's default bool binder only parses
  `true`/`false` -- so the TV page's own polling silently never marked itself
  connected until this was caught by `curl`ing the live endpoint and reading
  the 400 back; fixed by sending `tv=true`. And `CA1806` (build-breaking, not
  runtime) caught a `TryParse` result going unchecked in
  `BuildWizardStateResponse`.

**Live TV browser testing is not optional here and has still not
happened.** Everything above was verified at the HTTP layer. The specific
things that can only be seen by actually loading the settings page and the
TV page in real browsers: whether `emby-input`'s custom-element rendering
tolerates a raw `<button>` inserted immediately before/after its `<input>`
(`addStepButtons()`'s approach, unverified in a real DOM), whether the
canvas-sampling path actually fires reliably on a real TV browser's `<img
onload>` timing, and whether the 1.5 s polling cadence feels responsive
enough switching steps in practice.

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

*Superseded 2026-09-08: the list below (Hue not yet merged, old photo-based
wizard, decoder-lead cause unknown) predated most of this file's own "What
shipped 2026-09-08" entry above and no longer describes reality. Replaced
with the actual current plan, agreed with the operator, for work reachable
without the TV (currently in use elsewhere):*

1. **Documentation** (this pass) -- README, ADR index (added ADR-011 through
   014, fixed the stale index table), and this handoff file. Done as of this
   entry.
2. **Rebuild the settings page toward the "Ambient Control" redesign.**
   A preview mockup was published as a standalone Artifact earlier this
   session (dashboard-with-status-cards layout, WLED/Hue as separate cards,
   drill-down detail views, inline SVG colour-wheel for the calibration
   curve). The operator additionally asked for a specific interaction
   pattern not yet in that preview: each tab shows a **view-only summary**
   of what is actually configured once it is configured, with a
   **reconfigure** action that re-enters the step-by-step flow *pre-filled*
   from current values rather than blank, so a specific step can be jumped
   to directly. Update the preview artifact with that pattern first (agreed
   with the operator -- preview before real implementation), then build it
   into `config.html`/`config.js` for real.
3. **Close the Hue test-coverage gap.** `HueEntertainmentService` (outer
   plugin project) has zero unit tests -- it never has, being heavily
   network/hardware-dependent -- including the "turn on a light that was
   off" fix from this session. Requires putting `HueBridgeClient`/
   `HueLightControl` behind interfaces to be mockable; not yet started.
4. **Continue the efficiency sweep.** Only `EdgeSampler` (per-pixel
   transfer-function math → 256-entry lookup tables) has been done.
   `LinearLightInterpolator`, `BlackBorderDetector`, `HueCorrectionCurve`
   and the new `WledTemporalSmoother` have not been reviewed for the same
   kind of hot-path win.
5. **Real-content validation, once the TV is available again** (blocked on
   the operator, not on code): the RGBW white-channel ceiling default (50%)
   was derived from flicker-photometry testing with synthetic swatches, not
   varied playback content -- re-check and adjust `WhiteChannelStrengthPercent`
   while watching an actual bright/white scene. Same for the widened
   Brightness range (1-200%, currently set to 140% live) and the Hue
   turn-on-if-off fix, neither of which has been exercised through a real
   Jellyfin playback session since deployment.
6. Register this project's own repository in the reference host's Jellyfin
   (Dashboard → Plugins → Repositories →
   `https://github.com/cappie15/jellyfin-ambilight-realtime/releases/download/v0.1.1/manifest.json`)
   -- the one remaining step for the plugin-catalog fix, and a live-server
   settings change the operator needs to do themselves, not a code deploy.
7. Merge `feat/colour-calibration-curve` to `main` once the operator is
   satisfied with a stable checkpoint -- it currently carries far more than
   its name (RGBW/VAAPI/Hue fixes, the manifest fix), all committed and
   individually deployed already, but never merged.
8. Add source-profile detection and separately validate HDR10, HLG and Dolby
   Vision before claiming general HDR support. The HDR graph runs and one
   HDR10 setup has been observed working; HLG/DV remain unvalidated.
