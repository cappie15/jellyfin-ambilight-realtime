# ADR-002 — Playback-device-centric mapping

- **Status:** **Accepted** — operator review 2026-09-05
- **Date:** 2026-09-05
- **Relates to:** master prompt §8, §9, §10, §12, §44, §60
- **Evidence:** [research.md](../research.md) §4

## Context

An Ambilight installation is a physical fact: a strip of LEDs is screwed to the
back of one particular television. That relationship does not change when a
different family member logs into that television.

Jellyfin's session model offers several possible keys. `PlaybackProgressEventArgs`
exposes `Users`, `DeviceId`, `DeviceName`, `ClientName`, `PlaySessionId` and
`Session`. Of these, `PlaySessionId` is per-playback and `Users` is per-account —
neither survives the thing we are modelling. `DeviceId` is stable for the
physical client installation and is the only field that matches the physical
reality.

The predecessor plugin already reached this conclusion:
`AmbilightPlaybackService.ResolveWledTargets(SessionInfo)` keys its mapping on the
device, supports several WLED targets per device, and de-duplicates hosts. That
part of its design is sound and worth keeping.

## Decision

**Ambilight configuration is owned by a Jellyfin playback device, identified by
`DeviceId`. It is never owned by a user.**

The model is:

```
Jellyfin Playback Device  (DeviceId)
        └── Ambilight Setup                (0..1 per device)
                ├── WLED Device A          (1..n per setup)
                ├── WLED Device B
                └── Logical LED Layout     (one perimeter across all controllers)
```

Rules for v1:

- A playback device has **at most one** Ambilight Setup.
- A Setup contains **one or more** WLED devices.
- A WLED device belongs to **exactly one** Setup. No sharing between devices (§10).
- All WLED devices in a Setup together form **one logical perimeter**. The video
  is decoded and analysed **once**, producing one logical frame, which is then
  split across controllers and transmitted concurrently to minimise skew (§12).

**Session scope (§9):** exactly **one active Ambilight session per Jellyfin
server** in v1. If a second eligible session starts while one is active, the
running session keeps the Ambilight; the new session is ignored and the decision
is logged at INFO. This rule is deliberately trivial — no room priority, no
arbitration, no preemption. The internal architecture keeps the active session in
a replaceable holder so multi-session support remains possible later, but no
effort is spent on it now.

Configuration is strongly typed and versioned, with migrations (§60). Device
bindings are stored as `PlaybackDeviceBinding` records keyed by `DeviceId`, never
as free-form JSON strings.

## Consequences

**Positive**

- Matches physical reality; survives account switching, guest accounts and
  multi-user households with no special handling.
- `DeviceId` arrives directly on every playback event, so binding lookup is a
  dictionary hit with no extra Jellyfin queries (§52: no high-frequency polling).
- One decode serves all controllers on a TV, which is what makes multi-controller
  setups cheap (§12).
- The single-session rule removes an entire class of concurrency bugs from v1.

**Negative**

- `DeviceId` changes if the user reinstalls the client or clears its data,
  orphaning the binding. The wizard must make re-binding easy, and Diagnostics
  should show "last seen" so a stale binding is visible.
- One TV cannot have two independent Ambilight setups. Accepted; not a real use
  case.
- Two TVs cannot share one WLED controller in v1. Accepted per §10.
- With one session per server, a second household member starting a movie gets no
  Ambilight and only a log line. This is a deliberate v1 simplification and must
  be stated in the documentation so it does not read as a bug.

## Operator decision, 2026-09-05

**Second-session rule: first session wins.** When Ambilight is already running
and a second bound device starts playback, the running session keeps Ambilight;
the new session is ignored and the decision is logged at INFO. No notification is
raised — one log line is enough, and §9 explicitly forbids building a room
priority system in v1.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| Key on Jellyfin user | A television's LEDs do not change owner when someone else logs in. Explicitly rejected by §8. |
| Key on `PlaySessionId` | Lifetime is a single playback; cannot hold persistent configuration. |
| Key on client IP address | Unstable under DHCP; and several clients may share an address. |
| Full multi-session arbitration with room priority | Explicitly out of scope for v1 (§9). Large complexity cost for a rare case. |
