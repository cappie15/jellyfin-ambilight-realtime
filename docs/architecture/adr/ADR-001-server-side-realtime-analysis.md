# ADR-001 — Server-side realtime analysis

- **Status:** **Accepted** — operator review 2026-09-05
- **Date:** 2026-09-05
- **Relates to:** master prompt §1, §2, §22, §25, §26, §74
- **Evidence:** [research.md](../research.md) §3.2

## Context

Ambilight colours must come from somewhere. There are three families of source:
capture the display, precompute the whole library, or analyse the media in
realtime as it plays.

**Display capture was measured and rejected.** On the reference playback device
(TCL Google TV, Jellyfin Android TV client) every client-side capture route was
tried: Android MediaProjection, Hyperion Android Reborn, ScreenGlow, Kodi
Hyperion integration, Kodi MediaCodec Surface, scrcpy over ADB, Android
SurfaceControl mirroring, both `c2.mtk.avc.encoder` and `c2.android.avc.encoder`,
and hardware and software H.264 encoding. In every case, activating display
capture collapsed hardware video playback to roughly **1–3 fps**. Reducing
capture resolution and frame rate did not help. The failure is architectural, not
a tuning problem.

**Precomputation was measured and rejected.** The predecessor plugin
(`jellyfin-ambilight`, and the locally disabled `Ambilight Hyperion 0.1.2`) writes
a `.json` sidecar plus a `.bin` colour stream per media item. On this host that
directory holds **711 items totalling approximately 89 GB**, including
**189,334,969 bytes for a single episode**. The sidecars record
`ExtractionTopLedCount: 266`, `ExtractionRightLedCount: 150`,
`ExtractionBottomLedCount: 266`, `ExtractionLeftLedCount: 150` and
`ExtractedByPluginVersion: 0.1.2.0`. Precomputation also requires a library scan
before any new media can be used, which breaks the "play a movie, it works"
promise of §3.

HDMI capture hardware is out of scope: it reintroduces a device the user must buy
and wire, contradicting §4.

## Decision

**Ambilight analysis runs on the Jellyfin server, in realtime, as an independent
secondary consumer of the same media file the client is playing.**

The playback device is never touched. The plugin observes Jellyfin's session
events to learn *what* is playing and *where* the playhead is, then runs its own
decoder over the original media file, seeking to the playback position and
decoding only what is needed. When playback stops, decoding stops. Nothing is
written to disk.

Because analysis reads the original stream rather than the client's rendered
output, subtitles, OSD and client-side scaling are structurally incapable of
influencing LED colour (§34) — this falls out of the architecture rather than
needing to be filtered.

## Consequences

**Positive**

- The playback device keeps 100% of its decode budget. The 1–3 fps collapse
  cannot recur.
- Storage cost is negligible: bounded in-memory frame buffers, no files.
- Works with any Jellyfin client on any hardware, including clients that expose
  no capture API at all.
- New media works immediately; no scan, no extraction queue.
- Subtitles and OSD are excluded by construction.

**Negative**

- The server decodes the stream a second time, concurrently with any transcode it
  may already be running. This is the central performance risk and drives the
  hardware-acceleration and low-resolution-analysis decisions
  (ADR-005, ADR-006, ADR-008).
- Colours are derived from the *source*, not from what the panel actually shows.
  Any picture processing the TV applies is not reflected. Accepted: §31 defines
  the goal as approximating perceived colour, and the source is the best
  available proxy.
- Position must be inferred rather than observed, requiring a synchronisation
  layer (ADR-007).
- Live TV and other non-file sources need separate treatment (§21); isolated as
  a later feature.

**Non-negotiable constraint inherited from §25 and §74**

Ambilight is a strictly subordinate consumer. It may never alter the playback
stream, force a transcode, or create backpressure into Jellyfin. If the analysis
pipeline cannot keep up, **Ambilight drops frames; video never does.**

## Operator decision, 2026-09-05

**Live TV is deferred to after v1.** §21 permits Live TV "only if it can be done
cleanly without compromising the primary architecture", and it cannot: a Live TV
source has no file on disk, no stable timeline, and no dependable
`PlaybackPositionTicks` to synchronise against. That strikes at the core of
ADR-007. Movies and episodes work properly first; Live TV is isolated as a later
feature, exactly as §21 allows.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| Client display capture | Measured: playback collapses to 1–3 fps on the reference device across eight distinct implementations. |
| Precomputed colour files | Measured: ~89 GB for 711 items; requires a library scan; forbidden by §22. |
| HDMI capture hardware | Requires the user to buy and wire additional hardware; contradicts §4. |
| Hooking Jellyfin's existing transcode | Would force transcoding for Direct Play sessions, violating §25. |
