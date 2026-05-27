# Handoff — git-wizard consumes MFTLib 0.3 `IElevationProvider`

**Date:** 2026-05-26. **Branch:** `validate/mftlib-0.3-elevation` (local, unpushed WIP).
**Design spec:** `docs/superpowers/specs/2026-05-26-uac-free-tests-and-elevation-seam-design.md` (read it — this handoff only covers status + resume).

## Status: Part B done (surface validated), Parts C–E remain

### MFTLib side — DONE, merged, NOT published
- `IElevationProvider` + `ElevationUtilities.DefaultProvider` shipped on MFTLib **main** at commit `c25a97a` (PR #8, fast-forward merge; both gitea + github mains synced). 100% coverage, 0 exclusions.
- **The 0.3.0 NuGet is deliberately NOT published yet** (user-held decision). Until it is, git-wizard can only build against MFTLib via a **local ProjectReference** — it cannot use `PackageReference Version="0.3.0"`.

### git-wizard side — Part B done on this branch (validation only)
What's implemented and **proven green** (built via VS2026 MSBuild `Platform=x64`; 3/3 new decision tests pass with **zero UAC**):
- `GitWizard/GitWizard.csproj` — MFTLib `PackageReference 0.2.0` swapped to a **local ProjectReference** `..\..\MFTLib\MFTLib\MFTLib.csproj` with `SetPlatform="Platform=x64"`. **LOCAL DEV ONLY** — see "Before a real PR" below.
- `GitWizard/GitWizardApi.cs` — `TryFindAllRepositoriesUsingMft(..., IElevationProvider? elevation = null)`, `elevation ??= ElevationUtilities.DefaultProvider`, routes `IsElevated()` / `TryRunElevated(...)` through it.
- `GitWizard/WindowsDefenderException.cs` — `AddExclusions(IElevationProvider? elevation = null)`, same pattern over `IsElevated()` / `CanSelfElevate()` / `TryRunElevated(...)`.
- `GitWizardTests/ElevationDecisionTests.cs` — NUnit `FakeElevationProvider` spy + 3 tests: Defender not-elevated→TryRunElevated (cross-platform); MFT not-elevated→TryRunElevated (`[Platform("Win")]`); MFT **elevated + empty paths → TryRunElevated NOT called** (the already-elevated branch the internal MFTLib Func seams can't reach — this is the whole point of 0.3).

### ⚠️ Gotcha discovered (not in the spec) — applies to file-wizard too
Adding the optional `IElevationProvider?` parameter **breaks method-group-to-delegate conversions**. `GitWizardUI/Views/MainWindow.axaml.cs` had `Task.Run(WindowsDefenderException.AddExclusions)` (method group → `Func<bool>`); the optional param is not filled in method-group conversion, so it fails to compile (`CS1503`). **Fixed** by wrapping in a lambda: `Task.Run(() => WindowsDefenderException.AddExclusions())`. The two *direct* callers (`git-wizard/Program.cs`, `GitWizard/GitWizardReport.cs`) were genuinely unchanged. file-wizard should expect the same when it adopts the param.

## Remaining work (Parts C–E of the spec) — NOT started

- **Part C — UAC-free trigger tests:** `RefreshConcurrencyTest` (seed `repositoryPaths` directly from `TempRepoFixture`, drop the discovery call); `GetRepositoryPaths_With{EmptySearchPaths,NonExistentSearchPath}_DoesNotThrow` + `GetRepositoryPaths_EmptyConfiguration_FindsNothing` (pass `noMft: true`). Goal: `dotnet test` non-elevated pops **zero** UAC dialogs.
- **Part D — `GITWIZARD_HOME` redirect:** `GitWizardApi.GetLocalFilesPath()` honors env `GITWIZARD_HOME`; `TestUtilities.RedirectLocalFilesToTemp()`/`ClearLocalFilesRedirect()`; migrate `SettingsViewModelTests`, `AsyncFileIOTests`, cache tests in `GitWizardReportAdditionalTests` off the real `~/.GitWizard`.
- **Part E — coverage apparatus:** `[Category("RequiresAdmin")]` + `Assert.Inconclusive` guards on real-privilege code; translate MFTLib's `scripts/run-coverage.ps1` (self-elevating) to NUnit; root `TEST-REPORT.md`.

## Resume steps

1. **Do NOT run the full `dotnet test` yet** on Windows non-elevated — Part C is not done, so the 4 trigger tests will pop UAC. Run targeted: `--filter "FullyQualifiedName~ElevationDecisionTests"`.
2. Build: VS2026 MSBuild, `GitWizardTests/GitWizardTests.csproj -t:Build -p:Configuration=Debug` (the local ProjectReference's `SetPlatform=x64` builds MFTLib + native; `dotnet build` can't build the native vcxproj).
3. Implement Parts C–E.
4. **Before a real PR / publish:** MFTLib must publish 0.3.0 first; then flip `GitWizard.csproj` back to `<PackageReference Include="MFTLib" Version="0.3.0" />` (the `BlockLocalMFTLibOnPublish` target enforces this on `dotnet publish`). Re-verify, then open the git-wizard PR.

## Pointers
- MFTLib clone with 0.3 on main: `C:\Users\mtsch\MFTLib` (note: git-wizard CLAUDE.md's `C:\Users\mtsch\source\repos\MFTLib` path is **stale** — that dir doesn't exist).
- Cross-repo memory: `~/.claude/notes/idioms_mftlib_elevation_testing.md`, `~/.claude/notes/project_mftlib_wizard_family.md`, `~/.claude/notes/feedback_publish_is_user_gated.md`.
