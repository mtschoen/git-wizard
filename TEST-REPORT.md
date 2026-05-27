# Test & Coverage Report — git-wizard

**Branch:** `main` (held MFTLib 0.3 elevation work + the context-menu / clipboard / copy-icon GUI fixes)
**Last measured:** 2026-05-27, Windows, `Debug`, MFTLib via local `ProjectReference`. **Non-admin tier re-run this session** (after the copy-icon UX + `AvaloniaClipboardService` coverage work). The elevated `RequiresAdmin` tier was **not** re-run — its privileged MFT/elevation code is unchanged, so its contribution is carried forward from the prior full run.
**Command:** `scripts/run-coverage.ps1 -Configuration Debug -NoBuild -NonInteractive` (non-admin tier, no UAC; for the full merged figure drop `-NonInteractive` to self-elevate once)
**Git:** `0f6f1fe` + uncommitted copy-icon/clipboard changes

## Results

| Metric | Value |
| --- | --- |
| Tests passed (non-admin tier) | 303 (+7: copy-icon VM tests, a clipboard-failure test, a real-clipboard headless test, and a test-isolation regression guard) |
| Tests passed (`RequiresAdmin` tier, elevated) | 2 (carried forward — not re-run this session) |
| Failed | 0 |
| Unix-only (skipped on Windows) | 1 |
| **Line coverage (non-admin tier, this session)** | **42.35%** (was 41.45% before this work) |
| Line coverage (merged: non-admin + elevated, prior full run) | 44.24% |
| Branch coverage (non-admin report) | 37.43% (was 36.64%) |
| `[ExcludeFromCodeCoverage]` annotations | 0 |

> **Line coverage is correctly merged** across the two runs (`ci/post-coverage-status.py` ORs line hits
> across every `coverage.cobertura.xml`): in the prior full run the elevated tier lifted the non-admin
> baseline 41.45% → **44.24%** by covering the genuinely-privileged code (`MftVolume.Open` raw scan in
> `TryFindGitRepositoriesUsingMft`, `RunElevatedMftScan`). This session raised the **non-admin baseline to
> 42.35%** (copy-icon VM paths + a real-`IClipboard` headless test for `AvaloniaClipboardService`); a fresh
> self-elevating run would merge to ~45%, but the elevated tier wasn't re-run (unchanged privileged code).
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
