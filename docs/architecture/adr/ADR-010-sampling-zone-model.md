# ADR-010 — The sampling zone model: what a logical sample *is*

- **Status:** Proposed — awaiting operator review
- **Date:** 2026-09-06
- **Relates to:** master prompt §27, §28, §29, §31, §33, §34, §44, §56, §65
- **Evidence:** measured on the reference asset through the ADR-006 chain; prior
  art read from HyperHDR `e5bda28` and jellyfin-ambilight `e64536e`

## Context

ADR-005 decided *how many* logical samples (~100 along the long edge) and that
they interpolate to physical LEDs in linear light. It never defined what a sample
**is**: how deep into the frame the band reaches, how the runs meet at a corner,
the weighting inside a zone, or what happens at a crop boundary.

An external critic called this "the one question that determines whether the LEDs
look right, and it has no ADR". This is that ADR.

Three passes of research and measurement produced one finding that dwarfs the
rest.

## The crop dominates everything else

The reference asset is hard-matted **2.39:1**: the active picture occupies rows
69–470 of a 540-row analysis frame, identical at all 111 sampled positions across
the feature.

- **Without cropping, a top band up to 12.8 % of frame height reads exactly
  `0,0,0`** — on the brightest frame in the film. Roughly **73 % of the strip
  would sit at code 0 for 118 minutes.**
- **With cropping, depth barely matters**: across the whole 12–64 px range the
  zone colour moves by ~1–2 codes.

The predecessor shipped exactly this bug: `scale=320:180` with no crop
(`AmbilightInProcessExtractor.cs:204`), and a depth keyed to *frame* height.

**Black-border detection is therefore not a refinement of the sampling model. It
is a precondition for it.** §33 already makes it required and enabled by default;
this measurement says it is load-bearing, not cosmetic.

## Decision

### 1. Colour contract

A zone mean is undefined without one, so the analysis frame's colour definition
is part of this contract: **960×540 packed BGRA, BT.709 primaries, BT.709
transfer, limited range**, produced by the ADR-006 chain with its output
explicitly pinned (ADR-006 Amendment 2).

### 2. Geometry

Frame `W×H`. Four **independent** crop insets, each named for the edge it insets
— never for an axis:

```
active     x0 = cropLeft,  x1 = W - cropRight
           y0 = cropTop,   y1 = H - cropBottom
           Wa = x1 - x0,   Ha = y1 - y0

guard      g = clamp(round(0.005 * min(Wa, Ha)), 1, 4)      -> 2 px
depth      D = clamp(round(0.050 * min(Wa, Ha)), 8, 64)     -> 20 px

sampling   X0 = x0+g, X1 = x1-g, Y0 = y0+g, Y1 = y1-g
rect P'    Wp = X1-X0, Hp = Y1-Y0
```

Half-open rectangles, `floor()` on both bounds, so consecutive zones tile exactly
with no gap and no double count. `n` runs **clockwise from top-left**, matching
the operator-verified strip order:

```
top     n in [0,Nt)   x in [X0 + n*Wp/Nt, X0 + (n+1)*Wp/Nt)   y in [Y0, Y0+D)
right   n in [0,Nr)   x in [X1-D, X1)                          y in [Y0 + n*Hp/Nr, Y0 + (n+1)*Hp/Nr)
bottom  n in [0,Nb)   x in [X1 - (n+1)*Wp/Nb, X1 - n*Wp/Nb)    y in [Y1-D, Y1)
left    n in [0,Nl)   x in [X0, X0+D)                          y in [Y1 - (n+1)*Hp/Nl, Y1 - n*Hp/Nl)
```

**Four independent insets, not two.** HyperHDR's `BlackBorder` carries one number
per axis applied symmetrically, and resolves a top/bottom disagreement by taking
the *minimum* (`BlackBorderDetector.h:7-13`). It cannot represent an asymmetric
crop. §33 requires variable aspect ratio and changing IMAX sequences, so we must
not inherit that limitation.

### 3. Sample counts are frozen at layout time

```
N_side = max(2, round(100 * L_side / L_longest))
```

Reference install: top 1.840 m → **100**, right 1.042 m → **56**, bottom 1.847 m
→ **100**, left 1.042 m → **56**. Total **312 zones**, reproducing ADR-005's
"~100 top / ~56 side" exactly. Interpolation ratios 2.65 / 2.68 / 2.66 / 2.68.

Counts derive from the **LED layout**, never from the image aspect. Deriving them
from the active picture would change the wall spacing and the interpolation ratio
at every aspect change mid-film, for no gain.

Degenerate case: if `Wp/N < 3 px`, widen each zone symmetrically about its centre
to 3 px — zones then overlap, which is ADR-005's own named smoothing fallback —
and surface it in Diagnostics. Never emit a zero-width zone.

### 4. Depth: 5 % of the **active** short edge

**Default `D = 20 px` at 960×540. Expert range 8–64 px**, expressed as the
percentage; the pixel value is derived and clamped.

Keyed to the cropped active picture, never the frame — that is the whole lesson
above. Because it keys on the active short edge, the band stays a constant
fraction of *visible picture* as the letterbox changes.

Measured sensitivity (top run, N=100, three timestamps, max-channel delta vs
D=20): D=12 → 0.40–0.54; D=27 → 0.35–0.44; D=43 → 0.92–1.25; D=64 → 1.35–2.07
codes. Stable in the 12–27 px window, drifting beyond it as the band starts
importing mid-frame content.

