# ADR-004 — WLED realtime protocol strategy

- **Status:** **Accepted** — operator review 2026-09-05
- **Date:** 2026-09-05
- **Relates to:** master prompt §13, §14, §15, §16, §17, §38, §39, §61, §69
- **Evidence:** [research.md](../research.md) §10, §11 — WLED `f49e541`

## Context

The reference installation is **832 LEDs on one controller across four physical
outputs**. The predecessor plugin could not drive it: it is limited to roughly
490 LEDs.

Reading both sides of the wire established why. `jellyfin-ambilight` sends to
**port 19446** (`AmbilightInProcessPlayer.cs:36`), which is WLED's Hyperion raw
RGB port (`REALTIME_MODE_HYPERION`, `const.h:300`). That protocol is a bare RGB
triplet stream in a **single datagram with no offset field and no
fragmentation**, so its ceiling is `(MTU − IP/UDP headers) / 3 ≈ 490` LEDs.

**The ~490 limit is a property of the chosen protocol, not of WLED.** This is
exactly what §13 asserts, now confirmed from source.

## Decision

**DDP is the preferred realtime protocol. Multi-packet DDP is implemented
properly. Hyperion raw RGB is not used.**

### Wire format (from `ESPAsyncE131.h` and `udp.cpp:794-806`)

Port **4048**. 10-byte header:

| Offset | Field | Our value |
|---|---|---|
| 0 | flags | `DDP_FLAGS_VER1` (`0x40`), `\| DDP_FLAGS_PUSH` (`0x01`) on the final packet only |
| 1 | sequence | `seq++ & 0x0F` — **4 bits, 1–15; 0 means "unused"** |
| 2 | data type | `DDP_TYPE_RGB24` (`0x0B`) |
| 3 | destination | `DDP_ID_DISPLAY` (`1`) |
| 4–7 | channel offset | big-endian uint32, **in bytes** |
| 8–9 | data length | big-endian uint16, **exact** |
| 10.. | payload | RGB triplets |

`DDP_CHANNELS_PER_PACKET` is **1440** (480 LEDs). Largest datagram is
`10 + 1440 + 8 + 20 = 1478` bytes — inside a 1500 MTU, no IP fragmentation.

### Rules the receiver forces on us (`e131.cpp:25-98`)

- **`dataLen` must be exact.** WLED drops on `packetLen < DDP_HEADER_LEN + c + dataLen`
  ("DDP packet incomplete", line 71) and on `maxDataIndex > dataLen` ("data bounds
  exceeded", line 79).
- **Never set `DDP_FLAGS_TIME`** — it shifts the payload 4 bytes and WLED does not
  support timecodes (line 68).
- **Never set QUERY / REPLY / STORAGE** — rejected outright (lines 42–45).
- **PUSH means "render now"** (line 44). WLED renders every packet until it has
  seen its first push, then only on push (lines 93–97). Set PUSH on the **last
  packet of each frame** so a multi-packet frame renders atomically.
- **Realtime lock is re-armed per accepted packet**, `realtimeTimeoutMs` default
  **2500 ms** (`wled.h:432`). Any output rate we use (§40: down to a few fps at
  the smooth end) stays far inside this, so no keepalive is needed during
  playback. Conversely, simply *ceasing* transmission releases realtime control
  after 2.5 s — the fade of §38 must complete before we stop sending.

### The reference case

The controller reported 832 LEDs when the measurements in this ADR were taken.
The strip is physically **831**, and the controller was corrected afterwards
(research.md §17). Measurements below are left at their as-taken values; the
design arithmetic uses 831.

| | |
|---|---|
| channels | 831 × 3 = **2493** |
| packets | ⌈2493 / 1440⌉ = **2** |
| packet 1 | offset 0, len 1440, flags `VER1` |
| packet 2 | offset 1440, len 1053, flags `VER1 \| PUSH` |

Two packets, no fragmentation. **The reference installation is an ordinary DDP
case, not a stress case.**

### The real ceiling, and how we report it

`e131.cpp:47` — *"reject late packets belonging to previous frame (assuming 4
packets max. before push …)"*. WLED's out-of-order filter assumes **≤4 packets per
frame**. At 480 LEDs/packet that is **~1920 LEDs**, and it only bites when the
user has `e131SkipOutOfSequence` enabled.

