# Codex handover review

Reviewed 2026-09-06 against commit `16b02fa8f8bcfed698cdb46daa9d8054b96560fb`.
The worktree was clean. The repository is an architecture-and-research baseline,
not an active plugin implementation.

## BLOCKER

### B-001 — No end-to-end plugin implementation exists

- **Location:** the initial bootstrap under `src/` provides only an entry point,
  configuration root and packet serializers; no lifecycle, media or network
  service exists.
- **Why it matters:** There is no playback handler, decoder, WLED sender,
  configuration UI, manifest or package. None of the requested end-to-end
  product behaviour can run.
- **Smallest safe solution:** Scaffold a minimal `net9.0` Jellyfin plugin and
  test project without attempting the full product. Establish the plugin entry
  point, configuration, service registration and a buildable package first.
- **How to test:** Restore, build and run unit tests with the .NET 9 SDK; copy
  the generated plugin into a disposable Jellyfin 10.11.11 instance and verify
  that the plugin loads without affecting playback.

### B-002 — No package, CI or release path exists

- **Location:** repository root; a solution, projects and initial test project
  now exist, but there is no `manifest.json`, workflow, package target or
  release automation.
- **Why it matters:** The product cannot be installed from a Jellyfin repository
  or updated. The stated normal-user installation path is impossible.
- **Smallest safe solution:** Add solution/project files, deterministic
  `dotnet test` and `dotnet pack` targets, repository manifest generation, and a
  CI workflow before publishing any plugin build.
- **How to test:** Run the documented build/test/package commands on a clean
  .NET 9 environment; inspect the packaged archive and install it through a
  disposable Jellyfin repository.

## HIGH

### H-001 — Accepted transport policy conflicts with the product specification

- **Location:** [ADR-004](../architecture/adr/ADR-004-wled-realtime-protocol-strategy.md),
  Decision and protocol-selection table.
- **Why it matters:** ADR-004 says “Hyperion raw RGB is not used.” The product
  requires Hyperion Raw RGB as the low-latency baseline plus Auto, Raw RGB and
  DDP choices. A DDP-only implementation would remove the requested benchmarked
  low-LED path and expert override before either protocol has been compared by
  this product.
- **Smallest safe solution:** Amend ADR-004 before transport code lands: retain
  Raw RGB for frames representable in one safe UDP datagram, implement DDP as
  the multi-packet scalable option, expose both expert modes, and make Auto use
  measured capability/latency results.
- **How to test:** Unit-test Raw RGB rejection above its safe datagram ceiling
  and DDP packet construction at 100, 490, 491, 831/832 and 1000+ LEDs. Measure
  end-to-end latency on the same controller and record the Auto selection.

**Status after review:** ADR-004 Amendment 3 corrects the policy, and initial
packet tests implement the stated boundaries. Auto selection, UDP transmission
and latency measurement remain unimplemented.

### H-002 — Target-version decision is stale against “latest stable only”

- **Location:** [ADR-009](../architecture/adr/ADR-009-zero-touch-installation.md),
  “Target version”; [research.md](../architecture/research.md), §1.
- **Why it matters:** The research identifies Jellyfin 10.11.11 as the current
  stable release, while ADR-009 pins to 10.11.9. The master requirement targets
  only latest stable; beginning new code on the older ABI creates an intentional
  compliance gap.
- **Smallest safe solution:** Pin the initial project to Jellyfin.Controller and
  Jellyfin.Model 10.11.11 and revise ADR-009/research once restore confirms the
  packages. Do not add multi-version compatibility layers.
- **How to test:** Restore/build against 10.11.11 and load the package into a
  disposable 10.11.11 server.

**Status after review:** ADR-009 Amendment 2 and the initial project now target
10.11.11. Restore and Release build now pass with zero warnings/errors; loading
the assembly in a Jellyfin 10.11.11 server remains outstanding.

### H-003 — The master specification is not retained in the repository

- **Location:** repository-wide search; ADRs only cite “master prompt §…”.
- **Why it matters:** Another agent or contributor cannot reconstruct complete
  requirements from the repository. ADRs are useful decisions but omit many
  acceptance criteria and can drift from the product source of truth.
- **Smallest safe solution:** Add the approved specification, or an immutable
  requirements document with an explicit source/version and complete mapping to
  ADRs, under `docs/`.
- **How to test:** On a clean clone, verify that a contributor can locate the
  full requirements and trace every vertical-slice acceptance criterion without
  chat history.

### H-004 — HDR/DV evidence is not executable validation

- **Location:** [ADR-006](../architecture/adr/ADR-006-hdr-dolby-vision-normalization.md)
  and [research.md](../architecture/research.md), §18.
- **Why it matters:** The measured command-line experiments do not protect a
  future plugin from regressing colour conversion, argument ordering, packed
  downloads, or unsafe QSV selection. Dolby Vision P7 is also explicitly
  untested.
- **Smallest safe solution:** Encode the validated command builders and backend
  choice in unit tests. Add reproducible fixture metadata and mark profiles as
  supported only after actual implementation and visual validation.
- **How to test:** Assert command arguments for SDR/HDR10/HLG/DV P5, including
  input-side seek and pinned BT.709 output; run the P5 fixture through the
  worker and compare sampled output to an approved reference. Keep P7 listed as
  untested until it has a fixture and operator check.

## MEDIUM

### M-001 — Sampling ADR remains proposed

- **Location:** [ADR-010](../architecture/adr/ADR-010-sampling-zone-model.md).
- **Why it matters:** Its geometry becomes persisted user-visible behaviour once
  code and calibration ship, but it has not received the stated operator review.
- **Smallest safe solution:** Implement it behind explicit versioned defaults and
  document the status; accept or revise it before treating its values as fixed.
- **How to test:** Deterministic tests should validate rectangles, no zero-width
  zones, asymmetric crops, corner overlap and linear-light averaging.

### M-002 — Recorded controller configuration is a reference fixture, not a
portable contract

- **Location:** [ADR-004](../architecture/adr/ADR-004-wled-realtime-protocol-strategy.md),
  controller survey and amendments.
- **Why it matters:** Firmware changed RGBW and brightness settings without user
  intervention. A one-time setup probe will silently produce altered colours.
- **Smallest safe solution:** Re-probe relevant WLED state at session start and
  surface incompatible drift without writing device configuration.
- **How to test:** Mock state changes between setup and playback; verify a
  diagnostic warning and that no WLED configuration write occurs.

## LOW

### L-001 — Repository ignore rules are generic and do not yet cover release
artifacts

- **Location:** `.gitignore`.
- **Why it matters:** Once packaging begins, generated plugin archives and
  manifests may be accidentally committed.
- **Smallest safe solution:** Add narrowly scoped package-output ignore entries
  when the actual build layout is chosen.
- **How to test:** Run packaging and confirm `git status --ignored` identifies
  generated outputs while source manifests remain tracked.
