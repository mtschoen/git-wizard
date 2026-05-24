# git-wizard — handoff

**Last refreshed:** 2026-05-23. Supersedes the 2026-04-26 Avalonia-migration
handoff (that migration has fully landed).

## Status

All major workstreams are merged on `main`:

- **Core library + CLI** — cross-platform repo scanning, JSON batch reports,
  projdash integration. Latest release: **v0.4.1**.
- **Avalonia desktop UI** (`GitWizardAvalonia/`) — runs natively on
  Windows/macOS/Linux, built on the shared view models in
  `GitWizardUI.ViewModels/`. The migration plan is fully executed.
- **MAUI desktop UI** (`GitWizardUI/`) — Windows app, kept building alongside
  Avalonia (shares the same view models).
- **Gitea Actions CI** — `ci.yml` (Linux build + tests, Windows full build +
  tests) and `release.yml` (cross-platform CLI + Avalonia + MAUI artifacts on
  `v*` tags). Complete.
- **Per-branch divergence** — report schema 2.0, `--all-branches` flag (#41).

This file is now just a pointer. Active work lives in **PLAN.md** and in
**Gitea issues**: https://gitea.llamabox.internal/schoen/git-wizard/issues

## What's left

- **CLI `-merge` flag** — single-repo report merge for the projdash fallback
  path. Self-contained, no hardware dependency — **best next code task**.
  Tracked in [#42](https://gitea.llamabox.internal/schoen/git-wizard/issues/42)
  and PLAN.md → "Single-repo merge refresh".
- **Trust llamabox cert on the Windows CI runner** — drop the
  `NODE_TLS_REJECT_UNAUTHORIZED=0` workaround in `ci.yml`/`release.yml` once the
  self-signed Caddy cert is in the runner's trust store (PLAN.md →
  Infrastructure).
- **macOS testing** — needs Mac hardware. (Avalonia runtime verified on Linux
  and Windows 2026-05-23; macOS is the remaining untested platform.)
- Open Gitea issues: #40 (merged-branch cleanup UI), #39 (stale PNG decision),
  #37 (mirror releases to GitHub), #36 (coverage gate + ViewModel test
  backfill), #34 (submodule health detection).

## Conventions / gotchas

- C# / .NET 10 / **NUnit** (xUnit assertions are NOT used in this repo).
- `dotnet build` works on Linux/macOS via the MFTLib NuGet PackageReference.
  Swapping MFTLib to a local ProjectReference requires VS2026 MSBuild on
  Windows — see `CLAUDE.md`.
- Keep MAUI building — Windows users depend on it; Avalonia and MAUI share
  `GitWizardUI.ViewModels/`.
- Local config/cache lives in `~/.GitWizard/` (`config.json`,
  `repositories.txt`, `report.json`).