Per §14 we never truncate. Above 1920 LEDs the UI states the limit in plain
language and names the remedy (disable skip-out-of-sequence on the controller).

### Protocol selection (§16)

Normal users see `Auto (recommended)`; the UI reveals the outcome as
`Protocol: Auto (DDP)`. Auto inspects firmware version, LED count and known
compatibility, and picks the safest low-latency option. Expert mode allows an
explicit override.

| Protocol | Port | Offset | Ceiling | Role |
|---|---|---|---|---|
| **DDP** | 4048 | 32-bit byte offset | ~1920 soft | **default** |
| E1.31 / sACN | 5568 | universe | 170/universe | fallback |
| Art-Net | 6454 | universe | 170/universe | fallback |
| Hyperion raw | 19446 | **none** | ~490 | **not used** |

### Controller-state hazards to detect at first run (§61)

Three WLED settings silently corrupt or suppress output and must be read and
surfaced rather than discovered by the user in the dark:

| Setting | Effect | Source |
|---|---|---|
| `DMXAddress` | added to our start index: `start += DMXAddress / ddpChannelsPerLed` | `e131.cpp:63` |
| `arlsOffset` | added again at pixel level: `pix = i + arlsOffset` | `udp.cpp:668` |
| `realtimeOverride` | packets accepted but **not displayed** | `wled.h:723` |

## Consequences

**Positive**

- 832 LEDs works, and so does an order of magnitude more.
- Correct PUSH semantics give tear-free multi-packet frames.
- No IP fragmentation at the chosen packet size.
- Limits reported honestly instead of silently truncating (§14, §75).

**Negative**

- UDP is unreliable; a dropped packet corrupts part of one frame. Acceptable —
  the next frame arrives in ~33 ms, and §26 ranks latency above precision.
- The 4-bit sequence gives weak reordering protection. Mitigated by keeping frames
  to few packets and by sending a frame's packets back-to-back.
- Auto-selection needs a WLED capability probe, which adds a JSON API dependency
  at setup time (not at frame time).

## Controller survey — wled-livingroom, read-only, 2026-09-05

Read via `GET /json/info`, `/json/state` and `/json/cfg`. No packets were sent to
the realtime path.

| Property | Value | Consequence |
|---|---|---|
| `ver` | **16.0.0** (`vid 2605030`), `ESP32_Ethernet` | matches the researched source line; **wired ethernet**, not WiFi — good for jitter |
| `leds.count` | **832** | matches the reference installation |
| bus config | **one bus**, `start=0 len=832 pin=[16] type=30` | `TYPE_SK6812_RGBW` (`const.h:354`) — **the strip is RGBW, not RGB** |
| segments | one segment, `start 0 stop 832`, `ledmap: 0` | contiguous logical range, no custom LED map to defeat our indexing |
| `if.live.seqskip` | **`false`** | ⭐ the out-of-sequence filter is **disabled**, so the ~1920 LED heuristic ceiling does **not** apply on this controller |
| `if.live.dmx.addr` | **`1`** | ⚠ non-zero, but harmless *only* by integer truncation: `1/3 = 0` and `1/4 = 0` |
| `if.live.offset` (`arlsOffset`) | `0` | no pixel shift |
| `state.lor` (`realtimeOverride`) | `0` | realtime data will actually be displayed |
| `if.live.timeout` | `25` → **2500 ms** | confirms `realtimeTimeoutMs` |
| `if.live.maxbri` | **`false`** | ⚠ "force max brightness" is off, so master brightness scales our output |
| `state.bri` | **`128`** | ⚠ **our output is currently halved** |
| `if.live.no-gc` | `true` | gamma correction is **not** applied to realtime data — ours is the only gamma stage (§55) |
| `hw.led.maxpwr` / `info.leds.pwr` | 40000 mA / 5160 mA | ABL active at a 40 A cap |
| `hw.led.fps` | 42 | our ~40 fps ceiling (§40) fits under the strip's refresh budget |
| `info.live` | `false` at query time | no realtime consumer was active |

### Findings that change the design

**1. `seqskip=false` removes the soft ceiling.** The ~1920 LED limit documented
above is a property of WLED's out-of-order filter, and that filter is off here.
The limit stays in the UI logic because other users will have it on, but it is
not a constraint for this installation.

