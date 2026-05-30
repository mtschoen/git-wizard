# Test & Coverage Report — git-wizard

**Status:** PASS — 311 tests pass, **0 lint findings** (see Lint gate below).
**Mode:** lint = maintain (held the gate at 0 through the C# naming-convergence conformance pass); coverage = best-effort (behavior preserved, no tests removed).
**Branch:** `chore/linter-rollout` (linter rollout + naming-convergence pilot + vendored MFTLib CI unblock; off `main`)
**Last measured:** 2026-05-29, Windows, `Release` (MSBuild — required while MFTLib is a local `ProjectReference`), non-admin tier. Re-measured after the naming-convergence conformance pass (adopted the canonical fleet `.editorconfig`: charset utf-8-bom → **utf-8**, naming ruleset promoted from `suggestion` to `warning` → build error). Build stayed green with **0** git-wizard findings; 311/311 non-admin tests still pass. The elevated `RequiresAdmin` tier was **not** re-run — its privileged MFT/elevation code is unchanged, so its contribution is carried forward.
**Command:** `dotnet test GitWizardTests --no-build -c Release --collect:"XPlat Code Coverage"` then `ci/post-coverage-status.py` (non-admin tier, no UAC).
**Git:** `4e1ef55`

## Results

| Metric | Value |
| --- | --- |
| Tests passed (non-admin tier) | 311 |
| Tests passed (`RequiresAdmin` tier, elevated) | 2 (carried forward — not re-run this session) |
| Failed | 0 |
| Skipped (RequiresAdmin / Unix-only on Windows) | 1 |
| **Line coverage (non-admin tier, this session)** | **41.95%** (`Release`; prior `Debug` non-admin was 42.35% — flat, no tests removed; the delta is Debug-vs-Release measurement + removal of dead always-false branches) |
| Line coverage (merged: non-admin + elevated, prior full run) | 44.24% (carried forward) |
| Branch coverage (non-admin report) | 37.43% (carried forward) |
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

## Lint gate (PASS — 0 findings)

The linter rollout drove every configured analyzer/inspection to **zero** and baked the gate into the build + CI.

| Tool | Findings | How it runs |
| --- | --- | --- |
| Roslyn analyzers (CA*/IDE*) + Roslynator 4.15.0 | **0** | `Directory.Build.props`: `EnableNETAnalyzers`, `AnalysisLevel=latest-Recommended`, `EnforceCodeStyleInBuild`, `TreatWarningsAsErrors` → the Release build **is** the gate. |
| `dotnet format` (`.editorconfig`) | **0** | `dotnet format git-wizard.slnx --verify-no-changes` (CI Linux job) + an on-save PostToolUse hook (local, `.claude/`). |
| jb inspectcode (deep ReSharper) | **0** | `jb inspectcode … --severity=WARNING` gated by `ci/parse-jb-report.py` (CI Windows job). |
| aislop (AI-slop) | thin live gate (PR #54) | `.gitea/workflows/aislop.yml` runs `aislop ci` (failBelow 80); scores ~91 on the Python `ci/` files — aislop does not analyze C# yet, so it contributes nothing until aislop's C# engine ships. |

- Went **364 Roslyn warnings → 0** and **20 jb findings → 0**. Naming modernized to idiomatic .NET (dropped Unity-style `k_`/`s_` prefixes → PascalCase; `_camelCase` instance fields); the local + solution ReSharper naming config was aligned so `InconsistentNaming` stays active.
- **Naming-convergence conformance (2026-05-29):** adopted the canonical fleet `.editorconfig` (source of truth `~/.claude/notes/idioms_csharp_naming.md`) as the **authoritative naming gate**: charset utf-8-bom → **utf-8** (BOM stripped from all 51 `.cs` via `dotnet format`), and the naming ruleset promoted from `suggestion` to `warning` so `EnforceCodeStyleInBuild` + `TreatWarningsAsErrors` makes IDE1006 a **build error** (covers const/static-readonly→PascalCase and all private/internal fields→`_camelCase`). The MSBuild `-warnaserror` build stayed at **0** git-wizard findings — the code already conformed, so no hand-renames were needed; this validated the canonical editorconfig's naming-rule *order* (const/static-readonly before the general private-field rule) against a real build. jb's prior 0 carries forward (it binds to the global ReSharper config, not `.editorconfig`, and the only code change was BOM stripping).
- **Per-case code suppressions: 0.** Test-project scope-offs in `.editorconfig`: `CA1707` (NUnit `Method_Scenario_Expected` underscores) and `CA1861` (constant-array-arg perf) — both irrelevant to test code; all other rules still apply to tests.
- `WindowsDefenderException` → `WindowsDefender` (CA1711: it manages AV exclusions, not exceptions).
- MFTLib (local `ProjectReference`, outside this repo's tree) keeps its own ~4 warnings — it does not inherit `TreatWarningsAsErrors` and is out of scope.
- **CI is now GREEN on the runners** (2026-05-29) via a TEMPORARY vendored MFTLib `0.3.0` bridge (`lib/MFTLib/` + repo-root `Directory.Build.targets`) — the runners build with plain `dotnet build`, no MFTLib source/native toolchain. Both jobs pass: Build+Test (Linux) and Build+Test (Windows), plus aislop + coverage (49.08% ≥ 45%). Retire the bridge when MFTLib `0.3.0` ships to NuGet (see `lib/MFTLib/README.md`). jb inspectcode confirmed **0 findings** on the runner (its naming binds to the runner's global ReSharper config; a fresh runner's idiomatic-.NET defaults match the canonical convention).
- **CI-unblock follow-ups (2026-05-29):** running CI for the first time surfaced dormant pre-existing issues, all fixed — `.gitattributes` force-CRLF for C# sources (Linux `dotnet format` ENDOFLINE); jb `--settings=` flag + an inline-PowerShell jb gate (no `python` on the SYSTEM-account Windows runner); a `SettingsViewModel` fire-and-forget-save vs test-teardown race (teardown now waits out in-flight writes; ctor no longer saves on load); two `IgnoredPaths[0]` tests made cross-platform (default ignored paths are Windows-only); and a `continue-on-error` on the cert-blocked supplementary coverage-artifact upload.

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
