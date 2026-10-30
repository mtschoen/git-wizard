# Refresh Memory and Scheduling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce refresh peak memory and wall time for git-wizard issue 453 without changing report schema 2.1.

**Architecture:** Order top-level work by cached refresh durations with an unbuffered partitioner, and execute nested submodule/worktree refreshes sequentially so outer parallelism is not multiplied.

**Tech Stack:** C#/.NET 10, LibGit2Sharp 0.31.0, libgit2 native P/Invoke, NUnit.

**Spec:** `C:/Users/mtsch/MFTLib-worktrees/reports/lanes-17/kimi-453.md` and Gitea issue 453, including its acceptance measurements.

## Global Constraints

- Keep the existing `external/MFTLib` modification unstaged and never commit its pointer.
- Use CRLF, full-word identifiers, no em-dashes, and production files below about 500 lines.
- Every behavior change needs an observed red test followed by a green test; never assert elapsed wall time.
- Commit with the required co-author trailer; do not push or open a pull request.
- Do not edit `TEST-REPORT.md`, `.aislop/config.yml`, live configuration, or logs in the repository.

## Review Focus

- Cached timings must not change path membership or report serialization, and ties retain discovery order.
- Nested refresh must still create and refresh every submodule/worktree and preserve callback behavior.

---

### Task 1: Issue 453 refresh scheduling

**Files:**
- Create: `GitWizard/GitWizardReport.RefreshScheduling.cs`
- Modify: `GitWizard/GitWizardReport.cs`
- Modify: `GitWizard/GitWizardRepository.Submodules.cs`
- Modify: `GitWizard/GitWizardRepository.Worktrees.cs`
- Modify: `GitWizardTests/Repository/Refresh/RefreshPhaseObservationAndCancellationTests.cs`
- Create: `GitWizardTests/Reports/GitWizardReportRefreshSchedulingTests.cs`, `GitWizardTests/Repository/Refresh/NestedRefreshSchedulingTests.cs`
- Create: `GitWizardTests/TestSupport/RefreshOverlapProbe.cs`
- Modify: `docs/testing.md`

- [x] Add issue-tagged tests at the report ordering seam consumed by `GitWizardReport.Refresh`: prior timings put the longest repository first, while absent/equal timings retain input order. Avoid asserting operating-system thread acquisition order, and watch the behavioral versions fail against `ConcurrentBag` scheduling before implementation.
- [x] Preserve pruned path order in a list, snapshot timings from the cached report (or already populated report), stable-sort descending by `RefreshTimeSeconds`, and feed `Partitioner.Create(..., EnumerablePartitionerOptions.NoBuffering)` to the existing outer loop.
- [x] Replace both nested parallel loops with sequential `foreach` loops, retaining cancellation checks and all creation/refresh callbacks. This caps nested concurrency at one so total refresh concurrency remains at the existing outer cap.
- [x] Update comments/docstrings that still describe nested parallel behavior, then rerun scheduling, submodule, worktree, cancellation, and report tests.
- [x] Run scheduling, nested-refresh, cancellation, and CLI smoke checks only; defer wall-time and post-refresh-tail acceptance measurements to the orchestrator's quiet box as directed after the outage.
- [x] Run `aislop scan --staged`, stage only issue 453 files, commit, and confirm with `git log`.

### Task 2: Final verification and handoff

- [ ] Review and consolidate new tests; record net-new functions/cases and test-line delta. Run scaffolding regression checks and `dotnet format git-wizard.slnx --verify-no-changes`.
- [ ] Run the required lane build and one full `dotnet test git-wizard.slnx -c Release --no-build` pass with unique external results storage.
- [ ] Before the final commit run `bash tools/ci-preflight.sh --all`, reading real gate results separately from simulated self-test failures; resolve findings and repeat affected checks.
- [ ] Run installed `aislop scan .` to score 100 and `aislop scan --staged` before the last commit. Do not generate or edit `TEST-REPORT.md` because repository policy reserves it for CI on main.
- [ ] Exercise the built CLI on an isolated synthetic home/repository to verify startup and refresh end to end. Run the docs-update check and remove this temporary plan.
- [ ] Write `C:/Users/mtsch/MFTLib-worktrees/reports/lanes-17/kimi-453-report.md` with files, commit SHAs, red/green evidence, before/after counts, aislop score, performance counts/commands, limits and judgment calls, and any unverified acceptance item. Confirm all commits with `git log`; do not push.