**2. The strip is RGBW — but RGB24 is confirmed sufficient.**

| DDP data type | Channels | Packets for 832 |
|---|---|---|
| **`DDP_TYPE_RGB24`** | 2496 | **2** ← chosen |
| `DDP_TYPE_RGBW32` | 3328 | 3 |

Sending RGB24 leaves the white chip dark: WLED calls
`setRealtimePixel(i, r, g, b, 0)`. Whether that looks acceptable on SK6812 is a
visual question, so it was put to the operator, who confirmed on 2026-09-05 that
white rendered from R+G+B alone looks fine (research.md §17).

**831 LEDs therefore stay at two packets per frame**, and the RGBW32 path is not
needed for this installation. It remains worth implementing later for users whose
white balance demands it.

**3. Master brightness silently halves our output.** `bri=128` with
`maxbri=false` means everything we send is scaled to 50%. Per §39 we must not
change the user's configuration, so the correct behaviour is to **detect and
report** this in the first-run check and Diagnostics, and let the user decide.

**4. ~~ABL will distort the ALL-WHITE calibration pattern.~~ REFUTED by
measurement — see Amendment 2.** This estimated ~60 mA/LED and concluded the
40 A cap would be exceeded. Measured at forced full brightness, all 831 LEDs
white draw **19.65 A**, comfortably under the cap. ABL does not engage, and the
§47 white pattern renders at full brightness.

**5. Topology, resolved.** §13 describes 832 LEDs across **four physical
outputs**; the controller reports **one bus on a single pin**, and the operator
confirmed on 2026-09-05 that the whole strip hangs off **GPIO 16** and may draw
40 A continuously. The §13 description is out of date. Immaterial for us: we
address logical indices either way, exactly as §13 requires.

**Still to research:** mDNS discovery (§17), and the clean-release path (§39).

## Protocol validation against the live controller, 2026-09-05

Measured with a throwaway DDP sender against wled-livingroom, using WLED's own power
estimate (`info.leds.pwr`) as an objective read-back channel — it scales with the
number of lit LEDs, which makes truncation measurable without seeing the strip.

Fitting the two extremes gives `pwr = 951.3 + 3.7353 x n_lit`. Every measured
point lands on that line:

| LEDs lit | Packets | Measured (mA) | Model | Error |
|---|---|---|---|---|
| 1 | 1 | 955 | 955.0 | +0.0 |
| 100 | 1 | 1325 | 1324.8 | +0.2 |
| 480 | 1 | 2744 | 2744.2 | −0.2 |
| **481** | **2** | 2748 | 2747.9 | +0.1 |
| **490** | 2 | 2782 | 2781.5 | +0.5 |
| **491** | 2 | 2786 | 2785.3 | +0.7 |
| **832** | 2 | 4059 | 4059.0 | +0.0 |

Maximum error across the range: **0.7 mA**.

- **No truncation.** 481 sits exactly on the line drawn by 480, across the
  one-packet → two-packet boundary. Fragmentation is correct.
- **The ~490 ceiling is gone.** 490 and 491 show no discontinuity whatsoever. The
  §13 defect is fixed and measured.
- **All 832 LEDs receive data.** 832 lands exactly on the line.
- **Over-length input is safe.** Sending 1000 LEDs of data to the 832-LED strip
  clamps at 4059 mA, does not crash, and the controller still reports 832.
- **Source identification works.** WLED reports `lm: "DDP"`, `lip: "<dev-host>"`.

### Realtime release (§38, §39)

After holding a frame and then ceasing transmission:

| t (s) | `live` | `pwr` |
|---|---|---|
| 0.1 – 2.2 | `True` | 4059 |
| **2.9 onward** | `False` | **5160** |

Control returns at ~2.5 s, matching `realtimeTimeoutMs`, and `pwr` returns to
**exactly** the pre-test baseline of 5160 mA — WLED's own state is restored
untouched. §39 is satisfied by ceasing transmission; no explicit release call is
required, though the fade of §38 must finish *before* transmission stops.

**Confirmed layout** (operator-verified, research.md §17): index 0 at the
**top-left**, running **clockwise**, top 266 / right 150 / bottom 266 / left 150.

### Operator decisions, 2026-09-05

**RGB24, not RGBW32.** White rendered from R+G+B alone was judged acceptable, so
the frame stays at 2493 bytes in **2 DDP packets**.

