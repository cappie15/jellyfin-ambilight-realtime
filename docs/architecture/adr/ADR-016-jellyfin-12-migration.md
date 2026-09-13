# ADR-016 — Retargeting to Jellyfin 12 / .NET 10

- **Status:** Implemented and deployed to the reference host
- **Date:** 2026-09-13
- **Relates to:** [ADR-009](ADR-009-zero-touch-installation.md) (the original v1 target, `net9.0` / Jellyfin 10.11.9, and its own Am. 2 note that "Jellyfin 12.0 support is a follow-up decision")

## Context

Jellyfin 12.0 shipped (not merely announced) and the operator's own reference
host was upgraded to it. Jellyfin 12 requires .NET 10 at minimum, up from
.NET 9; a plugin assembly targeting a newer .NET than its host process cannot
load at all, so this could not be prepared and shipped speculatively ahead of
time the way most changes in this project are — the actual server upgrade had
to happen first, and the plugin had to wait for it.

Before retargeting, the plugin's own source was checked against Jellyfin 12's
published breaking-change list (`ISearchEngine`, `IAuthenticationProvider.HasPassword`,
several `IItemRepository`/`IUserManager` members, alternate-versions/playlist
storage, several removed HTTP routes). None of it is used anywhere in this
project.

## Decision — retarget once the real upgrade happens, not before; no code changes

1. `Directory.Build.props`: `TargetFramework` `net9.0` → `net10.0`.
2. Both `Jellyfin.Plugin.RealtimeAmbilight.csproj` and the test project:
   `Jellyfin.Controller`/`Jellyfin.Model`/`Jellyfin.Common` package references
   `10.11.9` → `12.0.0`.
3. `.github/workflows/ci.yml`: `actions/setup-dotnet` `dotnet-version`
   `'9.0.x'` → `'10.0.x'`.
4. `plugin-package/meta.json`: `targetAbi` `10.11.0.0` → `12.0.0.0`.
5. No changes anywhere in `src/` or `tests/` — confirmed by a clean build and
   all 219 tests passing unmodified against the new target, both locally in
   an isolated worktree ahead of the operator's own upgrade (proving the
   retarget was ready) and again on `main` after the merge (proving nothing
   regressed in the merge itself).

The retarget itself was prepared and verified on a separate branch
(`feat/jellyfin-12`) days before the operator's own upgrade, specifically so
it would not sit half-finished once the "go" signal came — but that branch
was deliberately never merged to `main` or deployed to the reference host
until the host was actually running Jellyfin 12, per the loading constraint
above. Once the operator confirmed the upgrade, the branch was merged, built,
deployed, and only then tagged and released as v0.4.0 (see the repository
manifest and the v0.3.0 entry immediately before it in `manifest.json` for
the two ABIs installable side by side).

## Consequence

- v0.4.0 onward targets Jellyfin 12 / `net10.0` / `targetAbi` `12.0.0.0` only.
  Installations still on Jellyfin 10.11.x should stay on v0.3.0 — the
  repository manifest lists both, newest first, so Jellyfin's own catalog can
  offer either depending on which ABI the running server actually satisfies.
- The ten other plugins installed on the reference host were not all
  upgrade-safe: `Intro Skipper` (targeting an older ABI) failed to load under
  Jellyfin 12 with a `TypeLoadException` on a method Jellyfin 12 removed the
  implementation contract for, and was disabled automatically by Jellyfin
  itself. This plugin's own clean load after the upgrade was verified in the
  Jellyfin log, not assumed from the successful build alone.
- No compatibility shim, dual-targeting, or `net9.0`/`net10.0` multi-targeting
  was introduced — ADR-009 §5 already rejected that shape of complexity for
  the original 10.11.9 decision, and the same reasoning applies here: one
  supported target at a time, matched to whichever server this project is
  actually run against.
