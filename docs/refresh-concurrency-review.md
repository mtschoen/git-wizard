# Refresh concurrency and timeout review

Reviewer: concurrency review agent. Date: 2026-08-28. Scope: `PLAN.md` line 193.

## Summary

`GitWizardReport.Refresh` blocks a `Parallel.ForEach` worker on `Task.Run(...).Wait(5 minutes)`, and
on expiry it abandons a task that keeps mutating the same `GitWizardRepository` for as long as it
likes, so a timeout hands ownership of a live object to two threads at once. The recommended fix is
the minimal third option: keep the synchronous core, but add a `CancellationToken` checked between
refresh phases, a per-repository refresh gate so no two threads ever refresh one repository object
concurrently, and a snapshot at serialization time. A full async rewrite is not justified because
LibGit2Sharp has no async API, so `Task.Run` would remain either way and the real defect is
ownership, not the threading model.

## Current design

Batch refresh, from the outside in:

1. `GitWizardReport.Refresh` (`GitWizard/GitWizardReport.cs:188`) prunes deleted paths, then runs
   `Parallel.ForEach` over the survivors at `MaxDegreeOfParallelism = ProcessorCount / 2`
   (`GitWizardReport.cs:192`, `GitWizardReport.cs:206`), then prunes non-repository paths
   (`GitWizardReport.cs:220`).
2. `RefreshSingleRepository` (`GitWizardReport.cs:287`) gets or creates the `GitWizardRepository`
   under `lock (Repositories)`, fires `OnRepositoryCreated`, then schedules the actual work with
   `Task.Run` and blocks on `refreshTask.Wait(TimeSpan.FromMinutes(5))` (`GitWizardReport.cs:310`).
   On expiry it logs and calls `repository.MarkRefreshFailed(...)` (`GitWizardReport.cs:314`); either
   way it then writes `repository.RefreshTimeSeconds` (`GitWizardReport.cs:324`).
3. `GitWizardRepository.Refresh` (`GitWizard/GitWizardRepository.cs:101`) opens one LibGit2Sharp
   `Repository`, sets `IsRefreshing = true`, and runs eleven phases in sequence, ending with
   `RefreshSizeOnDisk` (a full recursive file enumeration) and `NotifyRefreshCompleted`. There is no
   cancellation check anywhere inside it.
4. Two of those phases fan out further: `RefreshSubmodules` and `RefreshWorktrees` each run their own
   `Parallel.ForEach` with no degree-of-parallelism cap
   (`GitWizard/GitWizardRepository.Submodules.cs:47`, `GitWizard/GitWizardRepository.BranchesAndWorktrees.cs:14`).

Consumers: the CLI calls `Refresh` on the process-wide cached report
(`git-wizard/Program.Report.cs:75`) and serializes it with `Save` (`Program.Report.cs:119`, `:127`).
The UI wraps `GenerateReportAsync` in `Task.Run`
(`GitWizardUI/ViewModels/MainViewModel.Refresh.cs:238`) and never calls `Save`. UI Live mode calls
`repository.Refresh()` directly on a node's repository object
(`GitWizardUI/ViewModels/MainViewModel.Live.cs:124`), guarded only against other Live refreshes by
`_inFlightLiveRefreshes` (`MainViewModel.Live.cs:110`).

`IUpdateHandler` callbacks are invoked on whichever worker thread is running that phase. Both
implementations are non-blocking and thread-safe by construction: the UI enqueues onto a
`ConcurrentQueue` drained every 250 ms on the dispatcher
(`GitWizardUI/ViewModels/MainViewModel.Commands.cs:296`), and the CLI enqueues onto a
`ConcurrentQueue` drained later on the main thread (`git-wizard/UpdateHandler.cs:129`). Progress
posts are coalesced behind an `Interlocked.CompareExchange` gate
(`GitWizardUI/ViewModels/MainViewModel.UpdateHandler.cs:28`). This part of the design is sound and
should not change.

## Shared-state inventory

