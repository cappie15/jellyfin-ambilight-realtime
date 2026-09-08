# ADR-013 — SDR analysis decode gets the same VAAPI path HDR already had

- **Status:** Implemented and deployed to the reference host
- **Date:** 2026-09-08
- **Relates to:** [ADR-006](ADR-006-hdr-dolby-vision-normalization.md) (the HDR analysis graph, which has used VAAPI since it was written), `Core/Decoding/FfmpegAnalysisCommandBuilder.cs`, `Core/Decoding/FfmpegAnalysisWorker.cs`

## Context

Reported live during a hands-on test: Hue lights "started late / stood
still," and the operator initially suspected a regression in this session's
own colour-processing changes. Jellyfin's own logs told a more specific
story: `Realtime Ambilight is N ms behind the picture; the analysis decoder
is not keeping its lead`, escalating over the course of a session until a
forced restart, repeatedly.

## Decision — the SDR analysis path never used hardware acceleration

Read directly against the codebase, not assumed: `FfmpegAnalysisCommandBuilder.BuildSdrArguments`
built a pure software `scale=...,format=bgra` filter chain with no
`-hwaccel` of any kind, for every non-HDR source -- the overwhelming
majority of real playback. The HDR graph (`FfmpegHdrAnalysisCommandBuilder`,
ADR-006) has used a full VAAPI pipeline (`-hwaccel vaapi`, `scale_vaapi`,
`hwdownload`) since it was written. SDR was never given the same treatment;
this was not a regression, it was a gap that had simply never been the
bottleneck until Hue's added concurrent CPU load pushed it over the edge.

Measured directly on the reference host (`ps`/`/usr/bin/time -v`, and a
manual standalone `ffmpeg` invocation as the `jellyfin` user to correctly
exercise real GPU-device permissions) against an 8-second clip of a 4K HEVC
source:

| Path | CPU | Wall-clock for 8 s of content | Realtime factor |
|---|---|---|---|
| Software SDR (before) | 184% | ~25 s | ~0.3× (i.e. 3× slower than realtime) |
| VAAPI SDR (after) | 68% | ~1.7 s | ~15× |

`FfmpegAnalysisCommandBuilder.BuildVaapiSdrArguments` mirrors the filter
chain already proven correct by the HDR graph:
`-hwaccel vaapi -hwaccel_device <path> -hwaccel_output_format vaapi`, then
`fps=X,scale_vaapi=w=W:h=H,hwdownload,format=nv12,format=bgra`.
`FfmpegAnalysisWorker.CreateSdrStartInfo` dispatches to it only when
`File.Exists(source.HardwareDevicePath)` -- the same cheap existence check
the HDR path has never had (it has always unconditionally assumed the
device is present), added here specifically so an installation with no GPU
device node at all stays on the always-worked software path instead of
failing every analysis attempt. This is not a guarantee the device is
actually usable (wrong driver, missing permissions can still fail); that
risk was already accepted for HDR and is now accepted symmetrically for SDR.

## Consequence

The operator's specific complaint -- Hue "starting late," general perceived
slowness -- was root-caused to this, not to the session's new colour-curve
work (measured negligible CPU cost by comparison). Software decode remains
the fallback for hosts without a usable VAAPI device; ADR-006's own
unresolved gaps (source-profile detection, hardware-path selection) are
unaffected by this change.
