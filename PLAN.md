# GitWizard - Plan

## Goals

- **Ship MFTLib 0.3 and its consumers** (milestone: 0.3, target: none)
  Disposition: almost there. Immaculate code, an API that survives without
  breaking changes for the foreseeable future, and performance limited only
  by physics: MFTLib exists to be the fastest possible way to enumerate an
  NTFS volume. If the comparative benchmarks do not hold up the way 0.2's did,
  it is not ready, whatever else is done. Dogfood through file-wizard and
  git-wizard; the owner mints the release and nobody proposes publishing.
  Done when: the attended acceptance session passes, benchmarks beat 0.2's
  comparison set, and both consumers have run on the API for a week without
  an API change.

- **git-wizard ready for the MFTLib 0.3 release** (milestone: git-wizard-0.3, target: none)
  Disposition: the existing filters, sorting, UI and UX are fine as they are.
  Nothing blocks the UI thread for more than 500 ms. Where possible the
  initial scan stays under a minute, and no individual repository is still
  updating more than a minute later; pathological edge cases like chromium
  can remain outliers. Repository statuses update live via the watcher. The
  one-shot scan works on Linux, where it can be a little slow, and the app is
  testable on Linux.
  Done when: nothing blocks the UI thread for more than 500 ms, the initial
  scan finishes within a minute where possible with no repository still
  updating a minute later (outliers like chromium aside), statuses update
  live via the watcher, and the one-shot scan runs and is testable on Linux.

- **Polished cleanup scenarios** (milestone: cleanup-scenarios, target: none)
  Disposition: a goal beyond the 0.3 release. Polish the use-case and scenario
  runs for cleaning up local-only branches, submodule sync and the like.
  Done when: the use-case and scenario runs for cleaning up local-only
  branches, submodule sync and similar jobs are polished.

## Infrastructure

- [ ] **Retire the MFTLib submodule for a NuGet `PackageReference`** - repo-root `Directory.Build.targets` is a TEMPORARY source-build bridge that unblocks the build while MFTLib 0.3.0 is unpublished. Tracked as [#134](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/134); when 0.3.0 ships to NuGet, follow the retire steps in the `Directory.Build.targets` header and confirm CI stays green.

## Backlog

Scheduled after MFTLib 0.3.0; items tracked on Gitea carry the `ship:mftlib-0.4.0` label.

Still-live ideas migrated from the legacy `.plan` (2026-06-19) when that pre-Avalonia
file was retired. Not yet scheduled; promote to Next Up / a Gitea issue when picked up.

- [ ] ~~**Explore a path-handling library** for `%USERPROFILE%` / `~` aliases.~~
      *Closed (2026-11-06): in-house `GitWizardApi.ExpandSearchPath` / `PrettyPrintPath` /
      `ExpandIgnoredPath` cover `%VAR%` and `~` aliases; no NuGet package expands `~` plus
      environment variables cross-platform, so no library is adopted. The three expander
      copies have drifted (`PrettyPrintPath` leaves a bare `~` alone); a shared helper is a
      separate refactor, not a library question.*
- [x] **Async save/load for cached files** (configs and reports) - done: report persistence now has `ReadJsonObjectFromFileAsync`, `WriteAtomicAsync`, `SaveAtomicAsync` and `MergeIntoFileAsync`; the CLI merge path is async.
      *Note (2026-11-06): configuration and report persistence are both async now; the
      synchronous `Save`, `SaveAtomic` and `MergeIntoFile` remain for synchronous callers.*
- [x] **Make filters additive rather than mutually exclusive.** (`FilterType` is now a flags enum;
      active filters intersect, so each one narrows the list.)
- [x] **Review refresh concurrency/timeout** - all six phases in
      `docs/refresh-concurrency-review.md` are done:
      per-repository refresh gate (`_refreshGate`, phase 1,
      [#159](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/pulls/159));
      cooperative cancellation between phases via a `CancellationTokenSource(RefreshTimeout)` that
      replaced the old `Task.Run`/`Wait` pair (phase 2, landed together with the hazard 7 fix in
      [#162](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/pulls/162)); report-dictionary
      locking and snapshot-on-save (phase 4,
      [#157](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/pulls/157)); and the small
      `Interlocked`/`int?` fixes (phase 6,
      [#158](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/pulls/158)).
      Phase 3 (single-owner bookkeeping): `RefreshError` is cleared at the top of a refresh and the
      recovered-path pruning fix shipped in [#156](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/pulls/156);
      `GitWizardRepository.Refresh` is the only writer of `RefreshTimeSeconds`, timing itself under
      `_refreshGate` with its `RefreshClock`, and `GitWizardReport.RepositoryRefresh.cs` and
      `RepositoryCommandsViewModel.cs` read the value back.
      Phase 5 shipped with the refresh-scheduling change for issue 453: `RefreshSubmodules` and
      `RefreshWorktrees` now iterate sequentially, so the parent handle is touched by one thread.