| State | Written by | Thread | Synchronization |
| --- | --- | --- | --- |
| `GitWizardReport.Repositories` (`GitWizardReport.cs:48`) | `RefreshSingleRepository` insert | `Parallel.ForEach` worker | `lock (Repositories)` (`GitWizardReport.cs:291`) |
| `GitWizardReport.Repositories` removals | `PruneDeletedRepositories` (`:275`), `PruneNonRepositoryPaths` (`:246`) | caller thread | none. Safe only because both run outside the parallel region of a single `Refresh` call |
| `GitWizardReport.DeletedPaths`, `NonRepositoryPaths`, `SchemaVersion` | prune methods, `Save`/`SaveAsync`/`SaveAtomic` | caller thread | none. Read after `Refresh` returns |
| `GitWizardRepository` scalar fields (`CurrentBranch`, `HasPendingChanges`, `BehindRemoteCount`, ...) | every `Refresh*` phase | the `Task.Run` worker | none |
| `GitWizardRepository.RemoteUrls` (`List<string>`) | `RefreshRemoteUrls` does `Clear()` then `Add` (`GitWizardRepository.cs:212`) | the `Task.Run` worker | none |
| `GitWizardRepository.Branches` (`List<BranchInfo>`) | `CollectBranches` sets null then reassigns (`BranchesAndWorktrees.cs:200`, `:244`) | the `Task.Run` worker | none |
| `GitWizardRepository.SubmoduleHealth` | `CheckSubmoduleHealth` replaces then fills (`Submodules.cs:135`) | the `Task.Run` worker | none |
| `GitWizardRepository.Submodules`, `.Worktrees` | nested `Parallel.ForEach` bodies | nested workers | `lock (Submodules)` / `lock (Worktrees)` for the dictionary only |
| `GitWizardRepository.IsRefreshing` | `Refresh` (`:132`, `:151`, `:158`) and `MarkRefreshFailed` (`BranchesAndWorktrees.cs:78`) | two different threads on timeout | none |
| `GitWizardRepository.RefreshError` | `MarkRefreshFailed` and the `Refresh` catch block | two different threads on timeout | none. Never cleared on success |
| `GitWizardRepository.RefreshTimeSeconds` | `RefreshSingleRepository` (`GitWizardReport.cs:324`) | `Parallel.ForEach` worker | none |
| LibGit2Sharp `Repository` handle | `using var` inside one `Refresh` | one owning thread, plus nested workers via submodule and worktree objects | none |
| `MainViewModel._uiCommands` | all `IUpdateHandler` methods | any worker | `ConcurrentQueue` |
| `MainViewModel._repositoryMap` | `AddRepository`, `RemoveRepositoryPathFromUi` | UI thread | `ConcurrentDictionary` |
| `MainViewModel._allRepositories`, `_pendingGroups` | `AddRepository`, prune helpers | UI thread, except the `RefreshAsync` tail | none. See hazard 9 |
| `MainViewModel._progressCount`, `_progressTotal` (`int?`) | `UpdateProgress`, `StartProgress` | any worker | none |
| `_cachedReport` static (`GitWizardReport.cs:10`) | `GetCachedReport`, `GetCachedReportAsync` | any | `lock (ReportLock)` for the field itself, nothing for the object it points at |

## Hazards

Ordered by severity. Confidence is stated per item.

### 1. The abandoned task after a timeout keeps writing the repository (confidence: certain)

`refreshTask.Wait(timeout)` returning false does not stop anything
(`GitWizardReport.cs:311`). Interleaving: the worker times out, calls `MarkRefreshFailed`, which sets
`RefreshError = "Timed out after 5 minutes"` and `IsRefreshing = false`, and notifies the handler.
The orphan is meanwhile inside `RefreshSizeOnDisk` or `CollectBranches`. It later finishes, sets
`IsRefreshing = false` again, and fires a second `OnRepositoryRefreshCompleted` for the same
repository, so the UI processes two completion commands for one repository and the CLI counts two
completions. Worse, the orphan's success path never clears `RefreshError`, so the repository
displays a timeout while carrying fully refreshed data. `RefreshTimeSeconds` is written by the
parallel worker at `GitWizardReport.cs:324` while the orphan is still writing every other field, so
the persisted object is a mixture of two runs.

### 2. Concurrent `Refresh` calls corrupt the `Repositories` dictionary (confidence: high)

`Repositories` is a `SortedDictionary`. Inserts take `lock (Repositories)`
(`GitWizardReport.cs:291`) but the removals in `PruneDeletedRepositories` (`GitWizardReport.cs:275`)
and `PruneNonRepositoryPaths` (`GitWizardReport.cs:246`) do not. Within one `Refresh` call that is
fine, because the prunes bracket the parallel region. Across two concurrent `Refresh` calls on one
report it is not: thread A's prune can rebalance the red-black tree while thread B's parallel body
inserts under a lock A never takes, which can corrupt the tree, throw, or loop forever. This is not
theoretical: `GitWizardTests/GitWizardReportTests.cs:48` runs `Parallel.For(0, 10, _ =>
report.Refresh(repositoryPaths))` against a single report, so the test suite exercises exactly this
interleaving today and passes only because the window is narrow. That test also asserts nothing.

