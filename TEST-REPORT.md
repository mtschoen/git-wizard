# Test & Coverage Report - git-wizard

## Packed-index port: Task 11 Linux verification

Measured 2026-09-13 on Linux, Release, in the Task 11 worktree. Base `6dbcbde` is controller-supplied; no git commands were run. Mode: dispatch-defined 55% line floor plus no regression against Phase 1.

| Evidence tier | Result |
| --- | --- |
| Linux, measured by this lane | **PASS**: 1164 printed definitions, 1154 passed, 0 failed, 10 named skips. Initial, post-format and final coverage runs reconcile with the dispatch baseline. |
| Windows non-admin | **PENDING controller** at the exact final published reference; no result supplied. |
| Elevated / RequiresAdmin | **NOT RUN, owner attended**: the four strengthened Task 9 cases listed below. |
| Linux privileged fanotify | **NOT RUN, owner attended**: two named cases skipped. |

The real VSTest summary is `Failed: 0, Passed: 1154, Skipped: 3, Total: 1157`, while its output names ten skips. The printed definition count is 1164 without de-duplication, and 1154 + 10 = 1164. Baseline and final definition lists match exactly. The plan's older 1163/1153 ledger is superseded by the dispatch's measured 1164/1154 base. This lane adds no definitions.

## Linux coverage comparison

| Metric | Phase 1 (`9eacde5`, controller supplied) | Final, measured here | Difference |
| --- | ---: | ---: | ---: |
| Lines covered / valid | 4002 / 4836 | 4264 / 5005 | +262 covered / +169 valid |
| Line coverage | 82.75% | **85.19%** | +2.44 percentage points |
| Branches covered / valid | 1831 / 2292 | 2000 / 2431 | +169 covered / +139 valid |
| Branch coverage | 79.88% | **82.27%** | +2.39 percentage points |

The 55% gate and aggregate Phase 1 ratchet pass. Covered counts and denominators both grow; this is not an improvement caused by denominator reduction. Removing the obsolete exclusion cannot change effective scope because its source file was already deleted: both this lane's initial full-run report and final report have 5005 valid lines / 2431 valid branches. The initial run covered 4266 lines; the final run covers 4264. The two hit differences are MainViewModel.Live.cs:240-241 in the unchanged onEvent callback. Branch totals are unchanged. This within-lane variation is recorded, not hidden by repeating the run.

Final Cobertura: `TestResults/final/6be87ff8-302f-4c59-98d3-591358a104fc/coverage.cobertura.xml`. Exactly one report matches the final gate glob; no merged stale/focused report contributes. SHA-256: `38d3d124471b35a1cd21c2550c4a038ceb2fbc5479bcec1eda720bd6226119f3`. Detailed production-path counts are in LANE-REPORT.md. Phase 1 per-path XML was not supplied, so a path-by-path historical comparison remains pending controller evidence; aggregate totals alone do not prove all replacement behavior.

The surviving ExcludeByFile entries remain exactly `**/Program.Watch.cs,**/WindowsDefender.cs,**/MainWindow.axaml.cs,**/SettingsWindow.axaml.cs`. No replacement exclusion or other exclusion change was added.

## Quality and external gates

| Gate | Result / owner |
| --- | --- |
| MFTLib, MFTLibTestExtensions, whole solution Release builds | PASS, exit 0. Existing pinned-submodule NU1902 / empty SourceLink warnings and optional missing native-library warnings remain; consumer analyzer errors: 0. |
| dotnet format | **PASS, lane-owned under P19**. Initial verify: 10 findings, exit 2, in the four dispatch-named test files. Apply and final verify: exit 0; final verify emitted no findings. |
| Pinned C#-capable aislop | PENDING controller; require 100/100, >=10 files, zero findings. |
| jb inspectcode and parser | PENDING controller on Windows; require zero actionable findings. |
| Obsolete-symbol/source/exclusion sweeps | PASS: no requested matches after the edit. Named P7 successors are present. |
| Submodule pin, diff scope, whitespace, fanotify and recursive runtime-body audit | PENDING controller git checks. No production source changed during this lane, verified against starting file hashes; no historical diff or pin conclusion is claimed. |
| Attended memory/performance | PENDING controller/owner: same 21.3M-file machine, before/after, DirectoryIndex fixture/profile, cold settled client <1 GB, scan-relative cold duration, warm name query <200 ms. Campaign tool/path is controller-provided. |
| Warm persistent opening / <300 MB persistent warm target | NOT APPLICABLE to plan 4 under production NoCache; separate persistent-cache campaign only. |

Required external gates must complete before the controller marks the whole port verified or merges.

## Named Linux skips

