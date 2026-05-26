# GitWizard — Plan

## Completed

- [x] Core library: repo discovery (MFT + recursive scan), parallel refresh, LibGit2Sharp
- [x] CLI: report generation, JSON output, caching, scan-only mode
- [x] MAUI UI: CollectionView with grouping, status icons, background refresh
- [x] Per-repo state: branch, dirty, pending changes, local-only commits, remotes, author emails
- [x] Submodule and worktree support
- [x] Windows Defender exclusion setup
- [x] Cached report.json at ~/.GitWizard/

## Next Up

### Projdash integration — batch git reports for LLM consumption

GitWizard already scans 692 repos and caches state in `report.json`. Projdash currently shells out to `git` per-repo (slow, one at a time). The goal: GitWizard produces structured batch reports that projdash (and LLMs via MCP) can consume efficiently.

- [x] **CLI `-filter` flag**: case-insensitive path substring filter
- [x] **CLI `-paths` flag**: newline-separated file or comma-separated list of repo paths
- [x] **CLI `-summary` flag**: condensed summary with dirty/unpushed/stale counts and repos needing attention
- [x] **Recent commit log in report**: `RecentCommits` field on `GitWizardRepository` (last 10 commits)
- [x] **Staleness info in report**: `DaysSinceLastCommit` computed field on `GitWizardRepository`
- [x] **Stable JSON schema**: `docs/report-schema.md` + `SchemaVersion` field on `GitWizardReport`
- [x] **Exit code for attention-needed**: CLI exits with 1 when any repo has pending changes or unpushed commits
- [x] **GUI: Local Only Commits filter button**: new sidebar filter
- [x] **GUI: Stale filter button**: new sidebar filter (30+ days)
- [x] **GUI: Staleness indicator**: display text appends `(Nd)` for repos idle 30+ days

### Projdash consumer side

These tasks live in projdash's PLAN.md but are noted here for cross-reference:

- [x] projdash `GitWizardAdapter` reads `report.json` and shells out to the CLI with `-paths` on demand
- [x] projdash `queries.py` reads the GitWizard report in `_enrich()` with a subprocess fallback (no dedicated `refresh_from_gitwizard` helper — freshness is handled by `refresh_report()` + `projdash scan`/`refresh-git`)
- [x] MCP `get_project` and `list_projects` use GitWizard data when available, plus `gitwizard_status` exposes cache freshness

### Schema 1.1 fixes (projdash feedback)

- [x] `NumberOfPendingChanges` now counts untracked + added + renamed files so it matches `HasPendingChanges` (was 0 for untracked-only repos)
- [x] Added `LocalCommitCount` int field so consumers can show real unpushed counts instead of a bool
- [x] `CurrentSchemaVersion` constant stamped at save time — cached reports from older builds no longer propagate stale version strings
- [x] Documented `WhenWritingDefault` serializer behavior in `docs/report-schema.md` (absent fields = default values, not "unknown")

### Single-repo merge refresh (for projdash fallback) — tracked as [#42](https://gitea.llamabox.internal/schoen/git-wizard/issues/42)

Projdash falls back to per-repo `git` subprocess when the cache misses a repo
(fresh clones, repos created since the last full scan). On Windows that path
can hang for tens of minutes when a grandchild process inherits the pipe
handles — caught a 25-minute wedge in `~/.projdash/mcp.log` on 2026-04-26.
Projdash now bounds it to ~10s via abandon-on-timeout (projdash commit
`c72a185`, `scanner/git.py:_run_capture`), but the fallback still produces
empty git state for the missing repo. A targeted single-repo refresh in
GitWizard would let projdash skip subprocess git entirely.

- [ ] **CLI `-merge` flag**: when set with `-paths` and `-save-path`, read the
      existing JSON if present, update/insert entries for the supplied paths,
      leave other entries intact, write back atomically (temp file + rename).
      Stamps `SchemaVersion = CurrentSchemaVersion`.
- [ ] **Document concurrency in `docs/report-schema.md`**: two concurrent
      callers refreshing disjoint repos — last writer wins for the file as a
      whole. Acceptable; no lockfile.
