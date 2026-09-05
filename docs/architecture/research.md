# Architecture Research

Required by master prompt section 82. This document records what was found by
inspecting the *current* upstream sources, not by recalling documentation or
blog posts. Every claim below is traceable to a file and line in a pinned
commit, or to a command executed on the reference development host.

Status: **draft for operator review**. Sections marked ⚠ contain open questions
that must be resolved before the corresponding ADR can be finalised.

---

## 0. Pinned upstream references

All upstream trees were shallow-cloned on 2026-09-05 on the development host.

⚠ **Version drift qualifier — applies to every source citation in this document
and in all nine ADRs.** Citations are against the refs in the table below, which
are *not* the deployed versions:

| Cited | Deployed | Gap |
|---|---|---|
| jellyfin tag `v10.11.11` | Jellyfin **10.11.9** | two patch releases; the clone is shallow and carries no 10.11.9 tag, so no diff is possible |
| WLED `main` @ `f49e541`, build 2607201 (2026-09-01) | WLED **16.0.0**, build 2605030 | ~2.5 months of `main` ahead of the firmware actually running |

Several cited WLED sites are visibly post-16.0.0. Nothing has been verified
against the exact deployed builds; where a claim is load-bearing it should be
re-checked against them.

| Project | Ref | Commit | Date |
|---|---|---|---|
| jellyfin/jellyfin | tag `v10.11.11` | `1fbd8739292cce610231be93daf43368733edf63` | 2026-06-06 |
| jellyfin/jellyfin-plugin-template | `master` | `726279e026ff82e3bebea1bcd8f106412a718952` | — |
| Aircoookie/WLED | `main` | `f49e541c5e4a90a78068ebbe7b2672555cb3227f` | 2026-09-01 |
| awawa-dev/HyperHDR | `master` | `e5bda285227cbd38bce60da6a442dde936fd9e7a` | 2026-09-01 |
| gabrielprat/jellyfin-ambilight | `main` | `e64536e021910afc593375da5f3d6280f31d1b5b` | 2026-07-27 |

Reference development host (`jellyfin-dev`, an unprivileged Proxmox LXC,
Ubuntu 24.04, 4 cores / 8 GB):

| Component | Version |
|---|---|
| jellyfin-server / jellyfin-web | `10.11.9+ubu2404` |
| jellyfin-ffmpeg7 | `7.1.3-6-noble` |
| GPU | Intel iGPU, `/dev/dri/card1` (group `video`), `/dev/dri/renderD128` (group `render`) |

Network addresses in this document are pseudonyms: `jellyfin-dev` is the
development server, `jellyfin-prod` the production Jellyfin instance (never
contacted), and `wled-livingroom` the WLED controller of the reference
installation.

---

## 1. Current Jellyfin stable version

Queried from the official GitHub releases API on 2026-09-05:

| Tag | Prerelease | Published |
|---|---|---|
| `v12.0-rc7` … `v12.0-rc1` | **yes** | 2026-06-21 … 2026-08-31 |
| `v10.11.11` | **no** | 2026-06-06 |

**The current latest stable release is 10.11.11.** The `v12.0-rc*` series is the
next major line (the version numbering jumps from 10.11 to 12.0) and is still in
release-candidate state. Master targets `net10.0`; `v10.11.11` targets `net9.0`
(`Jellyfin.Server/Jellyfin.Server.csproj:11`).

**Q1 resolved — operator decision, 2026-09-05: do not upgrade the dev host. The
plugin must work from 10.11.9.**

Packages are therefore pinned to **10.11.9** rather than to the newest 10.11.x,
so the plugin runs on 10.11.9 and on later 10.11.x patch releases alike. Build
target:

| | |
|---|---|
| Target framework | `net9.0` |
| `Jellyfin.Controller` / `Jellyfin.Model` | `10.11.9` |
| Minimum supported server | **10.11.9** |
| Reference host | 10.11.9 — build and test target are now identical |

The plugin ABI is stable across the 10.11.x patch line, so pinning to the floor
of that line is the safe direction: building against 10.11.11 and running on
10.11.9 could bind to a symbol the older server lacks, whereas the reverse
cannot.

⚠ **Risk R1.** Jellyfin 12.0 will ship during this project's lifetime and is a
major version with a different TFM (`net10.0`). Section 5 forbids compatibility
abstractions for *obsolete* releases; it says nothing about *future* ones. A v1
targeting 10.11.x will need a follow-up release for 12.0. This should be an
explicit, documented decision rather than an accident.

---

## 2. Jellyfin plugin API

From the plugin template (`Jellyfin.Plugin.Template/Jellyfin.Plugin.Template.csproj`):

```xml
<TargetFramework>net9.0</TargetFramework>
<PackageReference Include="Jellyfin.Controller" Version="10.11.5" />
<PackageReference Include="Jellyfin.Model" Version="10.11.5" />
```

plus analyzers (`SerilogAnalyzer`, `StyleCop.Analyzers`,
`SmartAnalyzers.MultithreadingAnalyzer`). The template pins 10.11.5; we pin
**10.11.9** (see §1).

Relevant extension points for this project:

- `BasePlugin<TConfiguration>` + `IHasWebPages` — plugin identity and the
  embedded configuration page.
- `IPluginServiceRegistrator` — DI registration of our own services. This is how
  the analysis pipeline gets constructed and how it obtains `ISessionManager`
  and `IMediaEncoder`.
- `IHostedService` / `IServerEntryPoint` — lifecycle hooks for starting and
  stopping the playback monitor.
- ASP.NET Core controllers under `Api/` — for the wizard, calibration test
  patterns and diagnostics endpoints consumed by the config page.

`jellyfin-ambilight` demonstrates all of these
(`Plugin.cs`, `Server/AmbilightServiceRegistrator.cs`,
`Server/AmbilightEntryPoint.cs`, `Api/AmbilightController.cs`).

### 2.1 Access to jellyfin-ffmpeg — key finding

`MediaBrowser.Controller/MediaEncoding/IMediaEncoder.cs` exposes:

