# ADR-009 — Zero-touch installation architecture

- **Status:** **Accepted** — operator review 2026-09-05
- **Date:** 2026-09-05
- **Relates to:** master prompt §4, §5, §19, §45, §54, §61, §62, §63, §75, §76, §80
- **Evidence:** [research.md](../research.md) §1, §2, §12

## Context

§4 makes installation a hard requirement, and §75 restates it as
non-negotiable: the plugin must never require SSH, shell commands, manual FFmpeg
installation, manual service creation or configuration-file editing for normal
installation and configuration.

The measure of success is §76:

> "I installed the plugin, selected my TV, selected my WLED, told it where my
> LEDs are, pressed test, and it worked."

ADR-008 already removed the hardest part of this problem: with nothing to
download and no helper to install, the remaining work is packaging and honest
capability reporting.

## Decision

**Installation is a normal Jellyfin plugin repository installation and nothing
else. The plugin performs no privileged host configuration, downloads nothing at
install time, and works fully offline afterwards.**

### Target version

Per operator decision, the plugin must work on **Jellyfin 10.11.9**, the version
running on the reference host. Packages are pinned to `10.11.9` rather than to
the newest 10.11.x, so the plugin runs on 10.11.9 and later patch releases alike.

| | |
|---|---|
| Target framework | `net9.0` |
| `Jellyfin.Controller` / `Jellyfin.Model` | `10.11.9` |
| Minimum supported server | 10.11.9 |

The next major line (`v12.0-rc*`, `net10.0`) is out of scope for v1 per §5, which
forbids spending effort on compatibility abstractions. A 12.0-compatible release
is a follow-up, and this is a deliberate decision rather than an oversight
(risk R1).

### User-facing flow (§4, §45)

```
Dashboard → Plugins → Repositories → Add repository
Catalog → Realtime Ambilight → Install → restart if Jellyfin asks
Open plugin → wizard
```

The wizard is the whole configuration surface (§45): choose television → find
WLED → LED layout → starting corner → test pattern → response slider → ready.
Expert settings exist but are hidden behind "Show advanced settings" (§46, §77).

### First-run environment check (§61)

On first run the plugin probes and reports, in plain language:

| Check | Source |
|---|---|
| Jellyfin version | plugin host |
| jellyfin-ffmpeg present, version | `IMediaEncoder.EncoderPath` / `EncoderVersion` |
| Hardware acceleration, QSV | `IMediaEncoder.SupportsHwaccel("qsv")` |
| Tone-mapping filters | `IMediaEncoder.SupportsFilter(...)` |
| `/dev/dri` present | filesystem probe |
| WLED reachable | JSON API |
| WLED `DMXAddress` / `arlsOffset` / `realtimeOverride` | JSON API (ADR-004) |

Rendered as:

```
✓ Jellyfin supported
✓ FFmpeg ready
⚠ Intel Quick Sync unavailable — software decoding will be used
✓ WLED reachable
```

**A missing capability is explained, never fixed by instructing the user to open
a shell.** §62 is explicit: if `/dev/dri` is unavailable in an LXC, do not attempt
to alter the Proxmox host — say hardware acceleration is unavailable and fall
back to software. Host configuration may appear in advanced troubleshooting
documentation, never in the normal setup wizard.

### Distribution (§54, §79, §80)

Updates use Jellyfin's existing plugin update infrastructure; we do not build a
separate updater. GitHub Actions restores, builds, tests, packages, computes
hashes, creates release artifacts and updates the repository `manifest.json`.
Semantic versioning.

### Offline operation (§19, §20)

After installation nothing requires the internet: playback monitoring, decoding,
HDR processing, edge analysis, synchronisation, LED mapping, WLED communication
and configuration are all local. No cloud processing, no mandatory telemetry, no
uploading of playback information, media filenames or WLED addresses. Logging
goes to Jellyfin's own infrastructure at INFO/WARN/ERROR, with optional DEBUG off
by default.

## Operator decision, 2026-09-05

**v1 targets Jellyfin 10.11.9 only. Jellyfin 12.0 support is a follow-up
release.**

§5 is explicit: target the current latest stable release, and do not introduce
compatibility abstractions. `v12.0-rc7` is not stable, and dual-targeting
`net9.0` and `net10.0` would introduce exactly the abstractions §5 forbids while
doubling the test matrix before anything works at all.

This is recorded as a deliberate decision rather than an oversight (risk R1). A
12.0-compatible release follows once 12.0 is stable and v1 has shipped.

## Consequences

**Positive**

- The §78 definition-of-done items 1–3 are satisfied structurally rather than by
  effort.
- Identical experience in LXC, Docker and bare metal, because nothing
  platform-specific is installed.
- Offline-first is free rather than engineered.
- Capability probing turns "it silently doesn't work" into a legible UI message —
  the §4 requirement to detect a missing capability and explain it.

**Negative**

- Pinning to 10.11.9 means a 12.0 release will be needed later, and 12.0 is
  already at rc7. Accepted deliberately per §5 and recorded as risk R1.
- We cannot fix a broken host environment, only report it. This is the correct
  boundary but will produce support questions that documentation must absorb.
- Hosting a Jellyfin plugin repository is an ongoing obligation for the project
  owner.
- The first-run check is only as good as its probes; a probe that passes while
  the real pipeline fails is worse than no probe. Checks must exercise the actual
  capability, not merely assert a file exists.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| Documented manual install (SSH + systemd) | Explicitly forbidden by §4 and §75; fails §76 outright. |
| Download components at install time | Adds a network dependency, checksum verification and an offline-install failure; unnecessary given ADR-008. |
| Ask the user to pre-install FFmpeg | Forbidden by §4. Jellyfin already ships one. |
| Target the newest stable (10.11.11) | The reference host runs 10.11.9; operator decision is to work from 10.11.9. |
| Target 12.0-rc | Not stable; §5 says target the current latest **stable** release. |
| Support 10.10.x as well | §5 forbids compatibility abstractions for obsolete releases. |