### 3. Live-watch refresh overlaps a batch refresh on the same repository object (confidence: high)

`RefreshAsync` returns early if `IsRefreshing` (`MainViewModel.Refresh.cs:144`) and `ToggleLiveAsync`
returns early if `IsRefreshing` (`MainViewModel.Live.cs:222`), but nothing stops an already-running
Live watch from firing during a batch refresh. `_inFlightLiveRefreshes` only excludes other Live
refreshes of the same root. Interleaving: Live raises `Changed` for repository X, the UI thread reads
`node.Repository` (`MainViewModel.Live.cs:113`), and `Task.Run(() => repository.Refresh())` runs
while the batch `Parallel.ForEach` is refreshing the same object. The two LibGit2Sharp handles are
distinct so libgit2 itself is safe, but `RefreshRemoteUrls` does `RemoteUrls.Clear()` followed by
`Add` on an unsynchronized `List<string>` (`GitWizardRepository.cs:212`), which can throw
`IndexOutOfRangeException`, produce duplicates, or silently drop entries. `SubmoduleHealth` and
`Branches` are reassigned mid-flight the same way.

### 4. Nested parallelism over one LibGit2Sharp repository handle (confidence: high)

`RefreshSubmodules` runs `Parallel.ForEach(submodules, ...)` and reads `submodule.WorkDirCommitId`
(`Submodules.cs:62`) on worker threads; `RefreshWorktrees` runs `Parallel.ForEach(worktrees, ...)`
and reads `worktree.WorktreeRepository.Info.WorkingDirectory`
(`BranchesAndWorktrees.cs:24`). Both `Submodule` and `Worktree` are lazy wrappers over the parent
`git_repository`, so this is concurrent access to one repository handle from several threads, which
libgit2's threading document explicitly forbids: the library is thread-safe only across distinct
repository objects. The symptom would be a native-level fault or garbage data rather than a managed
exception, which is consistent with it never having been diagnosed. This is separate from the
timeout question but lives in the same call tree and should be fixed alongside it.

### 5. `Save` silently loses the report when an orphan is mutating (confidence: high, CLI only)

`GitWizardReport.Save` calls `JsonSerializer.Serialize(this, ...)` inside a `try` whose `catch` only
logs (`GitWizardReport.cs:337`). Interleaving: a repository times out during
`cachedReport.Refresh(...)` (`Program.Report.cs:75`), the orphan is still inside `RefreshRemoteUrls`
or `CollectBranches` when `SaveReport` runs at `Program.Report.cs:119`, and the serializer enumerates
a `List` that is being mutated. `InvalidOperationException: Collection was modified` is swallowed and
the run reports success while `report.json` is left at its previous contents. `SaveAtomic` and
`MergeIntoFile` share the same exposure through `WriteAtomic`. The file-level race between two
`MergeIntoFile` callers is already documented and accepted at
`GitWizardReport.Persistence.cs:17`, so it is out of scope here.

### 6. `RefreshError` is never cleared on success (confidence: certain, not a race)

Every write is `MarkRefreshFailed` (`BranchesAndWorktrees.cs:77`) or the catch block
(`GitWizardRepository.cs:156`); nothing sets it back to null. Because `RefreshError` has a public
setter and is serialized, a repository that failed in one run keeps its stale error text through
every later successful run. This has a second-order effect: `PruneNonRepositoryPaths`
(`GitWizardReport.cs:236`) removes any entry whose `RefreshError` equals `NotAGitRepositoryError`, so
a path that was transiently not a repository (mid-clone, unmounted then remounted) is pruned from the
cache even after a later refresh finds it healthy. In the UI, `RepositoryNodeViewModel.Update`
(`GitWizardUI/ViewModels/RepositoryNodeViewModel.cs:201`) keeps showing the error status.

### 7. Blocking `Wait` inside `Parallel.ForEach` risks thread-pool starvation (confidence: medium)

Up to `ProcessorCount / 2` pool threads sit blocked in `Wait` while the work they are waiting for
needs pool threads of its own, and the nested submodule and worktree loops need more still, with no
cap. The pool grows past its floor only on its slow injection schedule, so on a large scan the
refresh can stall in a way that looks like slowness rather than a deadlock. This is a throughput
defect, not a correctness one, but it is caused by the same `Task.Run` plus `Wait` shape.

