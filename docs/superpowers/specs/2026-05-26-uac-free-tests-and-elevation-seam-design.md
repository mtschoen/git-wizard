# Design: UAC-free `dotnet test` + dedicated self-elevation coverage

**Date:** 2026-05-26
**Branch:** `retire-maui-rename-ui` (PR #51) — all work lands here per the handoff.
**Source:** `docs/inspection-cleanup-handoff.md` → "TODO — UAC prompts during `dotnet test`".

## Problem

Running the full `dotnet test` suite non-elevated on Windows pops ~4 UAC dialogs and can
hang an unattended run. CI is unaffected: the Windows runner is elevated, so
`ElevationUtilities.IsElevated()` is true and no child is spawned.

Root cause: `GitWizardReport.GetRepositoryPaths(paths, updateHandler, noMft:false)` →
`GitWizardApi.TryFindAllRepositoriesUsingMft(...)`. On non-elevated Windows that method
calls `ElevationUtilities.TryRunElevated("--elevated-mft …")` **unconditionally** — the
elevated-child spawn does not depend on the search paths (the child does the scan, the
parent only launches it). So pointing tests at a temp search dir alone does **not** prevent
the prompt; `noMft:true` is also required.

Exactly four tests reach that spawn (each calls the MFT-capable instance overload
`GitWizardReport.GetRepositoryPaths(ICollection<string>, …)` with `noMft:false`):

- `GitWizardReportTests.RefreshConcurrencyTest` — uses `CreateDefaultConfiguration()` (real `%USERPROFILE%`).
- `GitWizardReportTests.GetRepositoryPaths_WithEmptySearchPaths_DoesNotThrow` — empty config, still spawns.
- `GitWizardReportAdditionalTests.GetRepositoryPaths_EmptyConfiguration_FindsNothing` — empty config, still spawns.
- `GitWizardReportAdditionalTests.GetRepositoryPaths_WithNonExistentSearchPath_DoesNotThrow` — nonexistent path, still spawns.

`GeneratedReport_DefaultsBranchScopeToActionable` is **not** a trigger: it passes a non-null
empty `repositoryPaths` list to `GenerateReport`, so `GetRepositoryPaths` is never called.
(The handoff's "re-verify" flag was correct to doubt it.)

A related, pre-existing isolation bug rides along: `SettingsViewModelTests` persist
`ForkPath`/search paths to the **real** `~/.GitWizard/config.json`. Run as an isolated
subset, two tests (`Construction_LoadsConfiguration`, `AddSearchPathAsync_AddsPathFromPicker`)
fail from cross-test pollution; full-suite ordering masks it. Same family as the UAC issue
(tests touching real user state). Several other classes also write to the real
`~/.GitWizard` (`AsyncFileIOTests`, the cache tests in `GitWizardReportAdditionalTests`).

## Goals

1. `dotnet test` non-elevated produces **zero** UAC prompts.
2. Self-elevation is still covered — by a **dedicated** test of the elevation *decision*, not
   as a side-effect of unrelated tests, and without spawning a real UAC prompt.
3. Tests stop reading/writing the real `~/.GitWizard`; they use a redirectable data dir.

## Non-goals (carry-forwards — validation, not code; left flagged in the handoff)

- GUI smoke-test of the committed context-menu `DataContext` fix (needs a headed window).
- Rider confirmation that the 10 naming warnings clear (needs Rider, not the CLI).

## Constraints discovered

- `MFTLib.ElevationUtilities` is a **static class** (`abstract sealed`) with static methods
  `IsElevated()`, `GetProcessPath()`, `CanSelfElevate()`, `TryRunElevated(string, int)` —
  confirmed by reflection. Not mockable directly; a seam is required.
- NUnit runs **sequentially** here (no `[Parallelizable]`, no `.runsettings`), so a per-test
  process-global `GITWIZARD_HOME` env var is safe.
- No `[InternalsVisibleTo]` exists; new seam types that tests touch are **public**.
- The elevated child is passed `--config-path` and `--output` **explicitly**
  (`GitWizardApi.cs` ~L162), computed from the parent's `GetLocalFilesPath()`. A
  `GITWIZARD_HOME` redirect in the parent is therefore self-consistent and does not affect
  the elevation handshake; the child does not need the env var.
- On Linux, `TryFindAllRepositoriesUsingMft` returns at the `!IsOSPlatform(Windows)` guard
  before consulting elevation, so its decision logic is inherently Windows-only.

## Design

### Sub-task 1 — Stop the UAC prompts (test-only, no production change)

- `RefreshConcurrencyTest`: build a `GitWizardConfiguration` whose single search path is a
  **temp directory containing 1–2 `TempRepoFixture` repos**, then call
  `report.GetRepositoryPaths(paths, noMft: true)`. This keeps the concurrency test
  meaningful (real repos to refresh in parallel) and fast, with no elevation.
- The other three tests: pass `noMft: true`. Their search paths are already
  empty/nonexistent, so the recursive fallback is a no-op and the assertions
  (`paths` empty / does-not-throw) still hold — now matching the test names exactly.

### Sub-task 2 — Injectable elevation seam + dedicated decision tests

New `GitWizard/IElevationProvider.cs`:

```csharp
public interface IElevationProvider
{
    bool IsElevated();
    bool CanSelfElevate();
    bool TryRunElevated(string arguments, int timeoutMs);
}

internal sealed class DefaultElevationProvider : IElevationProvider
{
    public static readonly DefaultElevationProvider Instance = new();
    public bool IsElevated() => ElevationUtilities.IsElevated();
    public bool CanSelfElevate() => ElevationUtilities.CanSelfElevate();
    public bool TryRunElevated(string arguments, int timeoutMs)
        => ElevationUtilities.TryRunElevated(arguments, timeoutMs);
}
```

(`GetProcessPath()` is omitted from the interface — not used by either consumer; YAGNI.)

Thread an optional provider through the two consumers, defaulting to the real one:

- `GitWizardApi.TryFindAllRepositoriesUsingMft(GitWizardConfiguration, ICollection<string>,
  IUpdateHandler? = null, bool noMft = false, IElevationProvider? elevation = null)` — does
  `elevation ??= DefaultElevationProvider.Instance` and routes its `IsElevated()` /
  `TryRunElevated(...)` calls through it. Production callers pass nothing → unchanged.
- `WindowsDefenderException.AddExclusions(IElevationProvider? elevation = null)` — same
  pattern over `IsElevated()` / `CanSelfElevate()` / `TryRunElevated(...)`.

New `GitWizardTests/ElevationDecisionTests.cs` with a spy fake:

```csharp
sealed class FakeElevationProvider : IElevationProvider
{
    public bool Elevated;
    public bool SelfElevatable;
    public bool RunElevatedResult;
    public readonly List<string> RunElevatedCalls = new();
    public bool IsElevated() => Elevated;
    public bool CanSelfElevate() => SelfElevatable;
    public bool TryRunElevated(string arguments, int timeoutMs)
    {
        RunElevatedCalls.Add(arguments);
        return RunElevatedResult;
    }
}
```

Tests:

- **Defender — cross-platform (runs on Linux CI, adds gate coverage; never spawns):**
  not-elevated + can-self-elevate → assert `TryRunElevated` was invoked once with
  `"--elevated-defender"` and `AddExclusions` returns the fake's `RunElevatedResult`.
  (`AddExclusions` has no OS short-circuit, so this is fully portable.)
- **MFT — `[Platform("Win")]`-gated (works on the elevated Windows runner because the fake
  controls elevation):**
  - not-elevated → assert `TryRunElevated` invoked once with an argument string starting
    `"--elevated-mft --config-path "`.
  - elevated + **empty** search paths → assert `TryRunElevated` was **not** called (proves the
    direct-scan branch was taken; empty paths means no real `MftVolume.Open`).

  Windows-gated because the method short-circuits before the provider on non-Windows.

### Sub-task 3 — `GITWIZARD_HOME` config-home redirect

- `GitWizardApi.GetLocalFilesPath()`: if env var `GITWIZARD_HOME` is set and non-empty, return
  it directly as the data directory; otherwise the current
  `Path.Combine(UserProfile, ".GitWizard")`. Gives users a relocatable config dir and gives
  tests an isolation seam.
- `TestUtilities` gains `RedirectLocalFilesToTemp()` (create a unique temp dir, set
  `GITWIZARD_HOME` to it, return the path) and `ClearLocalFilesRedirect(string? temp)` (clear
  the env var, delete the temp dir).
- Migrate the test classes that write to the **real** `~/.GitWizard` onto the redirect in
  SetUp/TearDown:
  - `SettingsViewModelTests` (fixes the known isolation bug).
  - `AsyncFileIOTests` (retire its delete-the-real-file `ResetStaticCaches` hack — the redirect
    makes the real files untouched).
  - The cache-writing tests in `GitWizardReportAdditionalTests`
    (`GetCachedReport*`, `…_InvalidJson`) and any other test there that writes to
    `GetCachedReportPath()` / `GetGlobalConfigurationPath()`.
  - Tests that assert **default** behavior (e.g. `GitWizardApiAdditionalTests.GetLocalFilesPath_ContainsUserProfile`)
    do **not** set the env var, so they keep validating the unredirected path.

## Verification

- Full `dotnet test` non-elevated on Windows → **zero** UAC prompts (manual, since CI is elevated).
- `SettingsViewModelTests` run as an isolated subset → `Construction_LoadsConfiguration` and
  `AddSearchPathAsync_AddsPathFromPicker` both pass.
- New elevation tests green on both runners (Defender everywhere; MFT on Windows).
- Coverage gate (Linux, line ≥ 45%) holds or improves: the Defender decision is newly covered
  on Linux; sub-task 1's `noMft:true` change is behaviorally identical on Linux (the MFT path
  already returned false there), so it removes no Linux coverage.

## Files

- **New:** `GitWizard/IElevationProvider.cs`, `GitWizardTests/ElevationDecisionTests.cs`.
- **Edit:** `GitWizard/GitWizardApi.cs`, `GitWizard/WindowsDefenderException.cs`,
  `GitWizardTests/TestUtilities.cs`, `GitWizardTests/GitWizardReportTests.cs`,
  `GitWizardTests/GitWizardReportAdditionalTests.cs`, `GitWizardTests/SettingsViewModelTests.cs`,
  `GitWizardTests/AsyncFileIOTests.cs`.
