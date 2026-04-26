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

### CLI polish

- [x] **Honor `--help` / `-h` / `-?`**: currently any unrecognized flag falls through and runs a full scan. Print usage (flags: `-filter`, `-paths`, `-summary`, plus the version banner from `Program.cs`) and exit 0 instead. Skip the elevation hidden args (`--elevated-mft`, `--elevated-defender`) from public help.
