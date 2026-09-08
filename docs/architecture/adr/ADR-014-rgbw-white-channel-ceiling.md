# ADR-014 — A single white LED cannot reach combined RGB brightness; cap what gets extracted to it

- **Status:** Implemented and deployed to the reference host, not yet re-checked against real playback content
- **Date:** 2026-09-08
- **Relates to:** `Core/Output/DitheredRgbw32Encoder.cs`, `Core/Color/PerimeterColourAdjustment.cs` (`WhiteExtractionFactor`, the White step's colour-temperature taper -- a related but distinct mechanism this ADR does not change), the earlier session's white-channel fixes documented in `DitheredRgbw32Encoder`'s own remarks (plain rounding instead of dithering white; total-preserving compensation for the colour-temperature taper)

## Context

Reported live during a hands-on RGBW test: "de wled brandt maar zwak" (WLED
burns only weakly), with the operator's own hypothesis being that some
content simply never reaches 100% of the picture's own brightness. Direct
raw-DDP testing against the physical strip (bypassing Jellyfin's decode
pipeline entirely, sending the exact wire protocol `WledRealtimeOutput`
uses) isolated this specifically to white/near-white content on an RGBW
strip with **Send a real white signal** enabled.

## Decision — measured via flicker photometry, not side-by-side comparison

A first attempt compared solid swatches sequentially and side-by-side: a
mixed-RGB white (`R=G=B=255`) against the white channel alone at various
values. This did not converge -- the operator reported the two look
"warm" (white channel) versus "enorm koel" (very cool, the mixed-RGB
reference), a colour-temperature difference large enough to dominate
perception and make brightness genuinely hard to judge directly. This is a
known, well-documented problem in photometry (heterochromatic brightness
matching), not a flaw in the operator's judgement.

The fix was **flicker photometry**: alternating the same physical LEDs
between the two colours at ~12.5 Hz (the classic 8-16 Hz range where hue
perceptually fuses while a genuine brightness difference still reads as
visible strobing). This isolated a clean signal: the gap between the mixed
reference and the white channel held **completely flat** from white=100
through white=230 out of 255 -- pushing the white channel's own value
higher did not narrow the gap at all. A closing gap would have meant "raise
the drive value"; a flat one across nearly the whole usable range means the
two are not commensurable at any value -- a single white die's peak output
does not reach three dies (red, green, blue) lit simultaneously, and no
software gain on the white channel alone can manufacture light the die
cannot physically produce.

## Decision — cap extraction, do not try to boost the die past its ceiling

`DitheredRgbw32Encoder` extracted `white = extractionFactor * min(r, g, b)`
unconditionally, sending 100% of a pixel's shared grey to the white channel
by default (`extractionFactor = 1`) and zeroing the equivalent amount from
red, green and blue. For a bright/near-white pixel this silently assumed
photometric equivalence between one die and three -- exactly what the
flicker test disproved.

The fix caps the amount actually sent to white
(`whiteChannelCeiling`, plumbed from a new **White LED strength** setting,
default 50%) and, critically, does **not** rescale red/green/blue back up
to compensate the way the existing colour-temperature taper's compensation
does. That compensation exists to *prevent* an increase (the White step
shifting temperature must not raise total output -- the earlier session's
"witte led komt er nog bij bovenop" bug). This is the opposite case: the
ceiling deliberately lets red, green and blue retain more of their own
original value above the ceiling, since three simultaneously lit dies can
reach brightness the white die alone cannot -- retaining that headroom is
the fix, not something to counteract.

Below the ceiling, behaviour is byte-identical to before: those pixels were
never bright enough for the die's own peak to be the limiting factor, so
dim/moderate greys keep the full benefit of the white die (lower current
draw, more neutral colour) exactly as they did previously.

## Consequence and open question

50% is a reasoned starting point from where the flicker test left off (the
gap held at every level tested up to 230/255 without narrowing at all, so a
lower ceiling than "no cap" is clearly warranted, but the precise die-vs-die
efficiency ratio was not pinned down more tightly than that). Exposed as an
operator-adjustable setting rather than hardcoded, since this ratio is a
property of the specific RGBW LED chip on the strip, not a universal
constant. Re-check against real, varied playback content once the operator
can watch the strip directly -- synthetic single-swatch and even
flicker-photometry tests, useful as they were for isolating the mechanism,
are still a proxy for how actual white-heavy scenes look.