**WLED auto-white calculation stays OFF** (`RGBW_MODE_MANUAL_ONLY`). Verified
from source that `autoWhiteCalc` is applied at bus level
(`bus_manager.cpp:275, 481, 739`), downstream of `setPixelColor`, so it *would*
transform our realtime pixels. Leaving it off keeps §31's colour pipeline
unbroken: with `no-gc: true` also set, our output reaches the bus untransformed,
and our calibration is the only colour stage. If the white channel is wanted
later, the correct approach is for the plugin to send RGBW32 and compute W
itself — one extra packet per frame, and the whole colour decision stays in one
place.

**Brightness: use "Force max brightness" rather than raising master brightness.**
`arlsForceMaxBri` (Config → Sync Interfaces, form field `FB`) forces 255 only
while realtime is active (`led.cpp:68`, `udp.cpp:435`); master brightness returns
for the user's own effects when the realtime lock expires. This cleanly separates
"how bright are my effects" from "how bright is Ambilight", and means the plugin
never has to touch master brightness. The first-run check should recommend it
when it detects `maxbri == false` with `bri < 255`.

**Not yet verified — requires the operator:** visual impact of ABL dimming on the
all-white calibration pattern.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| Hyperion raw RGB (port 19446) | ~490 LED ceiling, no offset field. This is the defect §13 exists to fix. |
| E1.31 / sACN as default | 170 LEDs per universe → 5 universes for 832; more packets and more overhead than DDP for the same result. |
| Art-Net as default | Same universe overhead; DDP is the better fit for a contiguous pixel array. |
| WebSocket / JSON API per frame | Far too much overhead for 30 fps realtime; the JSON API is for setup and state, not pixels. |
| One datagram per frame regardless of size | Would silently cap at ~490 LEDs or rely on IP fragmentation. Forbidden by §15. |

---

## Amendment 1 — 2026-09-05 — pending operator review

**The `hw.led.fps` headroom claim is refuted.** The survey table above reads
`hw.led.fps 42` as meaning "our ~40 fps ceiling fits under the strip's refresh
budget". `WLED_FPS` drives `strip.service()`, which is **skipped while realtime
mode is active** (`wled00/wled.cpp:131-155`). It is not the gate. The actual
cadence limit on the DDP path is a 15 ms guard (`wled00/udp.cpp:475`), i.e.
roughly 66 fps — and that is conditional on `if.live.mso` being false, which was
never surveyed on the reference controller.

The conclusion is unchanged and in fact safer: 40 fps fits. The reasoning was
wrong.

**The realtime timeout is a configurable value, not a constant.** This ADR, and
ADR-003 and ADR-007 which cite it, treat 2500 ms as fixed. It is
`if.live.timeout` in units of 100 ms, settable up to **650 (65000 ms)** through
both the settings form and `/json/cfg`. Every design that derives a duration from
it must read it from the controller rather than assume the default.

**Four more first-run hazards.** The §61 hazard table lists `DMXAddress`,
`arlsOffset` and `realtimeOverride`. Four further settings silently transform or
suppress realtime output and none were surveyed:

| Setting | Effect |
|---|---|
| `if.live.timeout == 650` | 65 s realtime lock; explicit release becomes mandatory |
| `if.live.rlm` | realtime honours the active ledmap — would re-route our indices |
| `light.scale-bri` | a third brightness scaler (`led.cpp:59-61`) |
| `if.live.mso` | main-segment-only; also decides whether the fps gate above applies |

---

## Amendment 2 — 2026-09-05 — firmware 16.0.1

The operator updated the controller from **16.0.0** (build 2605030) to
**16.0.1** (build 2606300) mid-project. Re-surveyed read-only, and the DDP
behaviour re-measured objectively.

### Protocol behaviour is unchanged — verified, not assumed

| LEDs lit | Packets | Measured (mA) | Model | Error |
|---|---|---|---|---|
| 1 | 1 | 953 | 953.0 | +0.0 |
| 100 | 1 | 1189 | 1188.8 | +0.2 |
| 480 | 1 | 2094 | 2093.9 | +0.1 |
| **481** | **2** | 2096 | 2096.3 | −0.3 |
| 490 | 2 | 2118 | 2117.8 | +0.2 |
| 491 | 2 | 2120 | 2120.1 | −0.1 |
| **831** | **2** | 2930 | 2930.0 | +0.0 |

