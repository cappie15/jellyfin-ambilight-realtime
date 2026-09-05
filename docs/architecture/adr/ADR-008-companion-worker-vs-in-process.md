# ADR-008 — Companion worker vs in-process implementation

- **Status:** **Accepted** — operator review 2026-09-05
- **Date:** 2026-09-05
- **Relates to:** master prompt §23, §24, §25, §52, §53, §57, §59, §63, §64, §70
- **Evidence:** [research.md](../research.md) §2.1, §5, §6, §12

## Context

§53 explicitly permits a companion service and warns against rejecting a
superior architecture merely because it needs a helper process. But §53 also
requires that the user never install it manually, and §63 requires the design to
work in standard Docker where systemd, root and package installation are all
unavailable.

The predecessor plugin chose to embed per-RID helper binaries in the DLL —
`EmbeddedBinariesResolver.cs` plus `<EmbeddedResource Include="Binaries\linux-x64\ambilight-extractor">`
for five runtime identifiers, a Rust extractor shipped inside the assembly.

**One finding changes the entire calculus.**
`MediaBrowser.Controller/MediaEncoding/IMediaEncoder.cs` exposes:

| Member | Line |
|---|---|
| `string EncoderPath` | 28 |
| `string ProbePath` | 34 |
| `Version EncoderVersion` | 40 |
| `bool SupportsHwaccel(string)` | 103 |
| `bool SupportsFilter(string)` | 110 |
| `bool SupportsFilterWithOption(FilterOptionType)` | 117 |

A plugin taking `IMediaEncoder` from DI can locate **the exact FFmpeg binary
Jellyfin itself uses**, and interrogate its hardware and filter support, with no
download, no bundling and no privileged configuration. On this host that is
jellyfin-ffmpeg 7.1.3, built with `--enable-libplacebo --enable-vulkan
--enable-vaapi --enable-libvpl --enable-opencl`, offering `qsv`, `vaapi`,
`opencl` and `vulkan` hwaccels.

## Decision

**The analysis pipeline runs in-process inside the Jellyfin plugin. The only
external process is jellyfin-ffmpeg, spawned as a child process using
`IMediaEncoder.EncoderPath`. No companion service, no daemon, no downloaded or
embedded helper binary.**

FFmpeg is configured to scale and tone-map **on the GPU**, download a small
analysis frame, and write raw frames to stdout, which the plugin reads and
processes. For a 4K source with a ~160×90 analysis target this keeps the
GPU→CPU transfer three orders of magnitude smaller than a full-resolution frame
(§65).

### Component structure (§57)

Modular components, not a monolithic `Plugin.cs`:

```
PlaybackMonitor → PlaybackClock → MediaResolver → Decoder → HdrProcessor
→ BorderDetector → EdgeSampler → SpatialInterpolator → TemporalProcessor
→ LayoutMapper → OutputScheduler → WledOutputDriver
```

### Decoder abstraction (§59)

```csharp
public interface IVideoAnalysisDecoder { /* ... */ }
```

v1 implements a common FFmpeg decoder with a selectable backend: **Intel QSV**
primary, **software** fallback, selected automatically by capability probe.
NVIDIA, AMD and VideoToolbox backends are explicitly **not** implemented in v1
but must remain addable without touching the pipeline above.

### Process management (§52, §70)

A child process that outlives its session is the classic failure of this design,
and §70 requires verifying that no orphan decoders remain. Therefore:

- every decoder is owned by a `CancellationToken` tied to the session;
- a watchdog detects a hung or crashed decoder and restarts it (§74: worker crash
  → restart worker, never disturb playback);
- disposal is deterministic; the process is killed, not merely signalled, if it
  does not exit;
- process exit is awaited and logged.

### Security (§64)

Configuration values never reach a shell. Arguments are passed as an argument
vector, never as a concatenated command line. IP addresses, hostnames, ports,
paths, LED ranges and device mappings are all validated. No generic
command-execution API is exposed.

## Consequences

**Positive**

- **Zero installation surface.** Nothing is downloaded, so there is no HTTPS
  fetch, no checksum verification, no version-matching, and no offline-install
  failure mode — which is what makes §19's offline-first requirement free rather
  than hard-won.
- **Identical in LXC and Docker.** No systemd, no root, no package installation,
  so §63 needs no special-casing.
- Uses the FFmpeg Jellyfin already trusts and already ships (§23).
- Cleaner licensing: the installed jellyfin-ffmpeg links **libfdk-aac**, which
  makes that binary non-redistributable under GPL terms. Invoking it as a
  separate process rather than bundling one avoids the problem entirely.
- Capability probing via `SupportsHwaccel` / `SupportsFilter` gives the §61
  first-run environment check for free.

**Negative**

- The analysis pipeline shares a process with Jellyfin, so an unhandled exception
  or a memory leak is Jellyfin's problem too. This raises the bar on §52's
  bounded queues, cancellation tokens and deterministic disposal — they become
  correctness requirements, not hygiene.
- Managed-code frame processing is slower than native. Mitigated by ADR-005: the
  per-frame work is small and fixed.
- Child-process lifetime management is genuinely error-prone and is the most
  likely source of the §70 orphan-process defect.
- We inherit whatever FFmpeg build the host has. Capability probing detects gaps;
  the UI explains them (§61) rather than failing silently.

**Assumption requiring validation**

**A4** — that spawning a child process from a plugin is unrestricted in standard
Docker deployments. Very likely, since Jellyfin does exactly this for
transcoding, but unconfirmed.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| Separate companion daemon | Needs install, lifecycle management and privilege we should not require. Impossible to install cleanly in Docker (§63). |
| systemd unit created at install time | Does not exist in Docker; requires privilege; §4 forbids making the user create services. |
| Downloaded per-platform helper binary | Adds HTTPS fetch, checksum verification (§64) and version-matching, and creates an offline-install failure that conflicts with §19. |
| Per-RID binaries embedded in the DLL | The predecessor's approach: bloats the plugin, duplicates an FFmpeg the host already has, and must be rebuilt per platform. |
| Bundle our own FFmpeg | Explicitly discouraged by §23; adds hundreds of MB and the libfdk-aac redistribution problem. |
| FFmpeg via P/Invoke instead of a child process | A crash in native code takes Jellyfin down with it. A child process is a fault boundary — which is exactly what §74 wants. |
