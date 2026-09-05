# ADR-005 — Logical sampling vs physical LED interpolation

- **Status:** **Accepted** — operator review 2026-09-05
- **Date:** 2026-09-05
- **Relates to:** master prompt §26, §27, §28, §29, §31, §44, §65
- **Evidence:** [research.md](../research.md) §5, §6

## Context

The reference installation has 831 LEDs. The naive design samples the video once
per LED — 831 sample regions per frame, 30 times a second.

That work is almost entirely wasted, and the measured geometry of the reference
installation makes the point far more sharply than §27's generic example.

Operator-confirmed 2026-09-05: the strip is **144 LEDs/m**, not the 60 LEDs/m
§27 uses as its typical case. Combined with the verified side counts:

| Side | Index range | LEDs | Length at 144/m |
|---|---|---|---|
| top | 0 – 264 | 265 | 1.840 m |
| right | 265 – 414 | 150 | 1.042 m |
| bottom | 415 – 680 | 266 | 1.847 m |
| left | 681 – 830 | 150 | 1.042 m |

Total **831** LEDs — see research.md §17 for how the controller's reported 832
was found to be wrong.

That is a ~1.84 x 1.05 m perimeter — aspect ratio 1.76, essentially 16:9, and a
2.12 m diagonal, i.e. roughly an **83-inch panel**. §27's "very large 85-inch
16:9 TV" is almost exactly this installation.

The one-LED asymmetry (top 265 vs bottom 266) is what
a hand-mounted strip actually looks like, and are a useful reminder that the
layout model must not assume opposite sides are equal.

**LED pitch is 6.94 mm.** At that density adjacent LEDs are unambiguously
sampling the same image content: at any realistic viewing distance the eye
cannot resolve neighbouring LEDs as separate sources, and a 3840-pixel-wide
source offers no meaningful colour variation at 7 mm intervals along its edge.
The image simply does not carry 265 independent colours along its top edge.
§27 puts the useful ceiling at roughly **100 samples along the long edge**;
against 265 physical top LEDs that is an interpolation ratio of 2.65:1, or one
logical sample every ~18 mm.

Meanwhile §26 is explicit that latency dominates: "a fast colour approximation at
the correct moment is preferable to a perfect colour calculation arriving 400 ms
late."

## Decision

**Analysis resolution is decoupled from physical LED count. The pipeline samples
a logical perimeter of ~100 points on the long edge, then interpolates up to the
physical LED count.**

```
VIDEO  →  LOGICAL EDGE SAMPLES  →  COLOUR PROCESSING
       →  INTERPOLATION  →  PHYSICAL LED FRAME  →  WLED
```

Two distinct concepts, kept separate throughout the codebase and the
configuration model:

| | Logical | Physical |
|---|---|---|
| Meaning | image sampling grid | LEDs on the wall |
| Reference case | ~100 top / ~56 side | 266 top / 150 side |
| Owned by | `EdgeSampler`, `SamplingConfiguration` | `LayoutMapper`, `LedLayout` |
| Driven by | image content and aspect ratio | the user's installation |

