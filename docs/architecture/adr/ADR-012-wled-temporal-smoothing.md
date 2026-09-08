# ADR-012 — WLED temporal smoothing: what actually fixes visible colour "steps"

- **Status:** Implemented this session, not yet re-tested against the physical strip
- **Date:** 2026-09-08
- **Relates to:** the earlier session's `DitheredRgb24Encoder`/`DitheredRgbw32Encoder` (temporal dithering, fixes *static* banding), the reverted Hue white-channel smoothing attempt (documented in `DitheredRgbw32Encoder`'s own remarks) -- a *different* problem this ADR does not repeat the mistake of

## Context

Operator's report, from watching a real test video: colour still visibly "steps" between source and target colour, especially at low brightness, and wanted this investigated against HyperHDR's "Infinite Color Engine" and/or higher (10/12-bit) colour precision before building anything.

## Decision 1 — the problem is not bit depth

Confirmed by re-reading this project's own protocol code (`Core/Protocol/DdpPacketizer.cs`, `Core/Wled/WledRealtimeProtocol.cs`, `Core/Protocol/HyperionRawRgbPacketizer.cs`): both DDP and Hyperion Raw RGB, this plugin's only two realtime transports, are 8 bits per channel with no higher-precision realtime variant. WLED's own realtime UDP inputs are 8-bit; there is nothing on the wire this plugin's own protocol clients already use that a higher-precision *sender* could exploit. Sending 10/12-bit values from this plugin would have nothing to carry them. This is stated plainly rather than left implicit, since the operator explicitly asked for this to be checked rather than assumed.

## Decision 2 — what HyperHDR's "Infinite Color Engine" actually does

Read directly (`github.com/awawa-dev/HyperHDR`, MIT, `sources/infinite-color-engine/InfiniteSmoothing.cpp` + sibling interpolator classes), not inferred from the name. It is a temporal *interpolator*: several selectable interpolator types (Stepper, Hybrid, HybridRgb, Yuv, Rgb, Exponential), each configured with a settling time (default 200 ms), an update frequency decoupled from the incoming analysis rate (default 25 Hz, minimum 20 Hz), and for some types spring-like stiffness/damping and a maximum-luminance-change-per-frame clamp (`y_limit`) -- the same *shape* of idea as this project's own `HueNaturalLightFilter` (colour/brightness low-pass + independent rate clamp), built for Hue earlier this session, minus that filter's Hue-specific concerns (1% floor, colour-vs-brightness split). HyperHDR still drives ordinary 8-bit strips underneath this -- the smoothness comes entirely from easing the *target* value over time before any dithering/PWM step of its own, not from wire precision.

## Decision 3 — this is a different bug from the one dithering already fixed

`DitheredRgb24Encoder`/`DitheredRgbw32Encoder` (temporal error-diffusion dithering, built earlier this session) fix *static* banding: a slow fade holding one wrong byte for many frames because 8-bit linear light gives the darkest tones the fewest of its 256 codes. Confirmed by re-reading the current `AmbilightFrameProcessor.Process` and `PerimeterColourAdjustment.Apply` that the colour-calibration-curve work committed earlier the same day (`887ffe6`) did not disturb this: colour stays `LinearRgb`/float all the way to the encoder's own final call, unchanged in structure. What the operator is now describing -- a visibly *moving* value snapping frame to frame, not a static one stuck on the wrong byte -- is a different problem dithering was never meant to solve: the *target itself* changing by a real, visible amount every ~33 ms, most noticeable at low brightness because differential brightness sensitivity (Weber-Fechner) is highest there.

This is deliberately not the same idea as the earlier, reverted attempt to smooth Hue's white channel before dithering (see `DitheredRgbw32Encoder`'s own remarks): that attempt tried to fix flicker on a *near-constant* target, where smoothing measurably made no difference (a held target still needs the ditherer's own alternation to represent it, however it got there). Here the target is a *continuously varying* real video signal, where smoothing the value the ditherer receives genuinely gives it a smaller, easier delta to represent every frame -- a materially different case, verified with a dedicated test (`ReducesTheFrameToFrameDeltaComparedToNoSmoothingUnderRealisticSamplingNoise`) rather than assumed to work just because it is superficially similar.

## Decision 4 — implementation

New `Core/Output/WledTemporalSmoother.cs`: a single per-LED exponential low-pass on the linear-light colour, driven by an explicit elapsed-time parameter (never the wall clock, never a frame count, matching this project's established testability discipline for exactly this kind of filter). Deliberately not a reuse or generalisation of `HueNaturalLightFilter` -- that filter's split time constants, independent brightness rate clamp and 1% floor all exist for Hue-specific reasons its own remarks explain; a physical 8-bit strip does not share them, and HyperHDR's own simplest interpolator is itself just this same basic exponential low-pass. Applied in `AmbilightFrameProcessor.Process` as the last step before encoding, after every other colour adjustment -- matching where HyperHDR's own smoothing sits in its pipeline.

New setting, `PluginConfiguration.WledSmoothingMilliseconds` (default `0`, off): a real cut or fast pan still catches up within a few times the configured value; zero preserves today's exact behaviour for every existing installation. Exposed as "Smoothing" on the Advanced tab, next to the existing "Minimum colour hold" control it is conceptually adjacent to but functionally distinct from (dwell gates whether a change is accepted at all; this eases *into* an already-accepted change).

## Consequences

- No wire-format or protocol change; nothing about DDP, RGB24/RGBW32, or WLED's own realtime handling was touched.
- Off by default; zero behaviour change for an installation that does not opt in.
- 179/179 tests pass (5 new, `WledTemporalSmootherTests`), including that a genuinely varying signal's frame-to-frame movement is measurably reduced and that a reconnect-scale gap snaps instead of creeping in from stale state.
- **Not yet re-tested against the physical strip.** The operator's own report was the reason for this work; whether it is actually gone (and at what setting) still needs a real look at the LEDs during playback, not just a passing test suite.