| Member | Line | Use for us |
|---|---|---|
| `string EncoderPath` | 28 | absolute path to the jellyfin-ffmpeg binary Jellyfin itself uses |
| `string ProbePath` | 34 | ffprobe path |
| `Version EncoderVersion` | 40 | version gating |
| `bool SupportsHwaccel(string)` | 103 | QSV / VAAPI availability |
| `bool SupportsFilter(string)` | 110 | e.g. `libplacebo`, `tonemap_vaapi`, `vpp_qsv` |
| `bool SupportsFilterWithOption(FilterOptionType)` | 117 | fine-grained filter capability |

**This is the single most important finding for the installation architecture.**
A plugin that takes `IMediaEncoder` from DI can locate and spawn the exact
FFmpeg binary Jellyfin is already using, and can interrogate hardware and filter
support, *without* downloading anything, without a companion service, and
without any privileged host configuration. See section 12 below.

---

## 3. jellyfin-ambilight architecture

Source layout at `e64536e`:

```
Plugin.cs, PluginConfiguration.cs
Server/    AmbilightServiceRegistrator.cs, AmbilightEntryPoint.cs
Api/       AmbilightController.cs
Tasks/     ExtractPendingAmbilightTask.cs
Services/  AmbilightExtractorService.cs, AmbilightInProcessExtractor.cs,
           AmbilightInProcessPlayer.cs, AmbilightPlaybackService.cs,
           AmbilightStorageService.cs, Amb3Format.cs,
           EmbeddedBinariesResolver.cs
Services/Extraction/  IExtractionLogic.cs, ExtractionLogicFactory.cs,
                      LinearLightAverageExtractionLogic.cs,
                      EdgeWeightedExtractionLogic.cs
```

Build target: `net8.0`, `Jellyfin.Controller` / `Jellyfin.Model` `10.10.*`,
`GPL-3.0-or-later`, `AssemblyVersion 2.5.0`.

### 3.1 What it does well and is worth reusing conceptually

- **Session monitoring and device mapping.** `AmbilightPlaybackService` binds
  WLED targets to a *device*, not a user (`ResolveWledTargets(SessionInfo)`,
  line 265), matching master prompt section 8. It supports multiple WLED targets
  per device and de-duplicates hosts (line 305).
- **PositionTicks handling.** `OnPlaybackStart` (line 63) converts
  `info.PositionTicks` to seconds via `/ 10_000_000.0` (line 178);
  `OnPlaybackProgress` (line 218) re-reads position and applies a tolerance
  window before correcting, with the comment "Tolerance covers decode/scheduling
  drift and clients that round position to whole [seconds]" (line 245). The
  drift-tolerance idea transfers directly to our `PlaybackClock`.
- **Extraction logic abstraction.** `IExtractionLogic` with
  `LinearLightAverageExtractionLogic` and `EdgeWeightedExtractionLogic` behind a
  factory. Linear-light averaging is the correct approach and section 31
  mandates it; edge-weighted (Sobel-style) sampling is a useful second mode.
- **User-facing feedback through the LEDs themselves.** `SendLoadingEffectAsync`
  and `SendFailureFlashAsync` communicate state without any UI. Worth keeping as
  an idea for the calibration wizard.

### 3.2 What must not be carried over

- **Precomputed `.amb3` files.** This is the architecture master prompt section
  22 explicitly forbids. Its cost is directly observable on this host: the
  production clone's `/ambilight/` directory holds **711 items totalling roughly
  89 GB** — one `.json` sidecar plus one `.bin` per media item, e.g.
  `189,334,969` bytes for a single episode of *Silo*. The sidecars record
  `ExtractionTopLedCount: 266`, `ExtractionRightLedCount: 150`,
  `ExtractionBottomLedCount: 266`, `ExtractionLeftLedCount: 150` — i.e. 832 LEDs
  — and `ExtractedByPluginVersion: 0.1.2.0`. This is empirical, on-disk
  justification for the realtime architecture and should be cited in ADR-002.
- **Hyperion raw RGB UDP on port 19446.**
  `Services/AmbilightInProcessPlayer.cs:36` — `public const int WledUdpPort = 19446;`
  with `const int bytesPerLed = 3` (line 167). Port 19446 is WLED's Hyperion raw
  UDP realtime port (`REALTIME_MODE_HYPERION`, `wled00/const.h:300`). This
  protocol carries a bare RGB triplet stream in a single datagram with **no
  offset field and no fragmentation**, so its LED ceiling is
  `(MTU − IP/UDP headers) / 3 ≈ 490`. **This — not WLED — is the origin of the
  ~490 LED limit called out in master prompt section 13.** Confirmed by reading
  both sides of the wire.
- **Embedded per-RID helper binaries.** `EmbeddedBinariesResolver.cs` plus
  `<EmbeddedResource Include="Binaries\linux-x64\ambilight-extractor" …>` for
  five RIDs. A Rust extractor shipped inside the DLL. Given finding 2.1 we do
  not need this.
- **`net8.0` / Jellyfin `10.10.*`.** Two Jellyfin minor versions behind.

---

## 4. Playback / session API

`MediaBrowser.Controller/Session/ISessionManager.cs` at `v10.11.11` — identical
event surface to master, so this is stable across the 10.11 → 12.0 boundary:

| Line | Event |
|---|---|
| 27 | `event EventHandler<PlaybackProgressEventArgs> PlaybackStart` |
| 32 | `event EventHandler<PlaybackProgressEventArgs> PlaybackProgress` |
| 37 | `event EventHandler<PlaybackStopEventArgs> PlaybackStopped` |
| 42 | `event EventHandler<SessionEventArgs> SessionStarted` |
| 47 | `event EventHandler<SessionEventArgs> SessionEnded` |
| 49 | `event EventHandler<SessionEventArgs> SessionActivity` |
| 54 | `event EventHandler<SessionEventArgs> SessionControllerConnected` |
| 59 | `event EventHandler<SessionEventArgs> CapabilitiesChanged` |

`MediaBrowser.Controller/Library/PlaybackProgressEventArgs.cs` carries exactly
what the synchronisation design needs:

```csharp
long?         PlaybackPositionTicks
BaseItem      Item
BaseItemDto   MediaInfo
string        MediaSourceId
bool          IsPaused
bool          IsAutomated
string        DeviceId
string        DeviceName
string        ClientName
string        PlaySessionId
SessionInfo   Session
```

