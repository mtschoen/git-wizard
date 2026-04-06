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

- projdash `GitWizardAdapter` reads `report.json` or calls CLI with `--paths` for its tracked projects
- projdash `queries.py` gains a `refresh_from_gitwizard()` function to bulk-update git state
- MCP `get_project` and `list_projects` can use GitWizard data when available (faster, richer)