- `TryFindAllRepositoriesUsingMftAsync_EmptySearchPaths_ReturnsFalse`
- `TryFindAllRepositoriesUsingMftAsync_Elevated_FindsPreExistingRepository`
- `TryFindAllRepositoriesUsingMftAsync_EmptySearchPaths_DoesNotInvokeConnectionFactory`
- `GetRepositoryPaths_SkipsHiddenAttributeDirectory`
- `CreateDefaultConfiguration_SearchPath_ContainsUserProfile_OnWindows`
- `ArmAndCatchUpAsync_RealBroker_ReturnsUsableDriveStatus`
- `WatchAsync_RealBroker_DetectsCreatedFile`
- `WatchAsync_RealBroker_ReplaysFileCreatedBetweenScanAndWatch`
- `Probe_RealFanotify_MarksAndUnmarksTemporaryFilesystem`
- `RealFanotify_DetectsFileCreationAndDeletion`

## Four strengthened Task 9 attended cases

- `ElevationCoverageTests.TryFindAllRepositoriesUsingMftAsync_Elevated_FindsPreExistingRepository`
- `IndexVolumeChangeSourceWindowsTests.ArmAndCatchUpAsync_RealBroker_ReturnsUsableDriveStatus`
- `IndexVolumeChangeSourceWindowsTests.WatchAsync_RealBroker_DetectsCreatedFile`
- `IndexVolumeChangeSourceWindowsTests.WatchAsync_RealBroker_ReplaysFileCreatedBetweenScanAndWatch`

These are NOT RUN here. Owner evidence must cover actual MFT discovery paths, usable real-drive status, delivered file creation, scan-to-watch replay, UAC, and broker/native deployment.

## Commands measured by this lane

```bash
dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
dotnet build external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj -c Release -p:Platform=x64
dotnet build GitWizardTests/GitWizardTests.csproj -c Release
dotnet build git-wizard.slnx -c Release
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build --list-tests
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build --filter FullyQualifiedName~MftIndexSessionTests.CancellationClosesClient
dotnet format git-wizard.slnx --verify-no-changes
dotnet format git-wizard.slnx
dotnet format git-wizard.slnx --verify-no-changes
dotnet build git-wizard.slnx -c Release
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build --list-tests
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --collect:"XPlat Code Coverage" --results-directory TestResults/final
python3 ci/post-coverage-status.py --cobertura "TestResults/final/**/coverage.cobertura.xml" --gate-line 55 --summary --skip-post
```

The cancellation matcher red/green probe, exact command output, count reconciliation, scope audit, and limitations are preserved in LANE-REPORT.md. No git, Windows verification, aislop, jb or inspectcode command was run by this lane. No HOME or GITWIZARD_HOME override was used.

## Historical report: 2026-08-21 Windows, different branch

Everything below this heading is retained historical evidence for `feat/watch-in-ui-revive`,
including its earlier lint rollout and coverage instructions. Its runtime names, test counts,
coverage percentages, and gate results describe that branch at that time, not the packed-index
port. In particular, its retired watch/discovery types do not describe the current consumer.
Current architecture and build instructions are in [AGENTS.md](AGENTS.md).