Notes for the design:

- These are **push** events. Section 35's "do NOT constantly poll Jellyfin at
  high frequency" is satisfied by subscribing rather than polling.
- `DeviceId` is the anchor for the device-centric model of section 8.
- `MediaSourceId` + `Item` give us the on-disk path for the analysis decoder,
  which is what lets Ambilight analyse the *original* stream independently of
  whether the client is direct-playing or transcoding (sections 25 and 34).
- There is **no dedicated seek event**. A seek surfaces as a `PlaybackProgress`
  whose position jumps discontinuously relative to our local clock. The seek
  debounce of section 36 must therefore be built on drift detection in
  `PlaybackClock`, not on an event subscription.
- ⚠ **Open question (Q2).** `PlaybackProgress` cadence is client-driven. The
  Android TV client's actual reporting interval on this setup is unmeasured. It
  determines how far the local monotonic clock must coast between corrections.
  Needs measurement against the real TCL device before ADR-007 is final.

---

## 5. Current jellyfin-ffmpeg behaviour

Installed build, `/usr/lib/jellyfin-ffmpeg/ffmpeg -version`:

```
ffmpeg version 7.1.3-Jellyfin
```

Configure flags relevant to us:

```
--enable-opencl --enable-libdrm --enable-libzimg --enable-libshaderc
--enable-libplacebo --enable-vulkan --enable-vaapi --enable-amf
--enable-libvpl --enable-ffnvcodec --enable-cuda --enable-cuda-llvm
--enable-cuvid --enable-nvdec --enable-nvenc
```

`--enable-libvpl` is the modern Intel oneVPL runtime, i.e. the current QSV path.

Available hwaccels on this host:

```
cuda  vaapi  qsv  drm  opencl  vulkan
```

Filters relevant to the analysis pipeline:

```
libplacebo        N->V   Apply various GPU filters from libplacebo
vpp_qsv           V->V   Quick Sync Video "VPP"
scale_qsv         V->V   QSV scaling and format conversion
tonemap_vaapi     V->V   VAAPI VPP for tone-mapping
tonemap_opencl    V->V   HDR to SDR conversion with tonemapping
tonemapx          V->V   SIMD optimized HDR to SDR tonemapping (software)
tonemap           V->V   generic (software)
```

`MediaBrowser.MediaEncoding/Encoder/EncoderValidator.cs:210` sets
`MinVersion = 4.4`, `MaxVersion = null`. 7.1.3 is comfortably supported.

Design consequence: we do not build a parallel media stack (section 23). We
spawn `IMediaEncoder.EncoderPath` as a child process with a filter graph that
scales down *on the GPU* before any download to system memory, and read raw
frames from its stdout.

---

## 6. QSV architecture

Confirmed present on the reference host: `qsv` hwaccel, `--enable-libvpl`,
`vpp_qsv` / `scale_qsv` filters, and `/dev/dri/renderD128` exposed inside the
unprivileged LXC.