**One depth number, not two.** HyperHDR's separate 8 % / 5 % split resolves to
near-equal pixel depths at 16:9 (86 vs 96 px at 1080p), which suggests a
coincidence of that aspect rather than perceptual tuning; nothing in its source
or history explains the split.

### 5. Guard band: 2 px

`g = 0.5 % of the active short edge`, clamped [1,4]. Measured: bar rows max at
**1 code**, but rows 69–70 carry a real overshoot (B mean 12.90 → 8.31 → 6.57).
Its real job is insurance against a one-pixel crop error, which would scale the
linear mean by roughly −5 % of light per contaminated row at D=20. Cost is nil:
shifting the band by ±2 rows moves a zone by mean 0.30 / max 2.5 codes.

### 6. Reduction: unweighted arithmetic mean in linear light, stride 1

Uniform box kernel, no distance weighting, emitted as an unclamped linear float
triplet. This is what HyperHDR does (`ImageColorAveraging.cpp:116-133`), and the
two studies that examined falloff kernels **contradicted each other**: once
effective depth is held constant, kernel shape contributes a few percent,
inconsistently signed. A shallower flat band reaches the same edge fidelity while
reading 42 % fewer rows.

At 960×540 a top zone carries 180–200 pixels and a side zone 140–160 — enough
that the noise argument for a deeper band does not survive measurement. Cost does
not constrain this decision: even a 50 % depth is ~92 MB/s against the ~311 MB/s
the `hwdownload` already moves.

### 7. Corners overlap

Each run spans the **full** extent of P′, so each D×D corner square is read by
both adjoining runs.

This follows the mature prior art, measured rather than assumed: HyperHDR's
generator overlaps by design-omission, and on this installation's geometry top
LED 264 shares pixels with 12 of the 150 right LEDs. A `cornerGap` parameter was
designed, translated and **never implemented** there — one commented-out line and
a dangling i18n string are all that remain.

Overlap guarantees no unsampled corner region, which is the visually worse
failure. An optional crossfade over `S` samples exists as an expert setting,
**default off**.

## Consequences

**Positive**

- Every parameter has a measured justification or a named prior-art precedent.
- Resolution-independent: rectangles are computed from the active rect, so a
  change of analysis resolution needs no re-tuning.
- Interpolation ratios stay constant across aspect changes, so a crop change
  cannot alter the wall spacing.
- Asymmetric crop is representable, which HyperHDR cannot do.

**Negative**

- Zones stretch with the picture: on 2.39:1 content the side LEDs show picture
  stretched 1.35× vertically. The alternatives are worse (see taste item 4).
- Depth is wall-anisotropic: 51.5 mm top/bottom versus 38.5 mm on the sides for
  this asset. Correcting it is one line; whether it should be corrected is item 2.
- The whole model rests on the crop detector being right. A crop error of a few
  pixels is absorbed by the guard; a crop *failure* is catastrophic in exactly
  the way §33 warns about.

## Matters of taste — the operator must look at the LEDs

Each is a decision the numbers do not contain, not a measurement someone forgot.

1. **Sampling depth.** 5 % is defensible; the predecessor's working install sits
   at ~6.7 % and HyperHDR at 8 %. Measurement can show a deep band imports content
   that is not at the edge; it cannot show a deeper band isn't *preferred*. Needs
   an A/B at 2 / 5 / 10 % in one sitting.
2. **Wall-isotropic versus picture-isotropic depth.** 1.34:1 anisotropy on this
   asset, up to 1.7:1 at the library's widest.
3. **The corner crossfade.** The bottom-left joint measures at the 96th percentile
   of perimeter steps, the top-right at the 47th. Whether a persistent step at a
   fixed location reads worse than a slightly smeared corner is not measurable
   from frames. Default off is a choice, not a finding.
4. **What the side LEDs beside the letterbox bars should do.** Stretch (chosen),
   clamp to the nearest picture row, or blank them. At 3.0:1, blanking would darken
   41 % of the side strip.
5. **Uniform box versus a depth-falloff kernel** — a look, not an error.
6. **Dark-scene behaviour** (§56) is explicitly about what the room looks like.
7. **Whether ~100 logical samples is enough.** Banding is a visual defect, as
   ADR-005's own Consequences section says.
8. **All of the above is conditional on DV P5 colour being right at all.** If the
   tone-map is wrong, every zone mean here is a faithful average of the wrong
   picture.

Still unmeasured rather than taste: the sampling loop's wall-clock cost, the
960×540 figure under a concurrent Jellyfin transcode (assumption A1), and this
model on a second, brighter, more saturated title — every measurement here comes
from one dark 2.39:1 film.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| Depth as a fraction of *frame* height | Measured: the top band then sits entirely inside the letterbox bar and drives every top LED at code 0. The predecessor's bug. |
| Two depth numbers, horizontal and vertical | HyperHDR's split appears to be a 16:9 coincidence; nothing explains it. |
| Exponential falloff kernel | The two studies contradicted each other; a shallower flat band matches it for 42 % fewer rows read. |
| Corner gap or hard split | The prior art overlaps and looks right; an unsampled corner is the worse failure. HyperHDR's own gap parameter was abandoned unimplemented. |
| Sample counts derived from image aspect | Changes wall spacing and interpolation ratio mid-film at every aspect change. |
| One crop number per axis, applied symmetrically | HyperHDR's model; cannot represent the asymmetric crops §33 requires. |
