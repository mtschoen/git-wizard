# Handoff - git-wizard consumes MFTLib 0.3 `IElevationProvider`

**Date:** 2026-05-26. **Branch:** `validate/mftlib-0.3-elevation` (local, unpushed WIP).
**Design spec:** `docs/superpowers/specs/2026-05-26-uac-free-tests-and-elevation-seam-design.md` (read it - this handoff only covers status + resume).

## Status: Parts B-E done + FF-merged into git-wizard local `main` (held unpushed). Pending: MFTLib 0.3.0 publish + ref flip, then `main` fast-forwards `gitea/main` green.

> **Update 2026-05-27:** the `validate/mftlib-0.3-elevation` branch was FF-merged into local `main` and **deleted**. Separately, **PR #51 (MAUI retirement) was FF-merged to `gitea/main` = `2b26b92`** the same day, so `gitea/main` already carries the rename/fold. Local `main` is now exactly **3 commits ahead** of `gitea/main` (the elevation work + the local ProjectReference) and is held **unpushed** - pushing would break CI (`dotnet` can't build MFTLib's native vcxproj). No separate git-wizard PR is needed anymore: once 0.3.0 publishes and the csproj is flipped, `main` just fast-forwards `gitea/main`.

### MFTLib side - DONE, merged, NOT published
- `IElevationProvider` + `ElevationUtilities.DefaultProvider` shipped on MFTLib **main** at commit `c25a97a` (PR #8, fast-forward merge; both gitea + github mains synced). 100% coverage, 0 exclusions.
- **The 0.3.0 NuGet is deliberately NOT published yet** (user-held decision). Until it is, git-wizard can only build against MFTLib via a **local ProjectReference** - it cannot use `PackageReference Version="0.3.0"`.

### git-wizard side - Part B done on this branch (validation only)
What's implemented and **proven green** (built via VS2026 MSBuild `Platform=x64`; 3/3 new decision tests pass with **zero UAC**):
- `GitWizard/GitWizard.csproj` - MFTLib `PackageReference 0.2.0` swapped to a **local ProjectReference** `..\..\MFTLib\MFTLib\MFTLib.csproj` with `SetPlatform="Platform=x64"`. **LOCAL DEV ONLY** - see "Before a real PR" below.
- `GitWizard/GitWizardApi.cs` - `TryFindAllRepositoriesUsingMft(..., IElevationProvider? elevation = null)`, `elevation ??= ElevationUtilities.DefaultProvider`, routes `IsElevated()` / `TryRunElevated(...)` through it.
- `GitWizard/WindowsDefenderException.cs` - `AddExclusions(IElevationProvider? elevation = null)`, same pattern over `IsElevated()` / `CanSelfElevate()` / `TryRunElevated(...)`.
- `GitWizardTests/ElevationDecisionTests.cs` - NUnit `FakeElevationProvider` spy + 3 tests: Defender not-elevated→TryRunElevated (cross-platform); MFT not-elevated→TryRunElevated (`[Platform("Win")]`); MFT **elevated + empty paths → TryRunElevated NOT called** (the already-elevated branch the internal MFTLib Func seams can't reach - this is the whole point of 0.3).

### ⚠️ Gotcha discovered (not in the spec) - applies to file-wizard too
Adding the optional `IElevationProvider?` parameter **breaks method-group-to-delegate conversions**. `GitWizardUI/Views/MainWindow.axaml.cs` had `Task.Run(WindowsDefenderException.AddExclusions)` (method group → `Func<bool>`); the optional param is not filled in method-group conversion, so it fails to compile (`CS1503`). **Fixed** by wrapping in a lambda: `Task.Run(() => WindowsDefenderException.AddExclusions())`. The two *direct* callers (`git-wizard/Program.cs`, `GitWizard/GitWizardReport.cs`) were genuinely unchanged. file-wizard should expect the same when it adopts the param.

## Parts C-E - DONE (2026-05-27, this branch)

Verified: full `dotnet test` **non-elevated** = 296 pass / 0 fail / **zero UAC** (the 2 `RequiresAdmin` tests go Inconclusive, the Unix-only config test skips). Built via VS2026 MSBuild `Platform=x64`, run `--no-build`.

- **Part C - UAC-free trigger tests (done):** `RefreshConcurrencyTest` now seeds `repositoryPaths` from two `TempRepoFixture.CreateWithInitialCommit()` repos and drops the discovery call. `GetRepositoryPaths_WithEmptySearchPaths_DoesNotThrow`, `GetRepositoryPaths_EmptyConfiguration_FindsNothing`, `GetRepositoryPaths_WithNonExistentSearchPath_DoesNotThrow` now pass `noMft: true` (short-circuits `TryFindAllRepositoriesUsingMft` before any elevation call).
- **Part D - `GITWIZARD_HOME` redirect (done):** `GitWizardApi.GetLocalFilesPath()` honors a non-empty `GITWIZARD_HOME`. `TestUtilities.RedirectLocalFilesToTemp()` / `ClearLocalFilesRedirect(string?)` added. `SettingsViewModelTests`, `AsyncFileIOTests` (its delete-the-real-file hack retired), and `GitWizardReportAdditionalTests` now redirect in SetUp/TearDown. `GitWizardApiAdditionalTests` deliberately left un-redirected (it holds the default-behavior assertions like `GetLocalFilesPath_ContainsUserProfile`, and never triggers MFT).
- **Part E - coverage apparatus (done):** `GitWizardTests/ElevationCoverageTests.cs` (`[Category("RequiresAdmin")]`, `[Platform("Win")]`) covers `RunElevatedMftScan` + the real elevated MFT scan. `scripts/run-coverage.ps1` (collector mode → Cobertura, reuses `ci/post-coverage-status.py` for the merge/summary; `-NonInteractive` / `-NoBuild` switches). Root `TEST-REPORT.md` records the methodology + measured non-admin figure (Line 41.45% / Branch 36.64%, 0 exclusions).

### ⚠️ Gotcha #2 (cost 2 real UAC fires during dev) - the `RequiresAdmin` guard MUST be in the test body, not `[SetUp]`
NUnit does **not** reliably skip the test body when `[SetUp]` calls `Assert.Inconclusive` - the body still runs, hits the real `IElevationProvider`, and **fires UAC** non-elevated. Put the guard as the first body statement, matching MFTLib's `UsnJournalLiveTests`: `if (!ElevationUtilities.IsElevated()) { Assert.Inconclusive("Requires admin"); return; }`. The cross-repo idiom note said "Assert.Inconclusive guard" generically; for NUnit it must be **body-level**. file-wizard will hit the same.

## Resume steps (what's left)

The code work (Parts B-E) is complete, validated UAC-free, and **FF-merged into git-wizard local `main`** (2026-05-27; the `validate/mftlib-0.3-elevation` branch was deleted). What remains is gated on the publish:

1. Build (while MFTLib is a local `ProjectReference`): VS2026 MSBuild, `GitWizardTests/GitWizardTests.csproj -t:Build -p:Configuration=Debug` (the `SetPlatform=x64` builds MFTLib + native; `dotnet build` can't build the native vcxproj). Then `dotnet test --no-build` - the full suite is now UAC-free non-elevated.
2. Full elevated coverage (optional, **one deliberate UAC**): `scripts/run-coverage.ps1` - runs the `RequiresAdmin` tests under self-elevation and merges their coverage. Updates the figure in `TEST-REPORT.md`.
3. **Publish gate (user-held - see `~/.claude/notes/feedback_publish_is_user_gated`):** as of 2026-05-27 MFTLib 0.3.0 is on `main` (`c25a97a`) but **not** published to NuGet (nuget.org has ≤ 0.2.0). The branch can't become a real PR until 0.3.0 publishes.
4. **When 0.3.0 publishes (no separate PR needed - #51 already carried the rename to `gitea/main`):** flip `GitWizard/GitWizard.csproj` back to `<PackageReference Include="MFTLib" Version="0.3.0" />` (the `BlockLocalMFTLibOnPublish` target enforces this on `dotnet publish`), drop `-NoBuild` from coverage runs (plain `dotnet` works again), re-verify, then push `main` - it fast-forwards `gitea/main` and CI runs green against the published 0.3.0.

## Pointers
- MFTLib clone with 0.3 on main: `C:\Users\mtsch\MFTLib`. (Earlier handoffs claimed git-wizard's CLAUDE.md held a stale `C:\Users\mtsch\source\repos\MFTLib` path - verified 2026-05-27, it does **not**; CLAUDE.md correctly uses `C:\Users\mtsch\MFTLib`.)
- Cross-repo memory: `~/.claude/notes/idioms_mftlib_elevation_testing.md`, `~/.claude/notes/project_mftlib_wizard_family.md`, `~/.claude/notes/feedback_publish_is_user_gated.md`.