The performance-critical property for section 65 ("Do not decode
full-resolution RGB frames on CPU if a lower-resolution GPU path can provide the
needed data") is that scaling must happen **before** `hwdownload`. For a 4K HEVC
source with a ~160×90 analysis target that is a bandwidth reduction of roughly
three orders of magnitude per frame.

Sketch of the intended shape (to be validated by prototype, not assumed):

```
-hwaccel qsv -hwaccel_output_format qsv  →  vpp_qsv (scale + tonemap)
                                         →  hwdownload
                                         →  format=<small planar/rgb>
                                         →  rawvideo to stdout
```

⚠ **Assumption A1 requiring prototype validation.** That QSV can decode 4K HEVC
Main10 and downscale on-GPU on *this* iGPU while a real Jellyfin playback
session is in progress, without measurably affecting that playback (section 25,
section 74). Must be measured, not assumed.

⚠ **Assumption A2.** That `vpp_qsv` alone can do the HDR→SDR conversion we need,
versus needing `libplacebo` (Vulkan) or `tonemap_opencl`. `tonemap_vaapi` and
`vpp_qsv` are both present; Jellyfin's own `EncodingHelper` prefers
`tonemap_vaapi` over `vpp_qsv` on Linux "for supporting Gen9/KBLx"
(`EncodingHelper.cs:406`). The correct choice depends on the actual iGPU
generation in this host, which is not yet identified.

---

## 7. HDR implementation

`MediaBrowser.Controller/MediaEncoding/EncodingHelper.cs` at `v10.11.11` gates
tone mapping on capability probes:

| Line | Gate |
|---|---|
| 279 | `_mediaEncoder.SupportsFilter("tonemap_vaapi")` |
| 319 | `_mediaEncoder.SupportsFilter("libplacebo")` |
| 341 | `!_mediaEncoder.SupportsFilter("tonemapx")` |
| 346, 381 | `state.VideoStream.VideoRange == VideoRange.HDR` |
| 415–416, 432–436 | HDR10 / HLG specific paths |
| 1066 | "libplacebo wants an explicitly set vulkan filter device" |

The pattern to copy is *capability probing at runtime*, not compile-time
assumptions — which is also what section 61's first-run environment check wants
to surface in the UI.

---

## 8. Dolby Vision capabilities

`Jellyfin.Data/Enums/VideoRangeType.cs` at `v10.11.11`:

```csharp
Unknown, SDR, HDR10, HLG,
DOVI, DOVIWithHDR10, DOVIWithHLG, DOVIWithSDR,
DOVIWithEL, DOVIWithHDR10Plus, DOVIWithELHDR10Plus, DOVIInvalid,
HDR10Plus
```

Jellyfin already classifies Dolby Vision by *cross-compatibility layer*, which
maps onto the profiles section 30 lists:

- `DOVIWithHDR10` — DV P8.1 (HDR10 base layer)
- `DOVIWithHLG` — DV P8.2/8.4 (HLG base layer)
- `DOVIWithSDR` — DV P8.4 style SDR base
- `DOVIWithEL` — dual-layer, i.e. **P7** (enhancement layer present)
- `DOVI` with no cross-compat — **P5**, which has *no* usable non-DV base layer
- `DOVIInvalid` — malformed metadata

`EncodingHelper.cs:1372-1376` groups
`DOVIWithHDR10 | DOVIWithEL | DOVIWithHDR10Plus | DOVIWithELHDR10Plus | DOVIInvalid`
for special handling, and line 391 carries the explicit upstream comment:

> `// libplacebo has partial Dolby Vision to SDR tonemapping support.`

**"Partial" is the operative word.** Section 32 requires that we never silently
treat DV as ordinary SDR, and section 30 requires that we not claim support for
untested profiles.

Practical reading, to be confirmed by prototype:

| Profile | Base layer | Expected handling |
|---|---|---|
| P8.1 | HDR10 | tone-map the HDR10 base — good result, RPU ignored |
| P8.2/8.4 | HLG / SDR | tone-map the base layer |
| P7 | dual-layer, BL is HDR10 | tone-map BL, discard EL — good result |
| P5 | **IPT-PQ-C2, no SDR-compatible base** | ⚠ **hard case** |

⚠ **Assumption A3 requiring prototype validation.** DV **Profile 5** is the real
risk. Its base layer is not BT.2020 PQ YCbCr in the ordinary sense; decoding it
without applying the RPU yields the characteristic green/purple cast. If
libplacebo's partial DV support does not handle P5 on this hardware, the
technically valid fallback per section 32 is to detect P5 and either apply a
documented approximate correction or explicitly degrade (and say so in
Diagnostics and in the docs) — never to pass it through as if it were SDR.

⚠ **Open question (Q3).** Which DV profiles exist in the operator's actual
library? Testing P5 and P7 requires real sample media. Section 72 requires
documenting precisely which profiles were actually tested. This needs the
operator to identify candidate files under `/films`, `/films2`, `/series`.

---

## 9. HyperHDR algorithms worth reusing

### 9.1 Black-border detection

`include/blackborder/BlackBorderDetector.h`:

```cpp
static uint8_t calculateThreshold(double blackborderThreshold);        // :19
BlackBorder process_classic  (const Image<ColorRgb>& image) const;     // :21
BlackBorder process_osd      (const Image<ColorRgb>& image) const;     // :22
BlackBorder process_letterbox(const Image<ColorRgb>& image) const;     // :23

return (color.red   < _blackborderThreshold)
    && (color.green < _blackborderThreshold)
    && (color.blue  < _blackborderThreshold);                          // :28
const uint8_t _blackborderThreshold;                                   // :32
```

`include/blackborder/BlackBorderProcessor.h` — the temporal stabilisation layer
that section 33 asks for:

```cpp
unsigned    _maxInconsistentCnt;      // :43
unsigned    _blurRemoveCnt;           // :44
QString     _detectionMode;           // :45
BlackBorder _currentBorder;           // :47
BlackBorder _previousDetectedBorder;  // :48
```

The design to adopt: a cheap per-frame detector producing a *candidate* border,
plus a separate processor that only promotes a candidate to the current border
after it has been consistent for N frames (`_maxInconsistentCnt`), and that
discards a margin of pixels next to the detected edge (`_blurRemoveCnt`) to
avoid sampling compression halo at the letterbox boundary. Three detection
modes (`classic` / `osd` / `letterbox`) let the user trade robustness against
responsiveness. This directly answers section 33's "do not let a dark movie
scene suddenly get interpreted as a black border".

### 9.2 Smoothing

`include/infinite-color-engine/InfiniteSmoothing.h`:

```cpp
enum class SmoothingType {
    Stepper = 0, RgbInterpolator = 1, YuvInterpolator = 2,
    HybridInterpolator = 3, ExponentialInterpolator = 4,
    HybridRgbInterpolator = 5                                          // :70
};
unsigned addConfig(int settlingTime_ms,
                   double ledUpdateFrequency_hz = 25.0,
                   bool pause = false);                                // :62
unsigned addCustomSmoothingConfig(unsigned cfgID, int settlingTime_ms,
                                  double ledUpdateFrequency_hz, bool pause);
bool getAntiFlickeringFilterState();                                   // :43
void SignalMasterClockTick();                                          // :48
```

Two things transfer directly:

1. **Smoothing is parameterised as `(settlingTime_ms, ledUpdateFrequency_hz)`** —
   exactly the two coordinated variables behind the single "Response
   FAST↔SMOOTH" slider of section 40. This is strong evidence that the slider
   design is workable rather than a simplification.
2. **A master clock tick drives LED output** (`SignalMasterClockTick`), decoupled
   from frame arrival. That is the `OutputScheduler` of section 57 and it is
   what makes bounded queues and frame dropping (section 42) natural.

An anti-flickering filter exists as a separate concern from smoothing — relevant
to section 56 (dark scenes).

---

## 10. WLED protocols

All findings below are from the WLED source at `f49e541`, both the send and the
receive path.

### 10.1 Realtime modes

`wled00/const.h:297-306`:

```c
#define REALTIME_MODE_INACTIVE  0
#define REALTIME_MODE_GENERIC   1
#define REALTIME_MODE_UDP       2
#define REALTIME_MODE_HYPERION  3
#define REALTIME_MODE_E131      4
#define REALTIME_MODE_ADALIGHT  5
#define REALTIME_MODE_ARTNET    6
#define REALTIME_MODE_TPM2NET   7
#define REALTIME_MODE_DDP       8
#define REALTIME_MODE_DMX       9
```

### 10.2 DDP wire format

`wled00/src/dependencies/e131/ESPAsyncE131.h`:

```c
#define DDP_DEFAULT_PORT   4048
#define DDP_HEADER_LEN     10
#define DDP_FLAGS_VER      0xc0   // version mask
#define DDP_FLAGS_VER1     0x40
#define DDP_FLAGS_PUSH     0x01
#define DDP_FLAGS_QUERY    0x02   // unsupported by WLED
#define DDP_FLAGS_REPLY    0x04   // unsupported by WLED
#define DDP_FLAGS_STORAGE  0x08   // unsupported by WLED
#define DDP_FLAGS_TIME     0x10
#define DDP_CHANNELS_PER_PACKET 1440   // 480 leds
#define DDP_TYPE_RGB24     0x0B   // 00 001 011
#define DDP_TYPE_RGBW32    0x1B   // 00 011 011
#define DDP_ID_DISPLAY     1
#define DDP_ID_CONTROL   246      // not implemented
#define DDP_ID_CONFIG    250      // not implemented
#define DDP_ID_STATUS    251      // not implemented
#define DDP_ID_ALL       255
```

Header layout, read off WLED's own sender (`wled00/udp.cpp:794-806`):

| Offset | Field |
|---|---|
| 0 | flags (`DDP_FLAGS_VER1`, `| DDP_FLAGS_PUSH` on the final packet of a frame) |
| 1 | sequence number, `seq++ & 0x0F` |
| 2 | data type (`DDP_TYPE_RGB24` / `DDP_TYPE_RGBW32`) |
| 3 | destination id (`DDP_ID_DISPLAY` = 1) |
| 4–7 | channel offset, big-endian uint32, **in bytes not LEDs** |
| 8–9 | data length, big-endian uint16 |
| 10.. | payload |

### 10.3 DDP receive path — the constraints that actually matter

`wled00/e131.cpp:25-98`, `handleDDPPacket()`:

- **Packet length is validated twice.** `packetLen < DDP_HEADER_LEN` → drop
  (line 29); `packetLen < DDP_HEADER_LEN + c + dataLen` → drop with
  `"DDP packet incomplete"` (line 71); and a bounds check
  `maxDataIndex > dataLen` → drop with `"DDP packet data bounds exceeded"`
  (line 79). We must set `dataLen` exactly and never over-claim.
- **Control/status/config destinations and query/reply/storage flags are
  rejected** (lines 39–45). We must send `destination = DDP_ID_DISPLAY (1)` and
  use only `VER1` + `PUSH`.
- **Timecode flag shifts the payload by 4 bytes and is not supported**
  (line 68). Do not set `DDP_FLAGS_TIME`.
- **Channel offset is divided by channels-per-LED, and WLED adds its own
  `DMXAddress`** (lines 62–63):
  ```c
  uint32_t start = htonl(p->channelOffset) / ddpChannelsPerLed;
  start += DMXAddress / ddpChannelsPerLed;
  ```
  ⚠ A non-zero `DMXAddress` in the user's WLED config silently shifts our whole
  frame. The first-run check should read it and warn.
- **Pixel index is further shifted by `arlsOffset`** (`wled00/udp.cpp:666-670`):
  ```c
  void setRealtimePixel(uint16_t i, byte r, byte g, byte b, byte w) {
    unsigned pix = i + arlsOffset;
    strip.setRealtimePixelColor(pix, RGBW32(r,g,b,w));
  }
  ```
  Same caveat.
- **The PUSH flag means "render now"** (line 44). WLED tracks
  `ddpSeenPush`; if it has never seen a push it renders every packet, otherwise
  it renders only on push (lines 93–97). So: set PUSH on the **last** packet of
  each frame only. This is what makes multi-packet frames tear-free.
- **Realtime lock is (re)armed on every accepted packet** (line 85):
  ```c
  realtimeLock(realtimeTimeoutMs, REALTIME_MODE_DDP);
  ```
  with `realtimeTimeoutMs` defaulting to **2500 ms** (`wled00/wled.h:432`).
- **`realtimeOverride`** (`wled00/wled.h:723`) — if the user has set an override,
  WLED accepts our packets but does not display them. Diagnostics should be able
  to detect this state via the JSON API rather than reporting "connected" while
  nothing lights up.

### 10.4 Out-of-sequence rejection — the real limiting constraint

`wled00/e131.cpp:47-57`:

```c
//reject late packets belonging to previous frame (assuming 4 packets max.
//before push, if more are used and packets are very late, they are still accepted)
if (e131SkipOutOfSequence && lastPushSeq) {
  int sn = p->sequenceNum & 0xF;   // 4 bits, 1-15, 0 means unused
  ...
}
```

Two consequences:

1. The DDP sequence number is **4 bits (1–15, 0 = unused)**, not 8.
2. WLED's out-of-order filter is tuned on the assumption of **at most 4 packets
   per frame**. Beyond that, its heuristic can discard legitimate late packets
   when `e131SkipOutOfSequence` is enabled.

At 480 LEDs/packet (RGB), 4 packets ≈ **1920 LEDs**. This is the honest,
source-derived ceiling to surface in the UI per section 14 — and it is a *soft*
ceiling that only applies when the user has enabled skip-out-of-sequence.

### 10.5 The 832-LED reference installation

| Quantity | Value |
|---|---|
| Physical LEDs | 832 |
| Channels (RGB) | 832 × 3 = **2496** |
| Packets at 1440 ch/packet | ⌈2496 / 1440⌉ = **2** |
| Packet 1 | offset 0, dataLen 1440, flags `VER1` |
| Packet 2 | offset 1440, dataLen 1056, flags `VER1 \| PUSH` |
| Largest UDP payload | 10 + 1440 = **1450 bytes** |
| IPv4 datagram | 1450 + 8 (UDP) + 20 (IP) = **1478 bytes** — fits a 1500 MTU without fragmentation |

**832 LEDs over DDP is comfortably within spec: 2 packets per frame, no IP
fragmentation, well inside the 4-packet heuristic.** The reference installation
is not a stress case for DDP; it is a normal one. The ~490 limit of
`jellyfin-ambilight` is purely an artefact of its Hyperion-raw-UDP choice.

### 10.6 Protocol comparison

| Protocol | Port | Offset field | Max LEDs / frame | Verdict for v1 |
|---|---|---|---|---|
| **DDP** | 4048 | yes, 32-bit byte offset | ~1920 soft (seq heuristic); far more with skip-out-of-sequence off | **preferred** |
| Hyperion raw | 19446 | **no** | ~490 (single datagram) | reject — this is the old limit |
| E1.31 / sACN | 5568 | universe-based | 170 LEDs/universe, many universes | fallback; more overhead |
| Art-Net | 6454 | universe-based | 170 LEDs/universe | fallback |
| TPM2.NET | 65506 | packet-index | — | not needed |

Section 16's "Auto (recommended)" should therefore resolve to DDP whenever the
controller's firmware supports it, and the UI should display `Protocol: Auto (DDP)`.

### 10.7 WLED device capability limits

`wled00/const.h:565-599` — these are firmware build limits, useful for the
first-run sanity check:

```c
MAX_LEDS          16384   // classic ESP32, S3, P4  (1536 on ESP8266, 2048 S2, 4096 C3)
MAX_LEDS_PER_BUS   2048
MAX_LED_MEMORY     85*1024 // ESP32
```

⚠ **Open question (Q4).** WLED applies its master brightness and (if enabled)
automatic brightness limiting (`ABL_MILLIAMPS_DEFAULT 850`) to realtime pixel
data. For 832 LEDs, ABL at a default current cap will actively scale our output.
This interacts with the colour calibration of section 55 and must be
characterised on the real controller — which needs the operator, since it is a
visual effect (see "Operator verification log" below).

⚠ Not yet researched: **mDNS discovery** (section 17), the **JSON API** surface
for reading state/firmware/LED count and for clean release (sections 39 and 49),
and **multi-output/bus mapping** semantics. These are needed before ADR-005 is
final.

---

## 11. Protocol limits — summary for the UI

Per section 14, these are the numbers the plugin must enforce and explain rather
than silently truncate:

| Configured LEDs | DDP packets | Status |
|---|---|---|
| 1 | 1 | fine |
| 100 | 1 | fine |
| 490 | 2 | fine (Hyperion raw would already be at its ceiling) |
| 491 | 2 | fine |
| 832 | 2 | **fine — reference installation** |
| 1000 | 3 | fine |
| 1920 | 4 | at the out-of-sequence heuristic boundary |
| >1920 | ≥5 | warn: recommend disabling skip-out-of-sequence on the controller |

---

## 12. Installation and helper options

Master prompt sections 4, 53 and 63 allow a companion service but forbid manual
installation. Finding 2.1 changes the calculus substantially.

| Option | Verdict |
|---|---|
| **In-process, spawn `IMediaEncoder.EncoderPath` as a child process** | **Preferred.** No download, no checksum verification, no systemd unit, no privilege escalation, no Docker special-casing. Works identically in LXC and Docker. Uses the FFmpeg Jellyfin already trusts (section 23). |
| Downloaded helper binary per RID | Rejected for v1. Adds HTTPS fetch, checksum verification (section 64), version-matching, and an offline-install failure mode that conflicts with section 19. |
| systemd companion service | Rejected for v1. Impossible in Docker (section 63), needs privilege we should not require. |
| Embedded per-RID binaries in the DLL | Rejected. This is `jellyfin-ambilight`'s approach; it bloats the plugin and duplicates FFmpeg. |

The remaining questions are process-management ones, not installation ones:
child-process lifetime, watchdog, and guaranteeing no orphan FFmpeg processes
(sections 52 and 70).

⚠ **Assumption A4.** That spawning a child process from a Jellyfin plugin is
unrestricted in a standard Docker deployment. Very likely, since Jellyfin itself
does exactly this for transcoding, but should be confirmed before ADR-008 is
final.

---

## 13. Licensing implications

| Project | License | Our intended use |
|---|---|---|
| jellyfin-ambilight | **GPL-3.0-or-later** | architectural study; possible adaptation of extraction logic and device-mapping patterns |
| HyperHDR | **MIT** (root `LICENSE`, awawa-dev 2020-2026; 8 non-MIT files confined to FTDI/amlogic/ESPixelStick paths) | algorithm study: black-border processor, smoothing model |
| Jellyfin server | **GPL-2.0** | plugin API consumer only, via NuGet packages |
| jellyfin-ffmpeg | **GPL-3.0** (built `--enable-gpl --enable-version3 --enable-libfdk-aac`) | invoked as a separate process |
| WLED | **EUPL-1.2** | protocol study only; no code reuse |

This repository is already **GPL-3.0**, which is the correct and compatible
choice given that GPLv3 material from `jellyfin-ambilight` may be adapted.

Key obligations:

- Any code adapted from `jellyfin-ambilight` must be attributed at the file
  level and must retain GPLv3. Section 7: "Do not disguise copied code."
- The jellyfin-ffmpeg build links **libfdk-aac**, which makes that *binary*
  non-redistributable under GPL terms. We invoke it as a separate process and
  never redistribute it — this is exactly why calling the system's existing
  jellyfin-ffmpeg (rather than bundling one) is also the cleanest licensing
  position. Worth stating explicitly in `licensing.md`.
- HyperHDR is **MIT**, not LGPL-3.0 as this document previously stated. That is a
  materially *looser* constraint than the project had been assuming: adapting its
  code requires only attribution and the licence text, not copyleft. The eight
  non-MIT files sit under FTDI/amlogic/ESPixelStick paths we have no reason to
  touch, but any file adapted must still be checked individually.

`docs/architecture/licensing.md` is a separate deliverable per section 7 and is
not yet written.

---

## 14. Open questions for the operator

| # | Question | Status |
|---|---|---|
| Q1 | Upgrade the dev host from 10.11.9 to 10.11.11? | **Answered 2026-09-05: no.** Make it work from 10.11.9. See §1. |
| Q2 | `PlaybackProgress` reporting cadence of the TCL Android TV client. | **Answered 2026-09-05: operator does not know.** I must measure it myself using the dev API key. |
| Q3 | Which files in the library have DV **P5** and **P7**? | **Answered 2026-09-05: operator cannot determine this.** Instead he named one reference asset — see §18. P5 is covered; **P7 has no known sample**, so §72 will ship with P7 listed as untested. |
| Q4 | WLED master brightness and ABL settings on wled-livingroom. | **Answered 2026-09-05: operator will help test.** ⚠ Controller is currently **unplugged and unreachable** — no WLED work possible until he restores power. |
| Q5 | May the disabled plugin DLLs in `/root/jellyfin-plugin-disabled/` be decompiled as research input? | **Answered 2026-09-05: yes.** Not yet performed — see below. |
| Q6 | Jellyfin API key for the dev server. | **Answered 2026-09-05:** key issued for jellyfin-dev. Held outside the repository. |

### Q5 follow-up — decompilation approved, not yet performed

The operator has approved decompiling the disabled predecessor plugins in
`/root/jellyfin-plugin-disabled/`:

| Plugin | Artefacts |
|---|---|
| `Ambilight_1.8.0.disabled` | `Jellyfin.Plugin.Ambilight.dll`, `.pdb` |
| `Ambilight Hyperion_0.1.2.disabled` | `Jellyfin.Plugin.Ambilight.dll`, `.pdb`, `runtimeconfig.json` |

Version `0.1.2.0` is the one that wrote the 89 GB of `.amb3` data in
`/ambilight/`, so its extraction and LED-mapping code is the direct ancestor of
the data on disk. This is queued as the next research increment; it requires the
.NET SDK and a decompiler (`ilspycmd`), neither of which is installed yet.

Note on licensing: both are GPLv3 derivatives, so reading decompiled output is
permissible, but anything **adapted** from it carries the same attribution and
license obligations as adapting the source directly (§13).

### Q6 follow-up — credential handling

An API key for the development server (jellyfin-dev) has been issued. It is stored
in the session scratchpad, outside the repository, and must never be committed.
It is scoped to the dev host only. Production Jellyfin (jellyfin-prod) remains
untouched.

## 15. Assumptions requiring prototype validation

| # | Assumption | Status |
|---|---|---|
| A1 | QSV 4K HEVC Main10 decode + on-GPU downscale does not measurably affect concurrent Jellyfin playback. | **Open.** Hardware now known: Intel **Core i5-10500T** (Comet Lake), UHD 630, in an LXC on PVE. Still needs measurement under real concurrent playback. |
| A2 | `vpp_qsv` suffices for HDR→SDR versus needing `libplacebo`/Vulkan. | **Resolved 2026-09-05.** iGPU is UHD 630 (**Gen9.5**). `vpp_qsv` tone-maps only "if the input has HDR metadata", which the P5 reference asset lacks — so it would do nothing. Colour path is **libplacebo over Vulkan**. |
| A3 | Dolby Vision **Profile 5** handling. Highest-risk item in the colour pipeline. | **Resolved favourably 2026-09-05.** `libplacebo apply_dolbyvision=true` applies the RPU. See §18 and ADR-006. Visual confirmation outstanding. |
| A4 | Child-process spawning from a plugin is unrestricted in standard Docker. | **Open.** |
| A5 | **New.** Multi-planar `hwdownload` (nv12) returns empty chroma on Mesa anv 25.0.7 / Gen9.5. The decoder must request packed `bgra`/`rgba`. | **Confirmed as a defect 2026-09-05**, worked around. |

## 16. Risks

| # | Risk |
|---|---|
| R1 | Jellyfin 12.0 ships during the project; v1 targets 10.11.9 / `net9.0`, 12.0 needs `net10.0`. Already at rc7. |
| R2 | DV P5 may have no acceptable fallback on this stack; section 30 forbids claiming untested support. |
| R3 | WLED `DMXAddress`, `arlsOffset`, `realtimeOverride` and ABL can each silently alter or suppress output. All need first-run detection. |
| R4 | The wled-livingroom controller is shared with production. Every realtime test needs operator coordination; this constrains the test schedule. |

---

## 17. Operator verification log

Master prompt: nothing requiring visual confirmation may be recorded as passed on
the assistant's own judgement. This table is the permanent record.

| Date | Question asked | Operator answer | Result |
|---|---|---|---|
| 2026-09-05 | "Is red actually red, green green, blue blue? If red appears green the colour order is wrong." | "rood ok, groen ok, blauw ok" | **PASS** — colour order correct, no channel swap. Send RGB in natural order. |
| 2026-09-05 | "This is white from R+G+B only, with the SK6812 white chip off. Does it look usable, or clearly cold/blue/dirty? This decides RGB24 vs RGBW32." | "wit ok" | **PASS** — **RGB24 is sufficient.** RGBW32 not required, so 832 LEDs stay at 2 packets per frame. |
| 2026-09-05 | "For each colour block, where is it physically: top / right / bottom / left?" | "rood boven, groen rechts, blauw onder, geel links" | **PASS** — layout confirmed, see below. |
| 2026-09-05 | "Which way does the chase run, clockwise or counter-clockwise from your seat, and in which corner does it start?" | "chase begint linksboven en loopt met de klok mee" | **PASS** — index 0 is the **top-left** corner, direction **clockwise**. |
| 2026-09-05 | Per-corner: "is the white marker exactly on the corner?" (markers at 264 / 414 / 680 / 831) | "onderkant heeft de witte led precies de hoek. Rechtsboven ook. LB lijkt het juist geel op de hoek" | **3 of 4 PASS** — 264, 414 and 680 exact; top-left wrong. |
| 2026-09-05 | Colour ruler at the top-left, six LEDs three apart: "which colour sits on the corner?" | "blauw. meer 2 leds verder naar het uiteinde zit de hoek" | Corner located at **index 830**. |
| 2026-09-05 | Operator hypothesis: "misschien is de strip ook maar 830 lang?" Last seven LEDs lit individually: "which do you see?" | "magenta zie ik inderdaad niet. ik zie geel nog wel" | ⭐ **Strip is 831 LEDs**, not 832. Controller was misconfigured. |
| 2026-09-05 | Asked the operator to set the WLED length to 831 | "aangepast naar 831" | Verified by read-back: `leds.count: 831`, segment `0..831`. |
| 2026-09-05 | Final check, white markers on 264 / 414 / 680 / 830 at strip length 831: "are all four exactly on their corner?" | "de witte markeringen zitten op de juiste plekken" | ✅ **Mapping definitive.** §68 edge mapping complete for this installation. Frame is 2493 bytes in **2 DDP packets**. |

### Confirmed physical layout — reference installation

Operator-verified per-corner on 2026-09-05, then re-verified after correcting
the controller's LED count. This is a known-good fixture for the §68
edge-mapping tests and the default the wizard should propose for this device.

| Logical index range | Count | Physical side |
|---|---|---|
| 0 – 264 | **265** | **top** |
| 265 – 414 | **150** | **right** |
| 415 – 680 | **266** | **bottom** |
| 681 – 830 | **150** | **left** |

- Start corner: **top-left**, direction **clockwise**
- **Total: 831**, one contiguous bus on GPIO 16, one segment
- Strip density **144 LEDs/m** (6.94 mm pitch), perimeter ~1.84 x 1.05 m,
  i.e. roughly an 83-inch panel

### How the corner boundaries were measured

Block-pair patterns (10 LEDs of colour A against 10 of colour B) proved to be a
poor instrument: the strip washes the wall, so adjacent colours blend over tens
of centimetres and the operator can see *that* there is a boundary but not
*where*. A photograph of the edge test showed the same limitation.

The instrument that worked was a **single bright white LED placed exactly on the
boundary index, with the flanking colours dimmed to ~18%**. That makes the
question binary: is the white dot on the corner, yes or no. Where it was not, a
**colour-coded ruler** — six single LEDs three apart, each a different colour —
let the operator name the exact index of the corner in one round.

Both techniques should become standard steps in the §47 calibration wizard.

### ⭐ The controller's reported LED count was wrong

WLED reported `leds.count: 832`. Lighting the last seven LEDs individually in
distinct colours showed **index 830 lit and index 831 dark**: the strip is
physically **831 LEDs**, and the controller was configured with one phantom LED.
(Whether LED 831 is absent or simply dead is indistinguishable and immaterial —
831 is the usable count either way.)

The operator corrected the WLED configuration to 831; `leds.count: 831` and
segment 0 spanning `start 0 stop 831` were confirmed by read-back.

**Design consequences, and the reason this matters far beyond one installation:**

1. **A controller's reported LED count is a *proposal*, not ground truth.** Had
   the plugin trusted the API, the entire perimeter mapping would have been
   silently off by one LED — subtle enough to be dismissed as "the corner is a
   bit off" and never diagnosed.
2. **The calibration wizard must verify length, not just orientation.** §47 lists
   colour, edge and chase tests; it does not mention length. The "last N LEDs in
   distinct colours" test should be added as a standard step.
3. **The plugin must tolerate a configured length longer than the physical
   strip** without misbehaving — surplus pixels simply go nowhere.
4. §39 forbids the plugin from rewriting WLED configuration, so the correct
   behaviour is to **detect and report** the mismatch and let the user decide.
   Here the operator made the change himself.

The old `.amb3` sidecars recorded 266/150/266/150 = 832. Now that the true count
is known to be 831, the predecessor's layout was wrong on three of four sides —
its transport was not the only thing it got wrong.

### Pending visual confirmations

Nothing below may be recorded as passing without the operator's own observation.

| Item | Requirement | Status |
|---|---|---|
| DV P5 colour correctness on the reference asset | §30, §32, §72 | needs the operator watching the film with LEDs live |
| ABL dimming on the all-white calibration pattern | §47, §55 | measured numerically; visual impact not yet judged |
| Exact corner alignment of segment boundaries | §44, §68 | not yet tested per-LED |
| Perceived latency and smoothing quality | §41, §43 | needs §66 measurement with a phone camera |
| Black-border behaviour on dark scenes | §33, §56 | needs the operator watching |

### Equipment status

**2026-09-05 — controller unplugged by the operator, then restored.** It is now
powered and reachable. A **read-only** survey via `GET /json/info`, `/json/state`
and `/json/cfg` was performed; the results are recorded in
[ADR-004](adr/ADR-004-wled-realtime-protocol-strategy.md). No realtime packets
have been sent.

Q4 is largely answered by that survey without needing eyes: master brightness is
`128` with `maxbri=false`, so realtime output is currently halved, and ABL is
capped at 40 A which will visibly dim the all-white calibration pattern. What
remains genuinely visual is how the RGBW strip *looks* under RGB24 versus
RGBW32, and whether the ABL dimming is objectionable in practice.

⚠ **Before any realtime transmission to wled-livingroom, the operator must confirm that
nothing is playing on production.** The controller is shared and accepts only one
realtime consumer at a time. `info.live` was `false` at survey time, which is
consistent with an idle controller but is not a substitute for his confirmation.

No packets have been sent to wled-livingroom. Production Jellyfin at jellyfin-prod has not
been contacted.

---

## 18. Reference test asset — DV Profile 5

The operator could not define a test matrix (Q3) and instead named one film to
make work first. It turned out to be the hardest case in the HDR table, not a
soft start.

```
/films2/Apocalypse Z The Beginning of the End (2024)/
  Apocalypse.Z.The.Beginning.of.the.End.2024.2160p.WEB-DL.DV.P5.
  ENG.SPANISH.ITALIAN.HINDI.DDP5.1.H265.MP4-BEN.THE.MEN.mp4
```

| Property | Value |
|---|---|
| Container / codec | MP4 (`hev1`), HEVC **Main 10**, `yuv420p10le` |
| Resolution / rate | 3840×2160 @ 24 fps, 169,954 frames, 118 min |
| Size / bitrate | 20 GB / ~19.9 Mbit/s video |
| `dv_profile` | **5** |
| `dv_bl_signal_compatibility_id` | **0** — no cross-compatible base layer |
| `rpu_present_flag` / `el_present_flag` | 1 / 0 (single layer) |
| Colour metadata | `color_space`, `color_transfer`, `color_primaries` **all `unknown`**; `color_range=pc` |
| Sidecars | `.nl.srt`, `.en.srt`, plus a trailer `.mkv` |

The missing colour metadata is the P5 signature and is exactly why a naive
pipeline goes wrong: there is nothing in the container to key off.

Validation results, hardware findings and the resulting design constraints are
recorded in [ADR-006](adr/ADR-006-hdr-dolby-vision-normalization.md). In summary:
`libplacebo apply_dolbyvision=true` works; the frame must be downloaded as
packed `bgra`/`rgba` because multi-planar download is broken on this driver; and
`vpp_qsv` is not a viable colour path on this hardware.

The `.srt` sidecars make this asset useful for the §34 subtitle test as well:
because analysis reads the original video stream, these must have no effect on
LED colour.

**P7 has no known sample in the library.** Per §30 and §72 the shipped
compatibility table will list P7 as untested rather than claim support.
