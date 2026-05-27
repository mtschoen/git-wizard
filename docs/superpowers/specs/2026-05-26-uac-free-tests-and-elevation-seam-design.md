# Design: Wizard-family self-elevation testability (MFTLib 0.3) + UAC-free git-wizard tests

**Date:** 2026-05-26
**Branch:** `retire-maui-rename-ui` (PR #51) for the git-wizard portion; MFTLib portion on a 0.3 dev branch.
**Source:** `docs/inspection-cleanup-handoff.md` → "TODO — UAC prompts during `dotnet test`", expanded by the decision to make the elevation seam a shared MFTLib 0.3 API (DRY across git-wizard + file-wizard).
**Cross-repo context:** see `~/.claude/notes/project_mftlib_wizard_family.md` and `~/.claude/notes/idioms_mftlib_elevation_testing.md`.

## Problem

1. Running `dotnet test` non-elevated on Windows pops ~4 UAC dialogs (can hang unattended). Root cause: `GitWizardReport.GetRepositoryPaths(..., noMft:false)` → `GitWizardApi.TryFindAllRepositoriesUsingMft` → on non-elevated Windows it **unconditionally** calls `ElevationUtilities.TryRunElevated("--elevated-mft …")` (the spawn doesn't depend on search paths — the elevated child does the scan). CI is unaffected (Windows runner is elevated).
2. Self-elevation has **no dedicated test** and isn't independently coverable: `MFTLib.ElevationUtilities` (NuGet **v0.2.0**) is a static class with 4 plain statics and no test seam, so consumers can't fake the elevation decision.
3. Pre-existing isolation bug: `SettingsViewModelTests` (and other classes) read/write the **real** `~/.GitWizard`, causing order-dependent failures.

The fix for (2) should be **DRY** — the seam belongs in MFTLib (consumed by git-wizard *and* file-wizard), not duplicated per consumer. MFTLib 0.3 is intentionally unshipped pending exactly this consumer-facing API surface.

## Goals

- `dotnet test` non-elevated → **zero** UAC prompts.
- Self-elevation decision logic (incl. the *already-elevated* branch) is testable without real UAC, via a shared MFTLib abstraction.
- Tests stop touching the real `~/.GitWizard`.
- **No regression of actually-covered lines/branches**; new code covered to 100% in principle; real-privilege code covered via interactive (RequiresAdmin) tests + a self-elevating coverage run, per the MFTLib pattern (0 `[ExcludeFromCodeCoverage]`).

## Non-goals

- file-wizard changes (future consumer of the same MFTLib API; out of scope here).
- A CI coverage-model overhaul (wiring elevated-Windows coverage into the gate). The CI Linux 45% gate stays as the cheap regression floor; the real cross-platform/elevated coverage truth is proven by the human-run `run-coverage.ps1` and recorded in `TEST-REPORT.md`.
- Carry-forwards (validation, not code): GUI smoke-test of the context-menu `DataContext` fix; Rider naming-warning confirm.

## Constraints discovered

- MFTLib v0.3-pre source (`~/MFTLib`) already has **internal** Func seams (`IsWindows`, `GetProcessPathFunc`, `StartProcess`) + `ResetToDefaults()`, used by MFTLib's own MSTest suite. They are internal and **cannot force `IsElevated()==true`** (real Windows token check) — so they don't let a *consumer* test its already-elevated branch. Hence a public injectable abstraction is needed.
- MFTLib uses **MSTest**; git-wizard uses **NUnit**. Translate, don't copy.
- git-wizard runs NUnit **sequentially** (no `[Parallelizable]`), so a per-test `GITWIZARD_HOME` env var is safe. No `[InternalsVisibleTo]` exists.
- The elevated child is passed `--config-path`/`--output` **explicitly**, so a `GITWIZARD_HOME` redirect in the parent is self-consistent and doesn't affect the elevation handshake.
- `TryFindAllRepositoriesUsingMft` returns at `!IsOSPlatform(Windows)` before consulting elevation → its decision logic is inherently Windows-only.

## Design

### Part A — MFTLib 0.3 (shared package)

Add a public elevation abstraction; keep the internal Func seams internal.

```csharp
namespace MFTLib;

public interface IElevationProvider
{
    bool IsElevated();
    bool CanSelfElevate();
    bool TryRunElevated(string arguments, int timeoutMs = 60000);
}
```

A public default implementation backed by the existing statics, exposed as a singleton:

```csharp
public static class ElevationUtilities      // existing static class
{
    public static IElevationProvider DefaultProvider { get; } = new RealElevationProvider();

    sealed class RealElevationProvider : IElevationProvider
    {
        public bool IsElevated() => ElevationUtilities.IsElevated();
        public bool CanSelfElevate() => ElevationUtilities.CanSelfElevate();
        public bool TryRunElevated(string arguments, int timeoutMs = 60000)
            => ElevationUtilities.TryRunElevated(arguments, timeoutMs);
    }
}
```

- MFTLib's own MSTest suite covers `RealElevationProvider`'s delegation using the existing internal Func seams (e.g. `IsWindows = () => false` ⇒ `DefaultProvider.IsElevated()` is false; `StartProcess` fake ⇒ `TryRunElevated` outcomes) — preserving MFTLib's 100% / 0-exclusions standard.
- Version → **0.3.0**. Shipped only **after** git-wizard validates the surface against it.
- file-wizard will later consume the same `IElevationProvider`.

### Part B — git-wizard consumes the abstraction

Develop against MFTLib 0.3 via the documented **local ProjectReference swap** (`GitWizard/GitWizard.csproj`, build with VS2026 MSBuild `Platform=x64`). Flip back to `PackageReference Version="0.3.0"` before committing/publishing.

Thread an optional provider through the two consumers, defaulting to the real one:

- `GitWizardApi.TryFindAllRepositoriesUsingMft(GitWizardConfiguration, ICollection<string>, IUpdateHandler? = null, bool noMft = false, IElevationProvider? elevation = null)` — `elevation ??= ElevationUtilities.DefaultProvider`; route `IsElevated()` / `TryRunElevated(...)` through it.
- `WindowsDefenderException.AddExclusions(IElevationProvider? elevation = null)` — same, over `IsElevated()` / `CanSelfElevate()` / `TryRunElevated(...)`.

Production callers (`git-wizard/Program.cs`, `GitWizardUI/.../MainWindow.axaml.cs`, `GitWizardReport.GetRepositoryPaths`) pass nothing → unchanged behavior.

New `GitWizardTests/ElevationDecisionTests.cs` with a NUnit `FakeElevationProvider` spy (records `TryRunElevated` calls; settable `IsElevated`/`CanSelfElevate`/result):

- **Defender, cross-platform (Linux CI too; never spawns):** not-elevated + can-self-elevate ⇒ `TryRunElevated("--elevated-defender", …)` invoked, `AddExclusions` returns the fake's result.
- **MFT, `[Platform("Win")]` (runs on the elevated Windows runner; fake controls elevation):**
  - not-elevated ⇒ `TryRunElevated` invoked with an arg string starting `"--elevated-mft --config-path "`.
  - elevated + **empty** search paths ⇒ `TryRunElevated` **not** called (proves direct-scan branch; empty paths ⇒ no real `MftVolume.Open`).

### Part C — UAC fix for the 4 trigger tests (test-only)

- `RefreshConcurrencyTest`: **drop the `GetRepositoryPaths` discovery call**; seed `repositoryPaths` directly from 1–2 `TempRepoFixture.CreateWithInitialCommit()` repos (the pattern every sibling `Refresh_*` test already uses), then hammer `Refresh` in parallel. No discovery ⇒ no elevation; the test's actual subject (parallel `Refresh`) is unchanged.
- `GetRepositoryPaths_WithEmptySearchPaths_DoesNotThrow`, `GetRepositoryPaths_EmptyConfiguration_FindsNothing`, `GetRepositoryPaths_WithNonExistentSearchPath_DoesNotThrow`: pass `noMft: true`. Search paths are already empty/nonexistent, so the recursive fallback is a no-op and assertions still hold — now matching the test names.
- (`GeneratedReport_DefaultsBranchScopeToActionable` is **not** a trigger — it passes a non-null empty list, so `GetRepositoryPaths` is never called. No change.)

### Part D — `GITWIZARD_HOME` config-home redirect

- `GitWizardApi.GetLocalFilesPath()`: if env `GITWIZARD_HOME` is set+non-empty, return it as the data dir; else `~/.GitWizard`. (Bonus: relocatable config.)
- `TestUtilities`: `RedirectLocalFilesToTemp()` (mk temp dir, set env, return path) + `ClearLocalFilesRedirect(string? temp)` (clear env, delete dir).
- Migrate classes that write the real `~/.GitWizard` onto the redirect in SetUp/TearDown: `SettingsViewModelTests` (the known bug), `AsyncFileIOTests` (retire its delete-the-real-file hack), and the cache-writing tests in `GitWizardReportAdditionalTests` (`GetCachedReport*`, `…_InvalidJson`). Default-behavior assertions (e.g. `GetLocalFilesPath_ContainsUserProfile`) do **not** set the env var.

### Part E — Coverage apparatus (mirror MFTLib)

- Real-privilege git-wizard code (`TryFindGitRepositoriesUsingMft` real `MftVolume` scan, `RunElevatedMftScan`, `WindowsDefenderException.RunDefenderCommands` / `RunDefenderCommandsViaElevatedPowerShell`): `[Category("RequiresAdmin")]` + guard `if (!ElevationUtilities.IsElevated()) Assert.Inconclusive("Requires admin");`.
- New `scripts/run-coverage.ps1` (translated from MFTLib's): default runs non-admin tests then **self-elevates** (`Start-Process -Verb RunAs`, one UAC) to run `Category=RequiresAdmin` and `MergeWith` their coverage; `-NonInteractive` skips them (`--filter "Category!=RequiresAdmin"`) for CI/headless.
- Root `TEST-REPORT.md`: git hash + test count + line/branch/method coverage + exclusion count, recording the real (elevated-Windows) coverage truth.

## Verification

- `dotnet test` non-elevated on Windows → zero UAC prompts.
- `SettingsViewModelTests` run as an isolated subset → both previously-failing tests pass.
- New elevation decision tests green (Defender everywhere; MFT on Windows); RequiresAdmin tests pass under the self-elevating run.
- **No-regression check:** capture the covered-line/branch set on the pre-change baseline and after; any line my changes uncover gets an explicit new test. CI Linux gate stays ≥ 45%.
- MFTLib stays 100% / 0 exclusions after adding `IElevationProvider`/`DefaultProvider`.

## Sequencing

1. MFTLib 0.3: add `IElevationProvider` + `DefaultProvider` + MFTLib tests; keep 100%.
2. git-wizard: local ProjectReference swap → implement Parts B–E → tests/coverage green.
3. Ship MFTLib **0.3.0** NuGet; flip git-wizard back to `PackageReference Version="0.3.0"`; re-verify.
4. (Later) file-wizard adopts the same `IElevationProvider`.

## Files

**MFTLib:** new `MFTLib/IElevationProvider.cs` (+ `DefaultProvider` in `ElevationUtilities.cs`); new MSTest coverage for the default impl; `<Version>` → 0.3.0.

**git-wizard:**
- New: `GitWizardTests/ElevationDecisionTests.cs`, `scripts/run-coverage.ps1`, root `TEST-REPORT.md`.
- Edit: `GitWizard/GitWizardApi.cs`, `GitWizard/WindowsDefenderException.cs`, `GitWizard/GitWizard.csproj` (MFTLib ref), `GitWizardTests/TestUtilities.cs`, `GitWizardTests/GitWizardReportTests.cs`, `GitWizardTests/GitWizardReportAdditionalTests.cs`, `GitWizardTests/SettingsViewModelTests.cs`, `GitWizardTests/AsyncFileIOTests.cs`.