**Status:** PASS - 1000 non-admin tests pass, **0 build/analyzer findings** (analyzer gate + `dotnet format` + jb inspectcode clean). aislop not re-scored locally (the C# engine lives in the pinned fork the `aislop.yml` workflow builds); the gate runs in CI.
**Mode:** coverage gate = pass (**85.67%** line ≥ 55%). The watch pipeline is covered end to end with fakes: the `IVolumeChangeSource` seam, `RepositoryWatchService` mapping / debounce / rename surfacing, `RepositoryChangeFilter.Classify` on both Windows and POSIX path shapes, the CLI `--watch` loop, `LiveWatchController`, and the `MainViewModel` Live wiring. The real-broker `UsnVolumeChangeSource` tests and the real-kernel `FanotifyInterop` test are `RequiresAdmin`/`RequiresRoot` and self-skip when unprivileged.
**Change:** Revived the shelved watch-in-UI work on top of current main. Ported `UsnVolumeChangeSource` from the removed raw `JournalBrokerClient` API to `JournalBrokerScanSession`, wired the UI Live-mode toggle through `LiveWatchController`, added the Linux `FanotifyInterop` capability probe, and replaced the Classify path normalization with syntax-preserving `WatchPath` comparisons.
**Branch:** `feat/watch-in-ui-revive` (consolidates `feat/watch-in-ui`, `openai/watch-ui-fanotify-interop`, and `salvage/watch-ui-linux-watch-tests`).
**Last measured:** 2026-08-21, Windows, `Release`, non-admin tier.
**Command:** `MSBuild.exe external/MFTLib/MFTLibNative/MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64` · `dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64` · `dotnet build external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj -c Release -p:Platform=x64` · `dotnet build git-wizard.slnx -c Release --no-restore` · `dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build --collect:"XPlat Code Coverage"` · `python ci/post-coverage-status.py --cobertura "GitWizardTests/TestResults/**/coverage.cobertura.xml" --gate-line 55 --summary --skip-post` · `dotnet format git-wizard.slnx --verify-no-changes` · `jb inspectcode git-wizard.slnx --settings=git-wizard.slnx.DotSettings --severity=WARNING --no-updates` gated by `python ci/parse-jb-report.py`.
**Git:** `feat/watch-in-ui-revive` at the WatchPath cherry-pick, plus this report edit

## Results

| Metric | Value |
| --- | --- |
| Tests passed (non-admin tier) | 1000 |
| Tests passed (`RequiresAdmin` tier, elevated) | Not run - 3 real-broker tests and 1 real-kernel fanotify test self-skip unprivileged |
| Failed | 0 |
| Skipped on Windows (Linux-only/non-Windows guards) | 2 |
| **Line coverage (non-admin, `Release`)** | **85.67%** (CI gate: 55%) |
| **Branch coverage (non-admin, `Release`)** | **83.06%** |
| `[ExcludeFromCodeCoverage]` annotations | 0 (Exclusions configured cleanly via `coverlet.runsettings` for Views, UI Services wrappers, and WindowsDefender) |

> These are Windows figures from one fresh Cobertura report. The elevated tier was not run:
> the real-broker `UsnVolumeChangeSource` tests need an actual UAC elevation, and the
> real-kernel `FanotifyInterop` test needs a Linux root session. Both self-skip with
> `Assert.Inconclusive` rather than failing.

## Lint gate (PASS - 0 findings)

The linter rollout drove every configured analyzer/inspection to **zero** and baked the gate into the build + CI.

| Tool | Findings | How it runs |
| --- | --- | --- |
| Roslyn analyzers (CA*/IDE*) + Roslynator 4.15.0 | **0** | `Directory.Build.props`: `EnableNETAnalyzers`, `AnalysisLevel=latest-Recommended`, `EnforceCodeStyleInBuild`, `TreatWarningsAsErrors` → the Release build **is** the gate. |
| `dotnet format` (`.editorconfig`) | **0** | `dotnet format git-wizard.slnx --verify-no-changes` (CI Linux job) + an on-save PostToolUse hook (local, `.claude/`). |
| jb inspectcode (deep ReSharper) | **0** | `jb inspectcode … --severity=WARNING` gated by `ci/parse-jb-report.py` (CI Windows job). |
| aislop (AI-slop, C# engine) | **100** (0 findings) | `.gitea/workflows/aislop.yml` clones + builds the C#-capable fork `gitea.fleet.sticktoitive.net/schoen/aislop` at a pinned commit (the public npm `aislop` is Python-only; `github.com/mtschoen/aislop` is a lagging mirror) and runs it against `.aislop/config.yml` (csharp.yml shape, `maxFileLoc: 400`, `failBelow: 100`); the job also installs `roslynator.dotnet.cli` and fails on a <10-file scan. This branch took the C# score 13 → 45 → **100**: all error-level, null-forgiving, todo-stub, narrative-comment, and file/function-too-large findings cleared. `failBelow` raised to 100 to match the repo's 0-findings bar. |

- **This PR's added tests (2026-05-31) pass all three gates with 0 new findings** - the build's analyzer/naming gate caught a real CA1725 (handler override param names had to match `IUpdateHandler`'s `gitWizardRepository`) and an RCS1139 (a `<param>` doc comment needed a `<summary>`); both fixed, then `dotnet format --verify-no-changes` and `jb inspectcode` (0 findings) re-verified clean.
- Went **364 Roslyn warnings → 0** and **20 jb findings → 0**. Naming modernized to idiomatic .NET (dropped Unity-style `k_`/`s_` prefixes → PascalCase; `_camelCase` instance fields); the local + solution ReSharper naming config was aligned so `InconsistentNaming` stays active.
- **Naming-convergence conformance (2026-05-29):** adopted the canonical fleet `.editorconfig` (source of truth `~/.claude/notes/idioms_csharp_naming.md`) as the **authoritative naming gate**: charset utf-8-bom → **utf-8** (BOM stripped from all 51 `.cs` via `dotnet format`), and the naming ruleset promoted from `suggestion` to `warning` so `EnforceCodeStyleInBuild` + `TreatWarningsAsErrors` makes IDE1006 a **build error** (covers const/static-readonly→PascalCase and all private/internal fields→`_camelCase`). The MSBuild `-warnaserror` build stayed at **0** git-wizard findings - the code already conformed, so no hand-renames were needed; this validated the canonical editorconfig's naming-rule *order* (const/static-readonly before the general private-field rule) against a real build. jb's prior 0 carries forward (it binds to the global ReSharper config, not `.editorconfig`, and the only code change was BOM stripping).
- **Per-case code suppressions: 0.** Test-project scope-offs in `.editorconfig`: `CA1707` (NUnit `Method_Scenario_Expected` underscores) and `CA1861` (constant-array-arg perf) - both irrelevant to test code; all other rules still apply to tests.
- `WindowsDefenderException` → `WindowsDefender` (CA1711: it manages AV exclusions, not exceptions).
- MFTLib (local `ProjectReference`, outside this repo's tree) keeps its own ~4 warnings - it does not inherit `TreatWarningsAsErrors` and is out of scope.
- **CI is now GREEN on the runners** (2026-05-29) via a TEMPORARY prebuilt-DLL MFTLib `0.3.0` bridge (`lib/MFTLib/` + repo-root `Directory.Build.targets`, since superseded by the `external/MFTLib` source submodule - see `AGENTS.md` → Build) - the runners build with plain `dotnet build`, no MFTLib source/native toolchain. Both jobs pass: Build+Test (Linux) and Build+Test (Windows), plus aislop + coverage (49.08% ≥ 45%). jb inspectcode confirmed **0 findings** on the runner (its naming binds to the runner's global ReSharper config; a fresh runner's idiomatic-.NET defaults match the canonical convention).
- **CI-unblock follow-ups (2026-05-29):** running CI for the first time surfaced dormant pre-existing issues, all fixed - `.gitattributes` force-CRLF for C# sources (Linux `dotnet format` ENDOFLINE); jb `--settings=` flag + an inline-PowerShell jb gate (no `python` on the SYSTEM-account Windows runner); a `SettingsViewModel` fire-and-forget-save vs test-teardown race (teardown now waits out in-flight writes; ctor no longer saves on load); two `IgnoredPaths[0]` tests made cross-platform (default ignored paths are Windows-only); and a `continue-on-error` on the cert-blocked supplementary coverage-artifact upload.

## How coverage is structured (two tiers - mirrors MFTLib)

Self-elevation is split into two test tiers because only one of them can be faked:

1. **Elevation *decision* logic** - the `IsElevated()` / `CanSelfElevate()` / `TryRunElevated()` branching in
   `GitWizardApi.TryFindAllRepositoriesUsingMft` and `WindowsDefenderException.AddExclusions`. Covered with
   **zero UAC** by injecting a fake `MFTLib.IElevationProvider` (`GitWizardTests/ElevationDecisionTests.cs`),
   including the already-elevated branch the internal Func seams can't reach. This is the payoff of the
   MFTLib 0.3 `IElevationProvider` refactor.

2. **Genuinely-privileged operations** - `MftVolume.Open` reads a raw NTFS volume handle; nothing can fake
   that, it needs real admin. Covered by `[Category("RequiresAdmin")]` tests
   (`GitWizardTests/ElevationCoverageTests.cs`) guarded on the **first line of the test body** with
   `if (!ElevationUtilities.IsElevated()) { Assert.Inconclusive("Requires admin"); return; }`. A normal
   `dotnet test` skips them (Inconclusive, **zero UAC**); the self-elevating coverage run includes them.

   > NUnit does **not** reliably skip a test body when the guard is in `[SetUp]` - it must be in the body
   > (matches MFTLib's `UsnJournalLiveTests`). A SetUp-only guard lets the body run the real provider and
   > fire UAC.

## Running the full (elevated) coverage

```powershell
.\scripts\run-coverage.ps1                  # self-elevates once (UAC) to include RequiresAdmin tests
.\scripts\run-coverage.ps1 -NonInteractive  # skip RequiresAdmin (CI / headless) - the figures above
.\scripts\run-coverage.ps1 -NoBuild         # tests already built (e.g. via VS MSBuild while MFTLib is a local ProjectReference)
```

Coverage is merged across the non-admin and elevated runs by `ci/post-coverage-status.py` (the same parser
the CI gate uses - it ORs line hits across every `coverage.cobertura.xml` under `TestResults`).

## Relationship to the CI gate

The CI **Linux** job gates on **55% line** coverage of the cross-platform code (`ci/post-coverage-status.py
--gate-line 55`). That is a separate measurement from the Windows figures above: the Windows-only MFT/elevation
code is out of scope on Linux. git-wizard's coverage truth is the **union** of the Linux-measured cross-platform
code and the elevated-Windows-measured privileged code.
