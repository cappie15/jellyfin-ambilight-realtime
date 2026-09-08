# Architecture Decision Records

Required by master prompt §83. Each ADR records one decision, why it was made,
what it costs, and what was rejected. An ADR is **Proposed** until the operator
has reviewed it; only **Accepted** ADRs may be treated as settled.

| ADR | Title | Core decision | Status |
|---|---|---|---|
| [001](ADR-001-server-side-realtime-analysis.md) | Server-side realtime analysis | Analyse on the server in realtime; never capture the client, never precompute | **Accepted** 2026-09-05 + Am. 1 accepted |
| [002](ADR-002-playback-device-centric-mapping.md) | Playback-device-centric mapping | Configuration is owned by `DeviceId`, never by a user | **Accepted** 2026-09-05 + Am. 1 accepted |
| [003](ADR-003-wled-only-v1.md) | WLED-only v1 | One output driver behind a narrow `ILedOutput` abstraction | **Accepted** 2026-09-05 + Am. 1 accepted |
| [004](ADR-004-wled-realtime-protocol-strategy.md) | WLED realtime protocol strategy | Raw RGB baseline through 490 LEDs; scalable multi-packet DDP beyond it | **Accepted** 2026-09-05 + Am. 1-2 accepted; Am. 3 records the current product requirement |
| [005](ADR-005-logical-sampling-vs-physical-led-interpolation.md) | Logical sampling vs physical LED interpolation | ~100 logical samples interpolated in linear light to 831 physical LEDs | **Accepted** 2026-09-05 + Am. 1 accepted |
| [006](ADR-006-hdr-dolby-vision-normalization.md) | HDR/Dolby Vision normalization | GPU tone-mapping to linear-light BT.709; per-profile DV handling, P5 documented | **Accepted** 2026-09-05 + Am. 1 accepted, **Am. 2 pending** |
| [007](ADR-007-jellyfin-playback-synchronization.md) | Jellyfin playback synchronization | Local monotonic clock corrected by push events; seeks detected by drift | **Accepted** 2026-09-05 + Am. 1-2 accepted |
| [008](ADR-008-companion-worker-vs-in-process.md) | Companion worker vs in-process | In-process, spawning `IMediaEncoder.EncoderPath`; no daemon, no bundled binary | **Accepted** 2026-09-05 + Am. 1 accepted |
| [009](ADR-009-zero-touch-installation.md) | Zero-touch installation architecture | Plugin repository only; nothing downloaded; capability gaps explained, not fixed | **Accepted** 2026-09-05 + Am. 1 accepted; Am. 2 pins latest stable 10.11.11 |
| [010](ADR-010-sampling-zone-model.md) | The sampling zone model | What a logical sample *is*: zone rectangles, 5% depth off the **cropped** picture, overlapping corners, linear-light box mean | **Proposed** 2026-09-06 |
| [011](ADR-011-hue-entertainment-integration.md) | Philips Hue Entertainment integration | Second, fully independent realtime output alongside WLED; DTLS Entertainment API, one paired bridge/area | **Accepted** 2026-09-08, validated against real hardware across several rounds |
| [012](ADR-012-wled-temporal-smoothing.md) | WLED temporal smoothing | Optional per-LED exponential ease toward a newly sampled colour, off by default | Implemented 2026-09-08, not yet re-tested against the physical strip |
| [013](ADR-013-sdr-analysis-hardware-decode.md) | SDR analysis hardware decode | VAAPI decode for the SDR analysis graph, matching the HDR graph's existing path; measured 0.3× → 15× realtime | Implemented and deployed 2026-09-08 |
| [014](ADR-014-rgbw-white-channel-ceiling.md) | RGBW white-channel ceiling | Cap how much of a bright pixel's grey goes to a single white LED; measured via flicker photometry that the gap never closes | Implemented and deployed 2026-09-08, not yet re-checked against real playback content |

**The first ten ADRs are Accepted (save ADR-010, still Proposed), with eleven amendments also accepted (operator review 2026-09-06); ADR-011 onward record later-session decisions.**
The amendments come from two adversarial research passes (30 agents, ~3.6M
tokens) plus measurement on the reference hardware. No base decision was
overturned, but three findings were serious:

- **ADR-006 Am.1** — Intel QSV silently drops the Dolby Vision RPU.
  `apply_dolbyvision=true` is accepted, errors nothing, and does nothing. VAAPI
  with GPU-side scaling before download is correct *and* 6.3× realtime.
- **ADR-007 Am.1** — freeze as specified releases control after the realtime
  timeout, the opposite of what §37 requires. It must be an active keepalive.
- **ADR-008 Am.1** — `-ss` after `-i` does not seek; it decodes the whole file
  from t=0, which at mid-film is a 62-minute hang indistinguishable from a
  crashed decoder, and the watchdog would respawn it into the same hang.

Background and evidence: [../research.md](../research.md).

## Cross-cutting constraint

Every ADR is subordinate to §88's priority order. Where two decisions conflict,
the higher priority wins:

1. Never damage or delay Jellyfin video playback
2. Low Ambilight latency
3. Installation simplicity
4. Reliable synchronization
5. Correct SDR/HDR/Dolby Vision colour
6. Stability
7. Low resource usage
8. Visual smoothness
9. Maximum configurability
10. Additional features
