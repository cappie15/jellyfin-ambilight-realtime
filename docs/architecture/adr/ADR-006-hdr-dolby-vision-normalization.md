# ADR-006 — HDR/Dolby Vision normalization

- **Status:** **Accepted** — operator review 2026-09-05. Colour *correctness* on the reference asset still requires visual confirmation (research.md §17).
- **Date:** 2026-09-05
- **Relates to:** master prompt §30, §31, §32, §55, §56, §72
- **Evidence:** [research.md](../research.md) §5, §7, §8

## Context

§30 makes HDR correctness a hard requirement, and §31 states the goal precisely:
the LEDs should approximate **the colours the viewer perceives**, not the numbers
in the file. Sending raw PQ-encoded values to RGB LEDs is explicitly forbidden.

PQ (SMPTE ST 2084) is an absolute transfer function covering 0–10000 nits over a
BT.2020 gamut. An RGB LED strip is a relative, roughly BT.709-ish device. Passing
PQ code values through unconverted yields washed-out, desaturated, badly
mismatched light — a bright HDR highlight and a mid-grey can land at similar
values.

Jellyfin already solves an adjacent problem for transcoding, and its approach is
worth copying: **capability probing at runtime**, not compile-time assumptions.
`EncodingHelper.cs` gates every tone-mapping path behind
`_mediaEncoder.SupportsFilter(...)` — `tonemap_vaapi` (line 279), `libplacebo`
(line 319), `tonemapx` (line 341).

The installed jellyfin-ffmpeg 7.1.3 is built `--enable-libplacebo --enable-vulkan
--enable-vaapi --enable-libvpl --enable-opencl` and offers `libplacebo`,
`vpp_qsv`, `tonemap_vaapi`, `tonemap_opencl` and software `tonemapx`.

## Decision

**Every source is normalised to a common linear-light BT.709 working space before
edge sampling. Colour handling is selected at runtime by capability probe, per
source type.**

```
encoded video → decode → source colour interpretation
              → DV metadata processing where applicable
              → HDR tone/gamut mapping → linear-light analysis
              → LED calibration → physical RGB
```

Tone mapping is performed **by the FFmpeg filter graph on the GPU**, before
download to system memory. We do not write our own tone mapper: §31 says not to
invent a simplistic one when mature implementations exist, and doing the
conversion on the GPU also serves ADR-005's bandwidth goal.

### Per-range handling

`Jellyfin.Data/Enums/VideoRangeType.cs` already classifies sources usefully, and
we key off the same enum:

| `VideoRangeType` | Profile | Handling |
|---|---|---|
| `SDR` | — | interpret BT.709, linearise, sample |
| `HDR10` | — | PQ → tone map → BT.709 linear |
| `HLG` | — | HLG → tone map → BT.709 linear |
| `HDR10Plus` | — | dynamic metadata ignored; treated as HDR10 base. **Documented as such** |
| `DOVIWithHDR10` | P8.1 | tone-map the HDR10 base layer; RPU ignored |
| `DOVIWithHLG` | P8.2/8.4 | tone-map the HLG base layer |
| `DOVIWithSDR` | P8.4-style | treat base as SDR |
| `DOVIWithEL` | P7 | tone-map the HDR10 base layer, discard the enhancement layer |
| `DOVI` (no cross-compat) | **P5** | ⚠ hard case — see below |
| `DOVIInvalid` | — | fall back to HDR10 interpretation, log WARN |

### Dolby Vision Profile 5

P5 is the genuine problem. Its base layer is IPT-PQ-C2 and there is **no
SDR- or HDR10-compatible interpretation**; decoding it without applying the RPU
produces the well-known green/purple cast. Upstream is candid about the limits —
`EncodingHelper.cs:391` carries the comment:

> `// libplacebo has partial Dolby Vision to SDR tonemapping support.`

Per §32, where a DV profile cannot be reproduced completely we use the best
technically valid fallback and **document it explicitly**. Concretely:

1. Detect P5 from stream metadata.
2. If libplacebo's DV support handles it on this hardware — verified by test, not
   assumed — use it.
3. If not, apply a documented approximate IPT→BT.709 correction, surface
   `HDR: Dolby Vision P5 (approximate)` in Diagnostics, and say so in the docs.
4. **Never silently process P5 as ordinary SDR** (§32).

§30 forbids claiming support for untested profiles, so the compatibility table
ships listing only what was actually tested on real media (§72).

### Black level (§56)

Common Ambilight implementations fail in dark scenes: grey LEDs on black video,
flickering shadows, exaggerated blue near black. Handling is a black threshold
plus noise suppression, applied **in linear light**, tuned not to crush
legitimate dark colours. HyperHDR keeps anti-flickering as a concern separate
from smoothing, and we do the same.

## Consequences

**Positive**

- Colours are perceptually correct across SDR, HDR10, HLG and the DV profiles
  with a usable base layer.
- Tone mapping on the GPU costs little and happens before the bandwidth
  bottleneck.
- Linear-light working space is what ADR-005's interpolation and the temporal
  smoothing both need anyway.
- Capability probing means a host without Vulkan or OpenCL degrades to software
  `tonemapx` instead of failing.

**Negative**

