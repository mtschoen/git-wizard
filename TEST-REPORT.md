# Test & Coverage Report - git-wizard

**Status:** PASS - 890 non-admin tests pass, **0 build/analyzer findings** (analyzer gate + `dotnet format` + jb inspectcode clean), **aislop 100/100** (`aislop ci .` clean, gate green).
**Mode:** coverage gate = pass (**81.75%** line ≥ 45%); sweep classification, branch counting, stash counting, error isolation, parser, serialization, and CLI entry-point behavior are covered. The built CLI was exercised against a disposable repository.
**Change:** Added the `--sweep` JSON contract for dirty tracked files, branches with commits absent from all remote-tracking refs, and stash counts; documented local/SSH agent operation and recovery bundling; added process-level coverage of normal, minified, and invalid sweep invocations.
**Branch:** `agent/1172-agent-skill-machine-wide-unpushe` (issue #101, based on `94650b4`).
**Last measured:** 2026-07-28, Linux, `Release`, non-admin tier.
**Command:** `cmake -S external/MFTLib/MFTLibNative -B external/MFTLib/build-linux -G Ninja -DCMAKE_BUILD_TYPE=Release` · `cmake --build external/MFTLib/build-linux` · `dotnet build git-wizard.slnx -c Release --no-restore` · `dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build --collect:"XPlat Code Coverage"` · `python3 ci/post-coverage-status.py --cobertura "TestResults/**/coverage.cobertura.xml" --gate-line 45 --summary --skip-post` · `dotnet format git-wizard.slnx --verify-no-changes` · `jb inspectcode git-wizard.slnx --settings=git-wizard.slnx.DotSettings --severity=WARNING --no-updates --properties:Configuration=Release` · `aislop ci .`.
**Git:** `agent/1172-agent-skill-machine-wide-unpushe` at `f3817a2` plus integration-test/report edits

## Results

| Metric | Value |
| --- | --- |
| Tests passed (non-admin tier) | 890 |
| Tests passed (`RequiresAdmin` tier, elevated) | Not run on Linux |
| Failed | 0 |
| Skipped on Linux (Windows-only MFT/elevation guards) | 3 |
| **Line coverage (non-admin, `Release`)** | **81.75%** (CI gate: 45%) |
| **Branch coverage (non-admin, `Release`)** | **79.25%** |
| `[ExcludeFromCodeCoverage]` annotations | 0 (Exclusions configured cleanly via `coverlet.runsettings` for Views, UI Services wrappers, and WindowsDefender) |

> These are the Linux CI-equivalent figures from one fresh Cobertura report. The elevated Windows
> tier was not run because this change does not touch privileged MFT or Defender behavior.

## Lint gate (PASS - 0 findings)

The linter rollout drove every configured analyzer/inspection to **zero** and baked the gate into the build + CI.

| Tool | Findings | How it runs |
| --- | --- | --- |
| Roslyn analyzers (CA*/IDE*) + Roslynator 4.15.0 | **0** | `Directory.Build.props`: `EnableNETAnalyzers`, `AnalysisLevel=latest-Recommended`, `EnforceCodeStyleInBuild`, `TreatWarningsAsErrors` → the Release build **is** the gate. |
| `dotnet format` (`.editorconfig`) | **0** | `dotnet format git-wizard.slnx --verify-no-changes` (CI Linux job) + an on-save PostToolUse hook (local, `.claude/`). |
| jb inspectcode (deep ReSharper) | **0** | `jb inspectcode … --severity=WARNING` gated by `ci/parse-jb-report.py` (CI Windows job). |
| aislop (AI-slop, C# engine) | **100** (0 findings) | `.gitea/workflows/aislop.yml` clones + builds the C#-capable fork `github.com/mtschoen/aislop` at a pinned commit (the public npm `aislop` is Python-only) and runs it against `.aislop/config.yml` (csharp.yml shape, `maxFileLoc: 400`, `failBelow: 100`); the job also installs `roslynator.dotnet.cli` and fails on a <10-file scan. This branch took the C# score 13 → 45 → **100**: all error-level, null-forgiving, todo-stub, narrative-comment, and file/function-too-large findings cleared. `failBelow` raised to 100 to match the repo's 0-findings bar. |

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

The CI **Linux** job gates on **45% line** coverage of the cross-platform code (`ci/post-coverage-status.py
--gate-line 45`). That is a separate measurement from the Windows figures above: the Windows-only MFT/elevation
code is out of scope on Linux. git-wizard's coverage truth is the **union** of the Linux-measured cross-platform
code and the elevated-Windows-measured privileged code.
