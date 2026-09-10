# ADR-015 — Which WLED settings can silently affect our output, and which cannot

- **Status:** Implemented and deployed to the reference host
- **Date:** 2026-09-10
- **Relates to:** `WledDiscoveryService.ReadRealtimeSettingsAsync` (the established read-and-warn pattern this extends), [ADR-004](ADR-004-wled-realtime-protocol-strategy.md) (WLED realtime protocol strategy)

## Context

The operator asked for every WLED setting that can affect ambilight output to
be readable, explained, and where safe, fixable from the plugin's own
settings page — "de gebruiker moet hier nimmer voor de wled controller
induiken" (the user should never need to dive into the WLED controller for
this). Rather than exposing WLED's whole configuration surface, each
candidate field was checked against WLED's own firmware source
(`github.com/wled/WLED`) for what it actually does, since several
plausible-sounding fields turn out not to touch realtime data at all.

## Decision — audited every field in `/json/cfg`'s `hw.led`, `light`, `def` and `if.live` sections

| Field | Verdict | Why |
|---|---|---|
| `if.live.offset` (`arlsOffset`) | **Managed** (read, warn, one-click fix) | `udp.cpp`: `unsigned pix = i + arlsOffset` — shifts every realtime pixel index before it reaches the strip. Nonzero silently misaligns this plugin's whole top/right/bottom/left layout, with no error anywhere. Safe to write (one integer, no hardware risk). |
| Nightlight (`state.nl.on`, live only, not in `/json/cfg`) | **Managed** (read, contextual warn, no fix offered) | Confirmed live, not just from source: started a nightlight, streamed a full-white realtime frame, watched WLED's own `bri` stay pinned at a dimmed value for the session instead of the value this plugin sent. Only actually reaches the strip when "force max brightness" is off (realtime bypasses WLED's own `bri` entirely while that is on) — so the warning fires on `nightlightActive && !forcesMaxBrightness` together, not nightlight alone, which would be a false alarm in the common case. No one-click fix offered: turning off someone's nightlight timer isn't this plugin's call the way resetting a mis-set offset is. |
| `if.live.rlm` (`realtimeRespectLedMaps`) | **Out of scope** | Only matters if a custom LED map (2D/matrix remap) is configured in WLED; a simple perimeter loop has none, making this inert for the reference install and most installs like it. |
| `if.live.mc` (`e131Multicast`) | **Out of scope** | E1.31 multicast vs unicast. Confirmed irrelevant — does not touch this plugin's DDP unicast path at all. |
| `hw.led.cct`/`cr`/`ic` | **Out of scope** | `FX_fcn.cpp`: all three gate on `bus->hasCCT()` — only apply to a dedicated tunable-white bus. An RGBW bus is `hasRGB()`, not `hasCCT()`; these fields do not apply to this hardware category at all. |
| `light.pal-mode` (`paletteBlend`) | **Out of scope** | Palette-interpolation mode for WLED's own effects engine, same category as `light.tr.dur` (ADR-014's earlier finding) — realtime mode bypasses the effects engine entirely. |
| `light.aseg` (`autoSegments`) | **Out of scope** | One-time convenience at bus-configuration time ("make one segment per bus"), not an ongoing live-rendering behaviour. |
| `def.ps`/`def.on`/`def.bri` (boot defaults) | **Out of scope, documented only** | Applied only at WLED's own power-on/restart, not during normal playback and not during the gap this plugin's own "claim the strip immediately" logic already covers. Only visible for the few seconds after a WLED reboot specifically. Not worth a control; set once in WLED's own UI. |
| Per-bus `ins[].freq`/`drv`/`ledma`/`type`/`pin`/etc. | **Read-only at most, never write** | Physical wiring facts (driver backend, per-LED current draw feeding WLED's own ABL power-budget math, clock rate for non-WS281x buses). A plugin writing these blind risks silently corrupting the ABL safety math or disabling output entirely if the wrong driver is selected. WLED's own setup wizard sets these once at install time; this plugin does not touch them. |

## Consequence

Two new fields joined the existing RGBW-mode/fps-cap/timeout/force-max-brightness
set already surfaced on the WLED card: `RealtimePixelOffset` (with
`TryResetRealtimePixelOffsetAsync`, mirroring the existing rgbwm/maxbri fix
pattern exactly) and `NightlightInterferesWithOutput` (read-only warning).
Both require the same `AllowWledControl` opt-in already gating every other
WLED write this plugin makes. No other field researched here met the bar for
inclusion — most either do not touch realtime-streamed data at all, or are
hardware-wiring facts unsafe to change from outside WLED's own setup flow.