### 8. Non-atomic progress counter in discovery (confidence: certain, cosmetic)

`GitWizardReport.DiscoverySession.cs:99` does `updateHandler?.UpdateProgress(++count)` inside a
`Parallel.ForEach`. The sibling loop in `Refresh` correctly uses `Interlocked.Increment`
(`GitWizardReport.cs:212`). Lost updates here mean the discovery progress bar undercounts and can
fail to reach 100 percent, which also leaves `IsProgressVisible` true because the UI pump only hides
the bar on an exact `_progressTotal == _progressCount` match
(`GitWizardUI/ViewModels/MainViewModel.Commands.cs:324`).

### 9. Lower-severity items (confidence: medium to high, low impact)

- `_progressCount` and `_progressTotal` are `int?`, a two-field struct, written by workers and read
  by the UI pump with no synchronization. A torn read yields a wrong progress number for one frame.
- `MainViewModel._allRepositories` is a plain `List` and `_pendingGroups` a plain `Dictionary`. The
  documented behavior that the `RefreshAsync` tail runs on a pool thread (`AGENTS.md`, Tips) means
  `ApplyFilterAndGrouping` enumerates `_allRepositories` off the UI thread while queued
  `AddRepository` commands could still append to it. In practice the drain-wait at
  `MainViewModel.Refresh.cs:181` closes this window, so it is currently guarded, but only by timing.
- `CountingUpdateHandler.RefreshCompletedCount++` (`GitWizardTests/TestUpdateHandlers.cs:20`) is
  non-atomic and is called from parallel workers. Any new test that counts callbacks under
  parallelism needs `Interlocked` here first.

Things that are already correctly guarded, for the record: the `_uiCommands` and CLI command queues,
the status-message coalescing gate, `_repositoryMap`, the `ReportLock` around the `_cachedReport`
field, the `lock (Submodules)` and `lock (Worktrees)` dictionary guards, `WriteAtomic`'s
same-volume rename, and the `Interlocked.Increment` in the batch refresh progress counter.

## Options

### Option A: fully async with cooperative cancellation

Convert `GitWizardRepository.Refresh` to `RefreshAsync(CancellationToken)`, replace the outer
`Parallel.ForEach` with `Parallel.ForEachAsync`, and drive the per-repository timeout from a linked
`CancellationTokenSource.CancelAfter`.

- Changes and blast radius: large. `GitWizardRepository` and all three partials,
  `GitWizardReport.Refresh` and `GenerateReportAsync`, `MergeIntoFile`, the CLI call site at
  `Program.Report.cs:75`, the Live path at `MainViewModel.Live.cs:124`, and most of the
  `*Report*Tests` and `*Repository*Tests` files. Every synchronous caller becomes async.
- CLI and UI: the CLI path is straightforward; the UI already runs inside `Task.Run` so it gets a
  little simpler. Testability is good in principle, since a token is easy to trip in a test.
