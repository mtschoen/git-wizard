# Refresh Memory and Scheduling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce refresh peak memory and wall time for git-wizard issues 450, 451, and 453 without changing report schema 2.1.

**Architecture:** Configure libgit2 from each application entry point before ordinary startup. Gate every repository refresh on giant local pack indexes, order top-level work by cached refresh durations with an unbuffered partitioner, and execute nested submodule/worktree refreshes sequentially so outer parallelism is not multiplied.

**Tech Stack:** C#/.NET 10, LibGit2Sharp 0.31.0, libgit2 native P/Invoke, NUnit.

**Spec:** `C:/Users/mtsch/MFTLib-worktrees/reports/lanes-17/kimi-450-451-453.md` and Gitea issues 450, 451, and 453, including their acceptance measurements.

## Global Constraints

- Keep the existing `external/MFTLib` modification unstaged and never commit its pointer.
- Use CRLF, full-word identifiers, no em-dashes, and production files below about 500 lines.
- Every behavior change needs an observed red test followed by a green test; never assert elapsed wall time.
- Commit once per issue with the required co-author trailer; do not push or open a pull request.
- Do not edit `TEST-REPORT.md`, `.aislop/config.yml`, live configuration, or logs in the repository.

## Review Focus

- Native option calls must use the option numbers and signed widths from the pinned libgit2 API, verified through native getters.
- A 100 MiB pack index is not giant; only a strictly larger index is serialized.
- Cached timings must not change path membership or report serialization, and ties retain discovery order.
- Cancellation while waiting for a giant pack must follow the existing timeout path.
- Nested refresh must still create and refresh every submodule/worktree and preserve callback behavior.

---

### Task 1: Issue 450 libgit2 memory limits

**Files:**
- Create: `GitWizard/LibGit2MemorySettings.cs`
- Modify: `git-wizard/Program.cs`, `GitWizardUI/Program.cs`, `GitWizardUI.Screenshot/Program.cs`
- Create: `GitWizardTests/Infrastructure/LibGit2MemorySettingsTests.cs`

- [x] Add a native-getter test asserting a 512 MiB total mapped-window limit and 128 MiB object cache; run it against the unchanged library and watch the assertion fail.
- [x] Add an application-startup helper that calls `git_libgit2_opts` option 3 with `nuint` 512 MiB and option 7 with `nint` 128 MiB, checking both return codes. Invoke it after child-mode dispatch in the CLI, desktop, and screenshot entry points. Document the measured rationale for both limits.
- [x] Rebuild and rerun the native-getter test, then the infrastructure/report neighbors.
- [x] Run short startup and CLI smoke checks only; the resumed instruction explicitly defers long interleaved profiling to the orchestrator's quiet box. Record the chosen limits and leave final performance counts to that measurement.
- [x] Run `aislop scan --staged`, stage only issue 450 files, commit, and confirm with `git log`.

### Task 2: Issue 451 giant-pack serialization

**Files:**
- Create: `GitWizard/GiantPackRefreshGate.cs`
- Modify: `GitWizard/GitWizardRepository.cs`
- Create: `GitWizardTests/Repository/Refresh/GiantPackRefreshGateTests.cs`

- [ ] Add refresh-level regression tests using real temporary repositories and sparse `.idx` files: two indexes strictly over 100 MiB never overlap phase 0, while exactly 100 MiB does not force serialization. Watch the over-threshold test fail on the ungated code.
- [ ] Implement a process-wide semaphore of one around `RefreshLocked` for paths with any `.git/objects/pack/*.idx` strictly over 100 MiB. Wait with the existing cancellation token and release in `finally`/disposal; place detection inside the existing refresh exception boundary.
- [ ] Rerun the regression and refresh/cancellation neighbors red-to-green.
- [ ] Run at least six interleaved issue-450/issue-451 probe and timing measurements with `time-phases.ps1 -RequireQuiet`; record spread, old-low-mode comparison, summed refresh seconds, wall seconds, and commands without committing logs.
- [ ] Run `aislop scan --staged`, stage only issue 451 files, commit, and confirm with `git log`.

### Task 3: Issue 453 refresh scheduling

**Files:**
- Create: `GitWizard/GitWizardReport.RefreshScheduling.cs`
- Modify: `GitWizard/GitWizardReport.cs`
- Modify: `GitWizard/GitWizardRepository.Submodules.cs`
- Modify: `GitWizard/GitWizardRepository.Worktrees.cs`
- Modify: `GitWizardTests/Repository/Refresh/RefreshPhaseObservationAndCancellationTests.cs`
- Create: `GitWizardTests/Reports/GitWizardReportRefreshSchedulingTests.cs`

- [ ] Add issue-tagged ordering tests through `GitWizardReport.Refresh`: prior timings put the longest repository first, while absent/equal timings retain input order. Use the issue-451 gate and phase observers for deterministic assertions, and watch them fail against `ConcurrentBag` scheduling.
- [ ] Preserve pruned path order in a list, snapshot timings from the cached report (or already populated report), stable-sort descending by `RefreshTimeSeconds`, and feed `Partitioner.Create(..., EnumerablePartitionerOptions.NoBuffering)` to the existing outer loop.
- [ ] Replace both nested parallel loops with sequential `foreach` loops, retaining cancellation checks and all creation/refresh callbacks. This caps nested concurrency at one so total refresh concurrency remains at the existing outer cap.
- [ ] Update comments/docstrings that still describe nested parallel behavior, then rerun scheduling, submodule, worktree, cancellation, and report tests.
- [ ] Run interleaved issue-451/issue-453 `time-phases.ps1 -RequireQuiet` measurements; require at least 5 seconds lower total wall, at least 1 second lower post-refresh tail, and no regression in summed refresh seconds. Record counts and commands.
- [ ] Run `aislop scan --staged`, stage only issue 453 files, commit, and confirm with `git log`.

### Task 4: Final verification and handoff

- [ ] Review and consolidate new tests; record net-new functions/cases and test-line delta. Run scaffolding regression checks and `dotnet format git-wizard.slnx --verify-no-changes`.
- [ ] Run the required lane build and one full `dotnet test git-wizard.slnx -c Release --no-build` pass with unique external results storage.
- [ ] Before the final commit run `bash tools/ci-preflight.sh --all`, reading real gate results separately from simulated self-test failures; resolve findings and repeat affected checks.
- [ ] Run installed `aislop scan .` to score 100 and `aislop scan --staged` before the last commit. Do not generate or edit `TEST-REPORT.md` because repository policy reserves it for CI on main.
- [ ] Exercise the built CLI on an isolated synthetic home/repository to verify startup and refresh end to end. Run the docs-update check and remove this temporary plan.
- [ ] Write `C:/Users/mtsch/MFTLib-worktrees/reports/lanes-17/kimi-450-451-453-report.md` with files, commit SHAs, red/green evidence, before/after counts, aislop score, performance counts/commands, limits and judgment calls, and any unverified acceptance item. Confirm all commits with `git log`; do not push.