- [ ] **Projdash consumer change** (tracked in projdash's own PLAN): replace
      the `git` subprocess fallback in `_enrich` with
      `refresh_report(..., merge=True)` followed by re-reading the cache.
      Also harden the `subprocess.run` inside `gitwizard.refresh_report` with
      the same abandon-on-timeout pattern as `scanner/git.py:_run_capture`.

### CLI polish

- [x] **Honor `--help` / `-h` / `-?`**: currently any unrecognized flag falls through and runs a full scan. Print usage (flags: `-filter`, `-paths`, `-summary`, plus the version banner from `Program.cs`) and exit 0 instead. Skip the elevation hidden args (`--elevated-mft`, `--elevated-defender`) from public help.

### Avalonia cross-platform UI

- [x] Extracted view models into `GitWizardUI.ViewModels` shared project behind `IUiDispatcher` / `IUserDialogs` / `IFolderPicker`
- [x] Neutralized MAUI types in `RepositoryNodeViewModel` (color/padding/font as strings)
- [x] Extracted handler logic from MAUI page code-behind into VM methods (`ApplyFilter`/`ApplyGroup`/`ApplySort` etc.)
- [x] MAUI app refactored to consume shared VMs via `MauiUiDispatcher` etc. — manual Windows verification pending
- [x] Avalonia desktop project (`GitWizardAvalonia/`) ports MainPage and SettingsPage
- [x] Native folder picker on Linux/macOS via Avalonia `IStorageProvider`
- [x] Windows-only features (Defender button) gated on `OperatingSystem.IsWindows()`
- [x] Verified scan + filter + group + sort on Linux and Windows (runtime smoke test, 2026-05-23)

### Avalonia → MAUI parity & retirement

Full capability-parity audit (2026-05-26): every MAUI (`GitWizardUI/`) feature is present in
Avalonia, which also has extras (Downstream Branches filter, Clean button, progress bar). **MAUI can
be retired without losing user-facing capability.** Remaining items are cosmetic/convenience polish —
none blocks retirement.

- [x] Scroll position restored across refresh — closed-loop offset correction in
      `MainWindow.RestoreScrollAnchor` (commit `ea6f2ce`); the during-refresh jump-to-top is an
      accepted UX (see CLAUDE.md). Validated by the `avalonia-vsp-scroll-top` spike.
- [x] **Settings: Tips footer + section description labels** — restored the MAUI-only "Tips" block and
      the per-section gray description labels in `SettingsWindow` (commit `ec5a17a`).
- [x] **Fix: `Padding`/`Thickness` binding spam** — Avalonia bindings don't apply the target
      `TypeConverter` (MAUI did), so `ItemPaddingString` (string) → `Padding` threw `InvalidCastException`
      per row on scroll. Added `StringToThicknessConverter` (commit `ec5a17a`). See CLAUDE.md tips.
- [x] **Fix: `RepositoryNotFoundException` spam on refresh** — guarded `Refresh` with `Repository.IsValid`
      so stale/non-repo cache entries skip cleanly (commit `b7a0321`). Cache-pruning follow-up tracked as
      [#48](https://gitea.llamabox.internal/schoen/git-wizard/issues/48).
- [x] **Debounce search** — 200ms debounce in the `MainViewModel.SearchText` setter coalesces rapid
      keystrokes into a single `ApplyFilterAndGrouping` pass (commit `ec5a17a`); the immediate
      `SetSearchText` path is unchanged. Covered by a coalescing regression test.
- [x] **Active filter/group/sort highlight** — the active sidebar button is now bold. `ApplyFilter`/
      `ApplyGroup`/`ApplySort` route through the notifying `Active{Filter,GroupMode,SortMode}`
      properties (and pick up MAUI's re-click-to-clear toggle for Filter/Group); each button binds
      `Classes.active` via `EnumEqualsConverter` to a `Button.active { FontWeight=Bold }` style.
- [x] **Settings: Enter-to-add typed path** — `KeyDown` handlers on the typed search/ignored path
      TextBoxes fire the Add commands on Enter (commit `ec5a17a`).
- [x] **Immediate "Scanning…" indicator** — an `IsScanning` flag (set at refresh start; cleared when
      the first repo surfaces, the determinate progress bar starts, or the refresh ends) drives an
      indeterminate "Scanning for repositories…" overlay on the empty list, filling the cold-start
      discovery gap. The `2026-05-24` parity spec doc is now fully implemented and can be deleted.

## Infrastructure

- [x] **Gitea Actions CI** — `.gitea/workflows/ci.yml` runs `test-linux` (build + full NUnit suite with coverage, gated at 33% line via `ci/post-coverage-status.py`) and `test-windows` (full solution build + tests) on push to `main` and PRs targeting `main`. `.gitea/workflows/release.yml` builds CLI + Avalonia for `win-x64`/`linux-x64`/`osx-x64`, builds the MAUI Windows zip, and creates a Gitea release with all 7 assets attached on `v*` tag pushes. See `CLAUDE.md` § CI infrastructure for runner/bot/branch-protection setup.
- [ ] **Trust llamabox cert on the Windows runner** — currently the MAUI publish and test-results upload use `NODE_TLS_REJECT_UNAUTHORIZED=0` to work around Node.js not trusting the self-signed Caddy cert. Install the cert into the runner's Node/system trust store and remove the env override.
