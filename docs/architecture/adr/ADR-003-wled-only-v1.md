# ADR-003 — WLED-only v1

- **Status:** **Accepted** — operator review 2026-09-05
- **Date:** 2026-09-05
- **Relates to:** master prompt §11, §18, §37, §38, §39, §58
- **Evidence:** [research.md](../research.md) §10

## Context

Ambilight output could target many ecosystems: WLED, Hyperion, HyperHDR, Home
Assistant, Philips Hue, ESPHome, MQTT, or a bespoke LED daemon. Each additional
target multiplies the test matrix, and several of them are themselves whole
systems the user would have to install and configure first.

The reference installation is a single WLED controller driving 832 LEDs across
four physical outputs. The stated product goal (§11) is that owning a correctly
configured WLED controller should be *sufficient* — nothing else required.

## Decision

**WLED is the only output target implemented in v1**, behind a small output
abstraction that keeps future drivers possible.

```csharp
public interface ILedOutput
{
    Task InitializeAsync(CancellationToken ct);
    Task SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct);
    Task FreezeAsync(CancellationToken ct);
    Task FadeToBlackAsync(TimeSpan duration, CancellationToken ct);
    Task ReleaseAsync(CancellationToken ct);
}
```

The single v1 implementation is `WledOutput`. The five verbs are not arbitrary —
they are exactly the lifecycle the master prompt specifies:

| Verb | Requirement |
|---|---|
| `InitializeAsync` | capability probe and protocol selection (§16), remember prior state (§39) |
| `SendFrameAsync` | realtime frame transmission (ADR-004) |
| `FreezeAsync` | pause holds the current colours; does **not** black out, does **not** hand control back (§37) |
| `FadeToBlackAsync` | stop dims smoothly over ~2–3 s before releasing (§38) |
| `ReleaseAsync` | clean return of control to WLED (§38, §39) |

The pipeline above this interface — decode, tone-map, sample, interpolate,
smooth, schedule — is entirely protocol-agnostic and produces a logical RGB
frame. Only `WledOutput` knows about UDP, DDP or packet offsets.

**The plugin is a realtime data source, not a WLED configuration manager (§39).**
It must never persistently modify presets, effects, brightness configuration,
segment configuration or device configuration.

HyperHDR support is explicitly deferred and must not delay v1.

## Operator decision, 2026-09-05

**Fade-to-black on stop: 2.5 seconds**, expert-configurable.

The value is not arbitrary. It sits mid-range in §38's "2–3 seconds" and it
coincides with WLED's measured realtime timeout of **2500 ms**
(`realtimeTimeoutMs`, `wled.h:432`), so the fade finishes at the same moment WLED
would reclaim control anyway. That avoids both failure modes:

- a shorter fade leaves the strip black for the remainder of the timeout before
  WLED resumes its own state, which is visible if the user's own effect is bright;
- a longer fade would be truncated by WLED reclaiming control mid-fade, and would
  require sending keepalive frames purely to hold the lock.

`FadeToBlackAsync` must therefore complete **before** transmission stops, and
`ReleaseAsync` is then satisfied simply by ceasing to send.

## Consequences

**Positive**

- Minimal dependencies: no broker, no daemon, no hub, no second application.
- One protocol to get right, which is what makes the DDP work in ADR-004
  affordable.
- Test matrix stays tractable; §69's LED-count matrix can be exercised properly.
- Adding a driver later touches one file and does not disturb the analysis
  pipeline.

**Negative**

- Users on Hyperion/HyperHDR/Hue get nothing in v1. Accepted per §11.
- The abstraction is designed against a single implementation, so it may need
  adjustment when a second driver arrives. Mitigated by keeping the interface
  narrow and frame-shaped rather than protocol-shaped.

**Failure behaviour (§18), which the abstraction must accommodate**

A WLED failure may never affect video playback. On loss of reachability: stop
transmitting, retry in the background with bounded backoff, log a WARN, and raise
**exactly one** Jellyfin notification per outage — not one per frame. On recovery,
resume automatically if playback is still active.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| Ship WLED + HyperHDR in v1 | Doubles protocol, calibration and test surface; §11 says do not delay v1 for it. |
| Output via Home Assistant / MQTT | Requires the user to run a broker or hub; contradicts the minimal-dependency goal. |
| No abstraction, WLED calls inline | Would entangle protocol details with the analysis pipeline and make §58's future drivers a rewrite. |
| A large plugin-style driver framework | Over-engineering for one implementation. |

---

## Amendment 1 — 2026-09-05 — pending operator review

Three corrections, one of them to a claim this ADR asserts as settled.

### a. `ReleaseAsync` — there *is* an explicit release

The text above says release "is then satisfied simply by ceasing to send". That
is the crash-safety fallback, not the design. WLED exposes an explicit release:
`POST /json {"live":false}` calls `exitRealtime()`
(`wled00/json.cpp:457`; the function is `wled00/udp.cpp:439`, whose only other
caller is the timeout at `:483`).

Using it matters for three reasons:

- it removes the 2.5 s dead tail between fade-end and control return;
- it is the **only** escape when a user has set `if.live.timeout` to its 65000 ms
  maximum, which otherwise means a near-permanent realtime lock;
- `exitRealtime()` calls `strip.show()` (`udp.cpp:449`), pushing one frame at the
  *restored* brightness — a possible single-frame flash we must test for.

This also needs a distinction the ADR does not draw: a `/json` **state** write is
not a `/json/cfg` **config** write. §39 forbids the latter. No cfg write is ever
required — the DDP listener is unconditional.

### b. "Exactly one Jellyfin notification per outage" presupposes a subsystem
that does not exist

At 10.11.x there is no `INotificationManager`, no `INotificationService`, no
`ServerEvent`. Two channels exist and both break that sentence:

- `IActivityManager` writes a durable admin activity-log row with **zero**
  deduplication — `CreateAsync` inserts unconditionally. "Exactly one per outage"
  becomes entirely a plugin-side edge-triggered state machine.
- `ISessionManager.SendMessageCommand` puts a toast in front of the **viewer**,
  which this ADR's own opening line and §88 priority 1 argue against.

**Amendment.** Outage reporting is: a WARN log line, one edge-triggered activity-log
entry per outage transition, and a status field on the diagnostics endpoint. No
viewer-facing toast. If `Jellyfin.Database.Implementations` proves unavailable as a
package, the activity-log entry degrades to the log line alone.

### c. `ILedOutput` is two verbs short

- **`KeepAliveAsync`** — see ADR-007 Amendment 1a. Freezing by ceasing to transmit
  releases control after the realtime timeout, which is the exact opposite of what
  §37 requires. A frozen or paused output must keep re-sending its last frame.
  HyperHDR does precisely this, re-sending on the first tick at or after 1000 ms.
- **a health/state surface** — required by the outage state machine in (b) and by
  the §66 metrics the diagnostics page must expose.

### d. The 2.5 s fade rationale is narrower than stated

The value survives for the reference controller (`if.live.timeout: 25`), but the
supporting argument — that a longer fade would be truncated — assumed 2500 ms is
universal. It is a **per-controller setting**. With an explicit release (a), fade
duration and the realtime timeout decouple entirely and the coincidence becomes
decorative rather than load-bearing. The default stays 2.5 s.