- Risk: high, and the payoff is limited. LibGit2Sharp has no async API (libgit2sharp issue #787), so
  every phase body stays synchronous and still has to be pushed onto a pool thread. The token would
  still only be observable between phases, which is exactly what option C provides at a fraction of
  the cost. Async does not by itself fix hazards 2, 3, or 4.

### Option B: stricter ownership of shared mutable state

Make refresh produce an immutable result object and have the report swap it in under a lock, so no
two threads ever write one repository's fields.

- Changes and blast radius: very large. Split `GitWizardRepository` into a mutable shell plus a
  `RepositoryState` record; rewrite all eleven phases to build the record instead of assigning
  fields; update `RepositoryNodeViewModel` (roughly thirty property reads), the JSON contract (all
  those `private set` properties are the serialization surface), `MergeIntoFile`'s DOM merge, and
  `docs/report-schema.md`.
- Testability: excellent once done; a state record is trivially assertable.
- Risk: high. This is the right long-term shape and it genuinely fixes hazards 1, 3, and 5 at the
  root, but it is a multi-session refactor of the project's central data type, and it does nothing
  for hazard 4 on its own.

### Option C: minimal, keep the synchronous core and fix ownership at the seams

Four independent changes:

1. Thread a `CancellationToken` into `GitWizardRepository.Refresh` and check it between phases.
   `RefreshSingleRepository` builds a `CancellationTokenSource` with `CancelAfter(timeout)` and
   disposes it after the wait, so the orphan stops at the next phase boundary instead of running to
   completion.
2. Add a per-repository refresh gate: a `SemaphoreSlim(1, 1)` field on `GitWizardRepository`, taken
   for the duration of `Refresh`. Overlapping refreshes on one object serialize instead of
   interleaving, which closes hazard 3 and makes `RefreshConcurrencyTest` meaningful.
3. Take `lock (Repositories)` in `PruneDeletedRepositories` and `PruneNonRepositoryPaths`, and
   serialize from a snapshot taken under that lock in `Save`, `SaveAsync`, and `SaveAtomic`.
4. Cap or remove the nested `Parallel.ForEach` over submodules and worktrees so one LibGit2Sharp
   handle is only touched by one thread.

- Blast radius: small. `GitWizardRepository.Refresh` gains an optional parameter with a default, so
  every existing caller compiles unchanged. `Program.Report.cs:75` needs no edit, and
  `MainViewModel.Live.cs:124` gets correct behavior for free through the gate.
- Testability: good. Each step is separately testable without wall-clock assertions.
- Risk: low per step, and every step is independently revertible.

## Recommendation

Take option C now, and treat option B as the direction of travel rather than a prerequisite. The
defect named in `PLAN.md` line 193 is an ownership defect: two threads writing one
`GitWizardRepository`. Option A changes the threading model without fixing ownership, and would
leave hazards 2, 3, and 4 exactly where they are while forcing an async cascade through the CLI, the
UI, and the test suite for no correctness gain, since LibGit2Sharp is synchronous either way. Option
C fixes all six top hazards with additive, individually revertible changes and leaves the door open
to option B later, because once refresh runs under a per-repository gate, swapping the field writes
for a state record becomes a mechanical change rather than a concurrency redesign.

## Implementation sketch

Each phase is independently mergeable and green on its own.

### Phase 1: per-repository refresh gate

Add `readonly SemaphoreSlim _refreshGate = new(1, 1);` to `GitWizardRepository` (marked
`[JsonIgnore]`-equivalent by being a field, so serialization is unaffected). Wrap the body of
`Refresh` in `_refreshGate.Wait()` / `finally { _refreshGate.Release(); }`. Note that
`RefreshSubmodules` and `RefreshWorktrees` call `Refresh` on *different* repository objects, so this
cannot self-deadlock; add a comment saying so.

Test: `Refresh_ConcurrentCallsOnOneRepository_DoNotInterleave`. Give `GitWizardRepository` an
`internal Action? PhaseObserverForTesting` hook invoked at each phase boundary; the test starts two
refreshes on one object from two threads, has the observer record `(threadId, phaseIndex)` into a
`ConcurrentQueue`, and asserts that the recorded sequence contains no thread interleaving. Fails
before (interleaved), passes after (two clean runs). No clock involved.

### Phase 2: cooperative cancellation between phases

Add `CancellationToken cancellationToken = default` to `GitWizardRepository.Refresh` and call
`cancellationToken.ThrowIfCancellationRequested()` before each of the eleven phases and before the
nested loops. Catch `OperationCanceledException` separately from the general catch so it sets
`RefreshError` to the timeout text exactly once rather than reporting a generic exception.

In `RefreshSingleRepository`, replace the bare `Task.Run` plus `Wait` with a
`CancellationTokenSource` whose `CancelAfter` uses a new `internal static TimeSpan RefreshTimeout`
(defaulting to five minutes) so tests can shorten it without sleeping. Keep the `Wait` for now; the
token is what makes the abandonment bounded.

Test: `Refresh_Cancelled_StopsAtPhaseBoundary`. Use the phase observer from phase 1 to cancel the
token at phase three, and assert the observer never records phase four and that `RefreshError`
reports a timeout. Fails before (all eleven phases run), passes after. The assertion is on the
observed call pattern, not on elapsed time.

### Phase 3: single-owner timeout bookkeeping

Move the `RefreshTimeSeconds` write inside `GitWizardRepository.Refresh` so the owning thread is the
only writer, and clear `RefreshError = null` at the top of a successful refresh so hazard 6 goes
away. Keep `MarkRefreshFailed` as the only writer on the failure path.

Test A: `Refresh_AfterPreviousError_ClearsRefreshError`. Set `RefreshError` on a `TempRepoFixture`
repository, refresh, assert it is null. Fails before, passes after.
Test B: `PruneNonRepositoryPaths_AfterPathBecomesValidRepository_KeepsEntry`. Refresh a
non-repository directory, `git init` it, refresh again, assert it survives in `Repositories`. Fails
before (stale `NotAGitRepositoryError` prunes it), passes after.

### Phase 4: lock the report collection and snapshot on save

Introduce a private `readonly object _repositoriesLock` (or keep `lock (Repositories)` consistently)
and take it in `PruneDeletedRepositories`, `PruneNonRepositoryPaths`, and `GetRepositoryPaths`. In
`Save`, `SaveAsync`, and `SaveAtomic`, copy `Repositories` into a new `SortedDictionary` under the
lock and serialize the copy. Note that this bounds the dictionary race, not the per-repository field
race, which phase 1 handles.

Test: `Refresh_ConcurrentCallsOnOneReport_DoNotCorruptRepositories`. Promote the existing
`RefreshConcurrencyTest` (`GitWizardTests/GitWizardReportTests.cs:35`) from a no-assertion smoke test
into one that asserts `Repositories.Count` equals the seeded path count and that every value is
non-null after ten concurrent refreshes. It will be flaky-to-failing before and stable after; run it
with `[Repeat(20)]` to make the before-state visible.

### Phase 5: remove nested parallelism over one LibGit2Sharp handle

In `RefreshSubmodules` and `RefreshWorktrees`, hoist everything that touches the parent
`Repository` handle (`submodule.Path`, `submodule.WorkDirCommitId`,
`worktree.WorktreeRepository.Info.WorkingDirectory`) into a sequential pass on the owning thread that
materializes a plain list of paths and identifiers, then keep the parallel fan-out only for the
recursive `submoduleRepository.Refresh(...)` and `worktreeRepository.Refresh(...)` calls, which
operate on their own repository objects and are safe. Give the remaining loop a
`MaxDegreeOfParallelism` so nested fan-out cannot multiply against the outer loop.

Test: `RefreshSubmodules_DoesNotTouchParentHandleFromWorkerThreads`. `TempRepoFixture` already
supports submodule creation; capture the managing thread id when the parent `Repository` is
constructed and assert through the phase observer that every parent-handle read happened on that
thread. Fails before, passes after.

### Phase 6: small fixes

Replace `++count` with `Interlocked.Increment(ref count)` at
`GitWizard/GitWizardReport.DiscoverySession.cs:99`; make `_progressCount` and `_progressTotal` plain
`int` fields with a separate `bool` (or `volatile int` sentinel) to remove the torn `int?` read; make
`CountingUpdateHandler.RefreshCompletedCount` use `Interlocked`.

Test: `GetRepositoryPaths_ParallelProgress_ReachesTotal`. Drive discovery over several seeded search
paths with a recording handler and assert the maximum reported count equals the path count. Fails
intermittently before, deterministically passes after; use `[Repeat(50)]` rather than a timing
assertion.

Deferred to a follow-up issue, not part of this work: replacing the blocking `Wait` inside
`Parallel.ForEach` (hazard 7). Once phase 2 lands, the timeout is enforced cooperatively and the
`Wait` becomes a throughput concern only, which is a better fit for a separate perf change than for
this correctness pass.

## Sources

- libgit2 threading model, one repository object per thread:
  https://github.com/libgit2/libgit2/blob/main/docs/threading.md
- LibGit2Sharp thread-safety discussion: https://github.com/libgit2/libgit2sharp/issues/64
- LibGit2Sharp has no async API, `Task.Run` is the expected wrapper:
  https://github.com/libgit2/libgit2sharp/issues/787
- Andrew Lock, "Just because you stopped waiting for it, doesn't mean the Task stopped running",
  the canonical statement that a `Wait` with a timeout abandons rather than cancels:
  https://andrewlock.net/just-because-you-stopped-waiting-for-it-doesnt-mean-the-task-stopped-running/
- Stephen Cleary, "A Tour of Task, Part 5: Waiting":
  https://blog.stephencleary.com/2014/10/a-tour-of-task-part-5-wait.html
- Microsoft Learn, "How to: Cancel a Parallel.For or ForEach Loop", the supported cancellation idiom
  for parallel loops:
  https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/how-to-cancel-a-parallel-for-or-foreach-loop
- Microsoft Learn, `Parallel.ForEachAsync`, the async alternative evaluated as option A:
  https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.parallel.foreachasync
