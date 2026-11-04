# GitWizard - Plan

## Infrastructure

- [ ] **Trust the act_runner artifact backend on the Linux runner** - `ci.yml`'s coverage-artifact upload (`ChristopherHX/gitea-upload-artifact`) is `continue-on-error` because the Linux runner's Node does not trust the mkcert CA serving the act_runner artifact endpoint (`ACTIONS_RESULTS_URL` in the runner config on llamabox, which may differ from `GITHUB_SERVER_URL`; `NODE_EXTRA_CA_CERTS` is not resolving). Install that CA into the runner's Node/system trust store, drop `continue-on-error`, and validate with a live CI run. Needs runner-side access to llamabox; not doable from a dev box. The coverage *gate* reads the cobertura file directly, so only the supplementary artifact is affected.
- [ ] **Retire the MFTLib submodule for a NuGet `PackageReference`** - repo-root `Directory.Build.targets` is a TEMPORARY source-build bridge that unblocks the build while MFTLib 0.3.0 is unpublished. Tracked as [#134](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/134); when 0.3.0 ships to NuGet, follow the retire steps in the `Directory.Build.targets` header and confirm CI stays green.

## Backlog

Scheduled after MFTLib 0.3.0; items tracked on Gitea carry the `ship:mftlib-0.4.0` label.

Still-live ideas migrated from the legacy `.plan` (2026-06-19) when that pre-Avalonia
file was retired. Not yet scheduled; promote to Next Up / a Gitea issue when picked up.

- [ ] **Explore a path-handling library** for `%USERPROFILE%` / `~` aliases.
      *Note (2026-08-28): the aliases themselves are already handled in-house
      (`GitWizardApi.PrettyPrintPath` / `ExpandSearchPath`: `%VAR%` via
      `Environment.ExpandEnvironmentVariables`, `~/` and `~\` via `SpecialFolder.UserProfile`).
      Only the library exploration is open; cross out if the in-house version is good enough.*
- [ ] **Async save/load for cached files** (configs and reports).
      *Note (2026-08-28): partial. Configuration has `SaveGlobalConfigurationAsync` /
      `LoadConfigurationAsync`; report persistence (`GitWizardReport.Persistence`:
      `ReadJsonObjectFromFile` / `WriteAtomic`) is still synchronous.*
- [ ] **Make filters additive rather than mutually exclusive.**
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