- The filter graph is the most hardware-dependent part of the system and the
  most likely source of "works here, not there" reports. Diagnostics must always
  show which path was selected.
- Tone mapping adds latency. It is GPU-side and small, but must be measured
  against the §41 budget rather than assumed negligible.
- DV P5 may remain approximate. This is a documented limitation, not a bug.
- HDR10+ dynamic metadata is ignored in v1.

## Validation on the reference asset (2026-09-05)

The operator could not supply a test matrix and instead named a single film to
make work first:

**`/films2/Apocalypse Z The Beginning of the End (2024)/…2160p.WEB-DL.DV.P5…mp4`**

This is the hardest case in the table above, not a soft start. `ffprobe`
confirms it from the DOVI configuration record:

| Field | Value |
|---|---|
| `dv_profile` | **5** |
| `dv_bl_signal_compatibility_id` | **0** — no cross-compatibility; the base layer is *not* viewable as HDR10 or SDR |
| `rpu_present_flag` | 1 |
| `el_present_flag` | 0 (single layer) |
| codec / pix_fmt | `hevc` Main 10, `yuv420p10le`, 3840×2160, 24 fps, 20 GB, 118 min |
| `color_space` / `color_transfer` / `color_primaries` | **all `unknown`**, `color_range=pc` |

The absent colour metadata is the P5 signature: the colour information lives in
the RPU, so a naive pipeline has nothing to key off and silently guesses.

### Result: the P5 path works, with one hard constraint

Hardware: Intel Core i5-10500T (Comet Lake), **UHD Graphics 630 (Gen9.5)**,
Mesa anv 25.0.7, jellyfin-ffmpeg 7.1.3. Vulkan initialises inside the LXC.

`libplacebo` exposes **`apply_dolbyvision <boolean> (default true)`**. Decoding
12 frames at t=2400 s down to 160×90, both ways:

| Path | mean R | mean G | mean B | G−R | B−R |
|---|---|---|---|---|---|
| **A** naive, no DV handling | 15.7 | 30.3 | 25.4 | **+14.6** | **+9.6** |
| **C** `libplacebo apply_dolbyvision=true` | 14.5 | 9.9 | 2.1 | **−4.5** | **−12.4** |

Path A is green/cyan-dominant with red lowest — the textbook P5-without-RPU
cast. Path C is warm and red-dominant, which is plausible for the scene. The two
differ enormously and in the direction the physics predicts, so **libplacebo is
genuinely applying the RPU**.

⚠ This is objective evidence that the naive path is wrong and the libplacebo
path is *different*. It is **not** proof that the libplacebo path is
*colour-correct* — that requires operator visual confirmation and is recorded as
pending in research.md §17.

### Hard constraint: download as a packed format, never multi-planar

| libplacebo output format | Result |
|---|---|
| `nv12` → `hwdownload` | **broken** — R=0, B=0, mean G=165 |
| `rgb24` | **rejected** — "Failed to configure output pad", `-95 Operation not supported` |
| `bgra` / `rgba` → `hwdownload` | **works** |

The nv12 failure is a driver limitation, not a filter-graph error. Mesa emits
`FINISHME: support more multi-planar formats with DRM modifiers` and the chroma
planes come back as zero rather than 128. In YUV→RGB that yields exactly
`R = Y − 179 → 0`, `B = Y − 227 → 0`, `G = Y + 135` — the observed signature.

**The decoder must therefore request a packed `bgra`/`rgba` frame from
libplacebo before `hwdownload`.** At the ~160×90 analysis size the extra alpha
channel costs nothing.

### Consequence for backend selection

`vpp_qsv`'s tone-map option is documented as "perform tonemapping **if the input
has HDR metadata**". This file has none in the container. **`vpp_qsv` would
therefore do nothing at all on the reference asset.** Combined with Jellyfin
preferring `tonemap_vaapi` over `vpp_qsv` on Gen9/KBLx (`EncodingHelper.cs:406`)
— and this host being exactly Gen9.5 — the primary colour path for DV is
**libplacebo over Vulkan**, not QSV VPP. QSV remains the decode backend; the
colour work happens in libplacebo.

**Assumptions status**

- **A2 — resolved.** iGPU identified as UHD 630 (Gen9.5). `vpp_qsv` is not
  viable for DV; libplacebo over Vulkan is the colour path.
- **A3 — resolved favourably.** DV P5 is handled by
  `libplacebo apply_dolbyvision=true`, subject to the packed-format constraint.
  Visual confirmation still outstanding.
- **New constraint** — multi-planar `hwdownload` is unusable on this
  Mesa/Gen9.5 combination.

**Colour accuracy cannot be signed off from logs.** Every claim in the §72 test
matrix requires operator visual confirmation.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| Send raw PQ values to the LEDs | Explicitly forbidden by §31; produces visibly wrong colour. |
| Write our own tone mapper | §31 forbids inventing a simplistic one when mature implementations exist. |
| Tone map on CPU after full-resolution download | Defeats ADR-005; violates §65. |
| Treat all HDR as SDR | The naive failure §31 and §32 exist to prevent. |
| Skip HDR support in v1 | §30 makes it a hard requirement. |
| Apply the DV RPU ourselves | Enormous complexity; the RPU is only partially documented; out of scope. |
