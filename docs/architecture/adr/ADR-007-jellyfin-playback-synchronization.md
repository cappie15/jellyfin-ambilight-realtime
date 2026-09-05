# ADR-007 — Jellyfin playback synchronization

- **Status:** **Accepted** — operator review 2026-09-05
- **Date:** 2026-09-05
- **Relates to:** master prompt §35, §36, §37, §38, §41, §42, §52
- **Evidence:** [research.md](../research.md) §4

## Context

ADR-001 makes the analysis pipeline an *independent* consumer of the media. The
price is that it does not inherently know where the playhead is. If the LEDs run
ahead of or behind the picture, the effect is worse than no Ambilight at all.

Jellyfin's session API offers push events, not a clock:

| Event | Payload |
|---|---|
| `PlaybackStart` | `PlaybackProgressEventArgs` |
| `PlaybackProgress` | `PlaybackProgressEventArgs` |
| `PlaybackStopped` | `PlaybackStopEventArgs` |

`PlaybackProgressEventArgs` carries `PlaybackPositionTicks`, `Item`,
`MediaSourceId`, `IsPaused`, `DeviceId` and `Session`.

**Two findings shape the design.**

First, these are push events, so §35's "do NOT constantly poll Jellyfin at high
frequency" is satisfied by subscribing. §52 reinforces this.

Second — and this is the important one — **there is no seek event**. A seek
surfaces only as a `PlaybackProgress` whose position has jumped discontinuously.
Seek handling must therefore be built on drift detection, not on an event
subscription.

The predecessor plugin arrived at the same conclusion: it converts ticks with
`/ 10_000_000.0` and applies a tolerance window before correcting, commenting that
the tolerance "covers decode/scheduling drift and clients that round position to
whole [seconds]" (`AmbilightPlaybackService.cs:245`).

## Decision

**A local monotonic `PlaybackClock` is the authority between Jellyfin updates.
Jellyfin's progress events correct it; they do not drive it frame by frame.**

On `PlaybackStart`: read `PlaybackPositionTicks`, seed the clock, start the
decoder seeked to that position.

Between updates: the clock advances from a monotonic time source (never wall
clock — NTP steps would corrupt it). The output scheduler ticks from this clock,
in the manner of HyperHDR's `SignalMasterClockTick`, decoupled from frame
arrival.

On each `PlaybackProgress`, compute drift = reported position − clock position,
and classify:

| Drift | Action |
|---|---|
| within tolerance | ignore — client rounding, not real drift |
| small | adjust scheduling; nudge the clock, do not reseek |
| large | **discontinuity**: treat as a seek |

Tolerance and threshold values are starting points to be measured, not derived.

### Seek handling (§36)

A large drift starts a **debounce window**. Further discontinuities restart it.
Only when seek activity has settled do we seek once, refill a minimal buffer, and
resume. §36 explicitly permits one or even two seconds of settling if it buys
stability — restarting FFmpeg twenty times during a scrub is the failure mode to
avoid.

During the window, output **freezes** rather than blacking out or chasing.

### Pause, resume, stop

| Event | Behaviour | Requirement |
|---|---|---|
| `IsPaused == true` | **freeze** current colours. Do not black out, do not advance, do not release control | §37 |
| resume | resynchronise to the reported position, continue | §37 |
| `PlaybackStopped` | smooth dim → black over ~2–3 s, **then** release realtime control | §38 |

The fade must complete before transmission stops: WLED's realtime lock expires
2500 ms after the last packet (`wled.h:432`), so ceasing transmission early would
hand control back mid-fade.

### Bounded queues and frame dropping (§42)

The queue between analysis and output is **bounded and small**. When full, the
**oldest** frame is discarded, never the newest. A slow decoder must never let
Ambilight drift seconds behind; it must lose frames instead. This is the §74
fail-safe principle applied to the buffer: Ambilight degrades, video does not.

## Operator decision, 2026-09-05

**During the seek debounce window the LEDs freeze on their last colour.** They do
not dim and do not black out. This is consistent with the pause behaviour §37
mandates and is the calmest of the three options. The accepted cost is that
during a long scrub the picture moves while the LEDs do not.