`pwr = 950.6 + 2.3819 × n_lit`, maximum error **0.3 mA**. No truncation, correct
fragmentation across the one-to-two-packet boundary, no discontinuity at 490/491,
all 831 LEDs addressed. The firmware update is a non-event for the protocol.

### A firmware update silently reverted an operator decision

Two settings changed without the operator touching them:

| Setting | 16.0.0 | 16.0.1 | Consequence |
|---|---|---|---|
| bus `rgbwm` | `0` (`RGBW_MODE_MANUAL_ONLY`) | **`2` (`RGBW_MODE_AUTO_ACCURATE`)** | auto-white calculation is now **ON** |
| `state.bri` | 128 | **80** | realtime output now scaled to 31% |

The `rgbwm` change directly contradicts the decision recorded in Amendment 1
("WLED auto-white calculation stays OFF"). WLED now derives the white channel
from our RGB and subtracts it from RGB — a colour transform between our
calibration and the LEDs, which is exactly what §31 requires us not to have.

**This is the strongest possible evidence for the first-run check.** Controller
state does not merely differ from expectations at install time; it can change
underneath a working installation, and a *firmware update* is enough to do it.
The plugin must re-verify controller state on every session start, not once at
setup, and report drift rather than silently producing wrong colour.

### An accidental confirmation

The slope fell from 3.7353 to 2.3819 mA/LED, a factor of 0.638, tracking the
brightness change 80/128 = 0.625. Master brightness therefore demonstrably scales
realtime output — previously known only from reading `led.cpp:68`, now measured.
The intercept was unchanged (951.3 → 950.6): standby current is brightness-independent.

### `info.leds.lc` must never be used to detect RGBW

`lc` went 3 → 1 and `wv` 2 → 0, which looks like the strip changed type. It did
not. `FX_fcn.cpp:1039-1044`:

```cpp
bool whiteSlider = (aWM == RGBW_MODE_DUAL || aWM == RGBW_MODE_MANUAL_ONLY);
// if auto white calculation from RGB is active (Accurate/Brighter), force RGB controls
if (!whiteSlider) capabilities |= SEG_CAPABILITY_RGB;
```

`lc` describes **which UI controls to show**, derived from the auto-white mode —
not what the hardware is. With auto-white active there is no manual white slider,
so the white capability bit is dropped even though the strip is still SK6812
RGBW. A plugin reading `lc` to decide RGB24 versus RGBW32 would flip its wire
format because a user changed an unrelated setting. **Read `hw.led.ins[].type`
instead.**

### Amendment 2, continued — `maxbri` enabled and verified

The operator restored `rgbwm` to 0 and authorised direct configuration changes.
`if.live.maxbri` was set to `true` via `POST /json/cfg` with the minimal body
`{"if":{"live":{"maxbri":true}}}`; a full-config diff before and after confirms
**exactly one field changed**. The pre-change configuration is backed up.

Verified empirically rather than trusted. With `state.bri` still at 80:

| Pattern | Slope (mA/LED) | 831 LEDs |
|---|---|---|
| `maxbri` off, `bri` 80 | 2.3819 | 2930 mA |
| `maxbri` off, `bri` 128 | 3.7353 | 4059 mA |
| **`maxbri` on, `bri` 80** | **7.5000** | **7183 mA** |

Predicted for a forced 255: `3.7353 × 255/128 = 7.4414`. Measured **7.5000** —
within 0.8%. `maxbri` forces full brightness during realtime while leaving the
user's own effects at 80, which is exactly the separation §55 wants and means the
plugin never needs to touch master brightness.

### ABL does not engage — the earlier claim was wrong

| 831 LEDs, forced full brightness | Draw |
|---|---|
| all red / all green / all blue | 7.18 A each |
| **all white** | **19.65 A** |

Against a 40 A cap. The survey's point 4 above estimated ~60 mA/LED and concluded
ABL would dim the white calibration pattern. WLED's own model uses the bus
`ledma: 30`, giving a theoretical ceiling of 831 × 30 = 24.9 A — still under the
cap. **The §47 all-white pattern renders at full brightness and needs no
explanatory note.** Q4 is closed without requiring visual confirmation.
