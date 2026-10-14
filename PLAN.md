# GitWizard - Plan

## Next Up

### Single-repo merge refresh (for projdash fallback) - tracked as [#42](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/42)

Projdash falls back to per-repo `git` subprocess when the cache misses a repo
(fresh clones, repos created since the last full scan). On Windows that path
can hang for tens of minutes when a grandchild process inherits the pipe
handles - caught a 25-minute wedge in `~/.projdash/mcp.log` on 2026-04-26.
Projdash now bounds it to ~10s via abandon-on-timeout (projdash commit
`c72a185`, `scanner/git.py:_run_capture`), but the fallback still produces
empty git state for the missing repo. A targeted single-repo refresh in
GitWizard would let projdash skip subprocess git entirely.

- [ ] **Projdash consumer change** (tracked in projdash's own PLAN): replace
      the `git` subprocess fallback in `_enrich` with
      `refresh_report(..., merge=True)` followed by re-reading the cache.
      Also harden the `subprocess.run` inside `gitwizard.refresh_report` with
      the same abandon-on-timeout pattern as `scanner/git.py:_run_capture`.

## Infrastructure

- [ ] **Trust the act_runner artifact backend on the Linux runner** - `ci.yml`'s coverage-artifact upload (`actions/upload-artifact@v3`) is `continue-on-error` because the Linux runner's Node does not trust the mkcert CA serving the act_runner artifact endpoint (`ACTIONS_RESULTS_URL` in the runner config on llamabox, which may differ from `GITHUB_SERVER_URL`; `NODE_EXTRA_CA_CERTS` is not resolving). Install that CA into the runner's Node/system trust store, drop `continue-on-error`, and validate with a live CI run. Needs runner-side access to llamabox; not doable from a dev box. The coverage *gate* reads the cobertura file directly, so only the supplementary artifact is affected.
- [ ] **Retire the MFTLib submodule for a NuGet `PackageReference`** - repo-root `Directory.Build.targets` is a TEMPORARY source-build bridge that unblocks the build while MFTLib 0.3.0 is unpublished. When 0.3.0 ships to NuGet: delete the `external/MFTLib` submodule + `Directory.Build.targets`, add `<PackageReference Include="MFTLib" Version="0.3.0" />` to `GitWizard/GitWizard.csproj`, and confirm CI stays green. Steps in the `Directory.Build.targets` header.

## Backlog

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
- [ ] **Review refresh concurrency/timeout** - four of the six phases in
      `docs/refresh-concurrency-review.md` are done, phase 3 is partial, and phase 5 remains open:
      per-repository refresh gate (`_refreshGate`, phase 1,
      [#159](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/pulls/159));
      cooperative cancellation between phases via a `CancellationTokenSource(RefreshTimeout)` that
      replaced the old `Task.Run`/`Wait` pair (phase 2, landed together with the hazard 7 fix in
      [#162](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/pulls/162)); report-dictionary
      locking and snapshot-on-save (phase 4,
      [#157](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/pulls/157)); and the small
      `Interlocked`/`int?` fixes (phase 6,
      [#158](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/pulls/158)).
      Phase 3 is partial: `RefreshError` cleared at the top of a refresh and the recovered-path
      pruning fix shipped in [#156](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/pulls/156),
      but single-owner elapsed-time bookkeeping (moving the `RefreshTimeSeconds` write inside
      `GitWizardRepository.Refresh` under `_refreshGate` rather than writing it after gate release
      in `GitWizardReport.cs` / `MainViewModel.Commands.cs`) remains unfinished.
      Phase 5 is also still open: `RefreshSubmodules` (`GitWizard/GitWizardRepository.Submodules.cs`)
      and `RefreshWorktrees` (`GitWizard/GitWizardRepository.BranchesAndWorktrees.cs`) still read the
      parent `Repository` handle (`submodule.WorkDirCommitId`,
      `worktree.WorktreeRepository.Info.WorkingDirectory`) from inside their own
      `Parallel.ForEach` worker bodies instead of hoisting those reads to a sequential pass first,
      so hazard 4 (concurrent access to one LibGit2Sharp handle) is still live.