## Consequences

**Positive**

- Jellyfin is polled zero times; correction is event-driven (§35, §52).
- The monotonic clock gives smooth output between sparse, jittery updates.
- Drift-based seek detection needs no API that does not exist.
- Debouncing turns aggressive scrubbing from a decoder-restart storm into one
  seek.
- Bounded queues make unbounded latency growth structurally impossible.

**Negative**

- Synchronisation accuracy is bounded by the client's reporting cadence and by
  how honestly it reports position. Some clients round to whole seconds.
- Drift thresholds are a tuning problem with a real failure mode on both sides:
  too tight causes needless reseeks, too loose leaves the LEDs visibly behind.
- Freezing during the debounce window is visible during a long scrub. Judged
  better than chasing, but this is a **perceptual** judgement requiring operator
  confirmation.
- A client that stops reporting without a stop event leaves us coasting. Needs a
  staleness timeout.

**Open question**

**Q2** — the `PlaybackProgress` cadence of the TCL Android TV client is
**unmeasured**. It determines how far the clock must coast between corrections
and therefore the drift thresholds. This must be measured against the real device
before the ADR is final.

**Perceived latency and smoothing quality cannot be signed off from logs.** §41
and §66 require end-to-end measurement with the operator and a phone camera.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| Poll Jellyfin at high frequency | Explicitly forbidden by §35 and §52; still would not give frame accuracy. |
| Drive output directly from progress events | Events are sparse and jittery; output would stutter visibly. |
| Reseek on every drift | Restarts the decoder constantly; exactly the §36 failure mode. |
| Wall-clock time instead of monotonic | NTP adjustments would corrupt the playback clock. |
| Unbounded queue to smooth over jitter | Guarantees growing latency under load; forbidden by §42 and §52. |
| Black out during seek | Visually worse than freezing and contradicts the spirit of §37. |

---

## Amendment 1 — 2026-09-05 — pending operator review

### a. Freeze, as specified, does the opposite of what §37 requires

The table above says that on pause the plugin will "**freeze** current colours.
Do not black out, do not advance, **do not release control**."

ADR-004 measured that WLED returns control **2500 ms after the last packet**. A
freeze implemented by ceasing to transmit therefore releases control 2.5 s into
the pause, and the strip visibly jumps to the user's own preset — precisely what
§37 forbids. The two statements are incompatible, and the measurement that
disproves the design was already in the repository when the design was written.

**Amendment.** Freeze is an *active* state, not the absence of sending. While
frozen — pause, and the seek debounce window — the output driver keeps
re-transmitting its last frame at a low keepalive rate. HyperHDR does exactly
this, re-sending on the first tick at or after 1000 ms while the source is
active. This requires `KeepAliveAsync` on `ILedOutput` (ADR-003 Amendment 1c).
The keepalive interval must be derived from the controller's own
`if.live.timeout`, not from the 2500 ms default (ADR-004 Amendment 1).

### b. The drift loop would correct against its own extrapolation

Jellyfin synthesises its own progress events. `SessionManager.cs:930-933` calls
`session.StartAutomaticProgress(info)` when `!isAutomated`, which runs a ~1 Hz
timer that **extrapolates `PositionTicks` forward** and re-raises
`PlaybackProgress` with `IsAutomated = true` (`SessionInfo.cs:373`ff, capped at
`RunTimeTicks`).

The Context field list above does not mention `IsAutomated`, and the drift table
does not exclude those events. As written, the clock would be corrected against a
server-side extrapolation of itself: a self-referential loop that **masks** real
client drift instead of measuring it, and that would make seek detection
unreliable in exactly the case it exists for.

**Amendment.** Only `IsAutomated == false` events carry information. Automated
events are ignored for drift correction entirely. They may still be used as a
liveness signal.

This partially closes **Q2**: the 1 Hz automated floor is now proven from source,
and it is the cadence that must be *ignored*. The genuine client reporting cadence
is still unmeasured, so Q2 remains open but is correctly scoped.
