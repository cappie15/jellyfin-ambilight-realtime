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
