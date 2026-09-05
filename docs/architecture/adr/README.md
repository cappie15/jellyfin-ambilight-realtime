# Architecture Decision Records

Required by master prompt §83. Each ADR records one decision, why it was made,
what it costs, and what was rejected. An ADR is **Proposed** until the operator
has reviewed it; only **Accepted** ADRs may be treated as settled.

| ADR | Title | Core decision | Status |
|---|---|---|---|
| [001](ADR-001-server-side-realtime-analysis.md) | Server-side realtime analysis | Analyse on the server in realtime; never capture the client, never precompute | **Accepted** 2026-09-05 + **Amendment 1** pending |
| [002](ADR-002-playback-device-centric-mapping.md) | Playback-device-centric mapping | Configuration is owned by `DeviceId`, never by a user | **Accepted** 2026-09-05 + **Amendment 1** pending |
| [003](ADR-003-wled-only-v1.md) | WLED-only v1 | One output driver behind a narrow `ILedOutput` abstraction | **Accepted** 2026-09-05 + **Amendment 1** pending |
| [004](ADR-004-wled-realtime-protocol-strategy.md) | WLED realtime protocol strategy | Multi-packet DDP; Hyperion raw RGB and its ~490 LED ceiling abandoned | **Accepted** 2026-09-05 + **Amendment 1** pending |
| [005](ADR-005-logical-sampling-vs-physical-led-interpolation.md) | Logical sampling vs physical LED interpolation | ~100 logical samples interpolated in linear light to 831 physical LEDs | **Accepted** 2026-09-05 + **Amendment 1** pending |
| [006](ADR-006-hdr-dolby-vision-normalization.md) | HDR/Dolby Vision normalization | GPU tone-mapping to linear-light BT.709; per-profile DV handling, P5 documented | **Accepted** 2026-09-05 |
| [007](ADR-007-jellyfin-playback-synchronization.md) | Jellyfin playback synchronization | Local monotonic clock corrected by push events; seeks detected by drift | **Accepted** 2026-09-05 + **Amendment 1** pending |
| [008](ADR-008-companion-worker-vs-in-process.md) | Companion worker vs in-process | In-process, spawning `IMediaEncoder.EncoderPath`; no daemon, no bundled binary | **Accepted** 2026-09-05 |
| [009](ADR-009-zero-touch-installation.md) | Zero-touch installation architecture | Plugin repository only; nothing downloaded; capability gaps explained, not fixed | **Accepted** 2026-09-05 + **Amendment 1** pending |

Seven ADRs carry an **Amendment 1** dated 2026-09-05, raised by a nine-topic
adversarial research pass and awaiting operator review. The base decisions all
survived; the amendments correct supporting reasoning, one genuine correctness
defect (ADR-007a: freeze as specified releases control), and several claims that
turned out to describe mechanisms Jellyfin does not have.

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