Analysis frames are produced at low resolution **by the decoder**, on the GPU,
before any transfer to system memory (§65: "do not decode full-resolution RGB
frames on CPU if a lower-resolution GPU path can provide the needed data"). A
~160×90 analysis frame from a 3840×2160 source is a bandwidth reduction of
roughly three orders of magnitude per frame.

**Interpolation happens in linear light, not in gamma-encoded RGB** (§29, §31).
Interpolating gamma-encoded values between two samples produces a visibly dark,
desaturated midpoint; in linear light the ramp is physically correct. The colour
pipeline is already in linear light at this stage (ADR-006), so this is free.

The v1 method is **linear interpolation in linear light**, chosen for cost. Higher
order methods are not obviously better here: the input is a smooth low-frequency
signal and cubic interpolation can overshoot into clipping on hard edges. If
banding proves visible, the fallback is a mild smoothing kernel over the logical
samples before interpolation rather than a more expensive interpolator.

The exact logical sample count is a **starting value, not a conclusion** (§27:
"the exact optimum should be determined experimentally"). It is an expert-mode
setting.

## Operator decision, 2026-09-05

**Logical sample count: ~100 along the long edge**, following §27 directly.
Against the reference installation's 265 top LEDs that is one sample every
18.4 mm, an interpolation ratio of **2.65:1**, and an analysis frame of roughly
160x90. This is the lowest-latency setting, which is what §26 asks for when
latency and colour precision conflict.

§27 also says the optimum should be determined experimentally, so this remains an
expert-mode setting and may be revisited against a real A/B test.

## Consequences

**Positive**

- Per-frame sampling cost is fixed by the analysis grid, not by LED count. An 831
  LED installation costs the same to analyse as a 100 LED one.
- Decoder workload, memory bandwidth, sampling work and network preparation all
  drop together — the four costs §27 names.
- Directly serves §26: less work per frame means lower latency.
- Adding LEDs to the wall never slows the pipeline.

**Negative**

- Genuine fine detail below the sampling grid is lost. Accepted deliberately:
  §26 ranks latency above precision, and that detail is not perceptible at
  viewing distance.
- Two coordinate systems to keep straight, which is a real source of off-by-one
  bugs — especially at corners and across multi-controller boundaries. Mitigated
  by the §68 edge-mapping tests (top red / right green / bottom blue / left
  yellow) and the §47 chase calibration pattern.
- Interpolation quality becomes a visible property. Banding is a **visual**
  defect: it cannot be signed off from logs and requires operator confirmation.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| One sample region per physical LED | 831 sample regions per frame for no perceptible gain; explicitly rejected by §27–§28. |
| Analyse at full source resolution | Enormous memory bandwidth; forces a full-resolution GPU→CPU transfer; violates §65. |
| Fixed analysis resolution regardless of aspect ratio | Distorts sample spacing on non-16:9 content and interacts badly with black-bar cropping. |
| Cubic / spline interpolation | Higher cost, risk of overshoot and clipping, no perceptible benefit on a smooth low-frequency signal. |
| Interpolate in gamma-encoded RGB | Cheaper but produces dark, desaturated midpoints between samples. |

---

## Amendment 1 — 2026-09-05 — pending operator review

**a. The pipeline diagram has no place for three necessary stages.** As drawn it
is `VIDEO → LOGICAL EDGE SAMPLES → COLOUR PROCESSING → INTERPOLATION → PHYSICAL
LED FRAME → WLED`. Missing: temporal smoothing, the anti-flicker deadband, and —
on an SK6812 RGBW strip — RGBW extraction. Their **ordering is not a free
choice**: a deadband applied before extraction does not stop the white channel
dithering, because W is a nonlinear function of R, G and B. HyperHDR's own DDP
driver is not self-consistent here, so there is no precedent to copy. Stage
ordering is currently owned by no ADR and needs one.

**b. The linear-light justification was wrong, though the decision stands.** The
text says interpolation in linear light "is free" because the pipeline is already
there. It is not: in HyperHDR, `srgbLinearToNonlinear` sits in the *middle* of the
per-colour chain (`InfiniteProcessing.cpp:192`), with gamma, brightness,
saturation, minimal backlight and the power limit all operating in **non-linear**
space. Interpolating in linear light remains correct — it is the only way to avoid
dark, desaturated midpoints — but it costs an explicit conversion, and the implied
HyperHDR precedent does not exist.

**c. No crop term.** Black-border detection can produce an asymmetric crop, where
top and bottom are inset by different amounts. The logical/physical table has no
crop concept at all, so the top and bottom LED runs would sample from different
insets with nothing modelling it.

**d. What a sample *is* remains undefined.** This ADR specifies *how many* logical
samples (~100) but never what one is: depth of the sampling band into the frame,
corner overlap between the top run and the side runs, the weighting kernel, and
behaviour at a crop boundary. "Which pixels belong to logical sample *n*" is the
question that decides whether the LEDs look right, and it is currently unanswered
by any ADR.
