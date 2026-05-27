# Test & Coverage Report — git-wizard

**Branch:** `validate/mftlib-0.3-elevation` (Parts C–E of the MFTLib 0.3 elevation integration)
**Last measured:** 2026-05-27, Windows, `Debug`, MFTLib via local `ProjectReference`. Full **self-elevating** run (non-admin tier + elevated `RequiresAdmin` tier).
**Command:** `scripts/run-coverage.ps1 -NoBuild` (self-elevates once for the `RequiresAdmin` tier)

## Results

| Metric | Value |
| --- | --- |
| Tests passed (non-admin tier) | 296 |
| Tests passed (`RequiresAdmin` tier, elevated) | 2 |
| Failed | 0 |
| Unix-only (skipped on Windows) | 1 |
| **Line coverage (merged: non-admin + elevated)** | **44.24%** |
| Branch coverage (non-admin report) | 36.64% |
| `[ExcludeFromCodeCoverage]` annotations | 0 |

> **Line coverage is correctly merged** across the two runs (`ci/post-coverage-status.py` ORs line hits
> across every `coverage.cobertura.xml`): the elevated tier lifts it 41.45% → **44.24%** by covering the
> genuinely-privileged code (`MftVolume.Open` raw scan in `TryFindGitRepositoriesUsingMft`, `RunElevatedMftScan`).
>
> **Branch coverage is NOT reliably merged by that script** — it averages each report's root `branch-rate`
> (the script's own docstring flags this as an approximation for the multi-report case). With the full run
> producing two reports (a whole-suite report + a 2-test elevated report), that average reads ~20%, which is
> a tooling artifact, not a real drop. The meaningful single-report branch figure is **36.64%** (non-admin).
> Accurate branch merging across the split runs would need `reportgenerator` — a follow-up, not wired up here.
> `WindowsDefenderException.RunDefenderCommands*` remains uncovered by design (real Defender mutation; not auto-tested).

## How coverage is structured (two tiers — mirrors MFTLib)

Self-elevation is split into two test tiers because only one of them can be faked:

1. **Elevation *decision* logic** — the `IsElevated()` / `CanSelfElevate()` / `TryRunElevated()` branching in
   `GitWizardApi.TryFindAllRepositoriesUsingMft` and `WindowsDefenderException.AddExclusions`. Covered with
   **zero UAC** by injecting a fake `MFTLib.IElevationProvider` (`GitWizardTests/ElevationDecisionTests.cs`),
   including the already-elevated branch the internal Func seams can't reach. This is the payoff of the
   MFTLib 0.3 `IElevationProvider` refactor.

2. **Genuinely-privileged operations** — `MftVolume.Open` reads a raw NTFS volume handle; nothing can fake
   that, it needs real admin. Covered by `[Category("RequiresAdmin")]` tests
   (`GitWizardTests/ElevationCoverageTests.cs`) guarded on the **first line of the test body** with
   `if (!ElevationUtilities.IsElevated()) { Assert.Inconclusive("Requires admin"); return; }`. A normal
   `dotnet test` skips them (Inconclusive, **zero UAC**); the self-elevating coverage run includes them.

   > NUnit does **not** reliably skip a test body when the guard is in `[SetUp]` — it must be in the body
   > (matches MFTLib's `UsnJournalLiveTests`). A SetUp-only guard lets the body run the real provider and
   > fire UAC.

## Running the full (elevated) coverage

```powershell
.\scripts\run-coverage.ps1                  # self-elevates once (UAC) to include RequiresAdmin tests
.\scripts\run-coverage.ps1 -NonInteractive  # skip RequiresAdmin (CI / headless) — the figures above
.\scripts\run-coverage.ps1 -NoBuild         # tests already built (e.g. via VS MSBuild while MFTLib is a local ProjectReference)
```

Coverage is merged across the non-admin and elevated runs by `ci/post-coverage-status.py` (the same parser
the CI gate uses — it ORs line hits across every `coverage.cobertura.xml` under `TestResults`).

## Relationship to the CI gate

The CI **Linux** job gates on **45% line** coverage of the cross-platform code (`ci/post-coverage-status.py
--gate-line 45`). That is a separate measurement from the Windows figures above: the Windows-only MFT/elevation
code is out of scope on Linux. git-wizard's coverage truth is the **union** of the Linux-measured cross-platform
code and the elevated-Windows-measured privileged code.
