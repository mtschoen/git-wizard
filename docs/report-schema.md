# GitWizard Report JSON Schema

**Current version:** `2.0`

The `SchemaVersion` field at the top of every report indicates the schema version. Breaking changes increment this value.

## Version history

- **2.0** — Per-branch divergence. Added repository `Branches` (`BranchInfo[]`) and top-level `BranchScope`, plus repository `DefaultBranch`, `MatchingBranchName`, and `SizeOnDisk`, and summary `MergedBranches` / the `"merged-branches"` attention reason.
- **1.1** — `NumberOfPendingChanges` counts untracked + added + renamed files (matches `HasPendingChanges`); added `LocalCommitCount`.
- **1.0** — Initial schema.

## JSON serialization note

The report is written with `System.Text.Json`'s `DefaultIgnoreCondition = WhenWritingDefault`. **Numeric fields equal to `0`, boolean fields equal to `false`, and reference fields equal to `null` are omitted from the JSON output.** Consumers should treat absent fields as their default value (`0`/`false`/`null`), not as "unknown". Every non-nullable field documented below is guaranteed to be present *in spirit* — the serializer may simply elide it. (Empty non-null collections are still written, e.g. `[]`.)

## Concurrency

The report file has **no lockfile**. Writes are atomic at the file level — the CLI's
`-merge` flag (and the underlying `GitWizardReport.SaveAtomic`) writes to a temp file in
the same directory and renames it over the destination, so a concurrent reader always sees
either the complete old file or the complete new file, never a half-written one.

There is **no protection against concurrent writers**, however. If two callers refresh
**disjoint** sets of repos at the same time (e.g. projdash issuing a targeted `-merge` for
a freshly-cloned repo while a full rescan is also in flight), each one reads the report,
applies its own changes to that snapshot, and writes the whole file back. **Last writer
wins for the file as a whole** — the later write replaces the file produced by the earlier
write, so the earlier writer's entries can be lost even though the two callers touched
different repos. This is an accepted trade-off: the merge use case (projdash filling in a
cache miss) is low-frequency and self-correcting on the next full scan, and a lockfile would
add cross-process coordination complexity for little benefit. Callers that need a guaranteed
merge of disjoint updates must serialize their writes externally.

## Top-level fields

| Field | Type | Description |
|-------|------|-------------|
| `SchemaVersion` | `string` | Schema version (currently `"2.0"`) |
| `BranchScope` | `string` | How each repo's `Branches` list was filtered: `"actionable"` (default) or `"all"` (with `--all-branches`). Absent on reports written before 2.0. |
| `SearchPaths` | `string[]` | Configured search paths |
| `IgnoredPaths` | `string[]` | Configured ignored paths |
| `Repositories` | `object` | Map of repo path → repository object |
| `DeletedPaths` | `string[]` | Repo paths removed from the cache during the last refresh (no longer on disk). Usually empty/absent. |

## Repository object

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `WorkingDirectory` | `string` | yes | Absolute path to repo working directory |
| `CurrentBranch` | `string` | yes | Current branch name |
| `IsDetachedHead` | `bool` | no | Whether HEAD is detached |
| `HasPendingChanges` | `bool` | no | Whether repo has uncommitted changes (matches libgit2's `IsDirty`) |
| `NumberOfPendingChanges` | `int` | no | Count of modified + staged + removed + added + untracked + renamed files. Always matches `HasPendingChanges` (0 iff false). |
| `IsWorktree` | `bool` | no | Whether this is a git worktree |
| `LocalOnlyCommits` | `bool` | no | Whether any local branch has unpushed commits |
| `LocalCommitCount` | `int` | no | Total unpushed commits across all local branches (sums `AheadOfDefault`-style divergence for tracked branches and all commits on untracked branches). |
| `LastCommitDate` | `string` (ISO 8601) | yes | Timestamp of most recent commit on HEAD |
| `DaysSinceLastCommit` | `int` | yes | Days since last commit (computed at refresh time) |
| `DefaultBranch` | `string` | yes | The repo's default branch name (e.g. `main`); the divergence baseline for `Branches` |
| `MatchingBranchName` | `string` | yes | When HEAD is detached, a local branch whose tip matches HEAD (offer to check out); else null |
| `Branches` | `BranchInfo[]` | yes | Local branches and their divergence from `DefaultBranch`. Filtered to "actionable" branches unless `BranchScope` is `"all"`. Null when none qualify. |
| `SizeOnDisk` | `long` (bytes) | no | Repository size on disk in bytes (0/absent if not computed) |
| `RefreshTimeSeconds` | `float` | no | How long the last refresh took |
| `RefreshError` | `string` | yes | Error message if refresh failed |
| `RemoteUrls` | `string[]` | no | List of remote URLs |
| `AuthorEmails` | `string[]` | yes | Unique author emails from last 200 commits |
| `RecentCommits` | `CommitInfo[]` | yes | Last 10 commits on HEAD |
| `Submodules` | `object` | yes | Map of path → repository object (null if uninitialized) |
| `Worktrees` | `object` | yes | Map of path → repository object |

## BranchInfo object

A local branch's relationship to the repository's default branch. By default the `Branches` list is filtered to "actionable" branches (see `BranchScope`) — it is not a complete inventory unless the report was produced with `--all-branches`.

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Branch short name (e.g. `feature/x`) |
| `IsMerged` | `bool` | True when fully merged into the default branch (`AheadOfDefault == 0`) — safe to delete |
| `MergedInto` | `string` | Default branch name when merged (safe to delete), else null |
| `AheadOfDefault` | `int` | Commits on this branch not reachable from the default branch |
| `BehindDefault` | `int` | Commits in the default branch not reachable from this branch |
| `LastCommitDate` | `string` (ISO 8601) | Author timestamp of the branch tip; null if the branch has no commits |
| `HasUpstream` | `bool` | True when the branch tracks a remote (false ⇒ unpushed local work) |

## CommitInfo object

| Field | Type | Description |
|-------|------|-------------|
| `Hash` | `string` | 7-character short SHA |
| `Message` | `string` | First line of commit message |
| `Date` | `string` (ISO 8601) | Author date |
| `AuthorEmail` | `string` | Author email |

## Summary output (`-summary` flag)

| Field | Type | Description |
|-------|------|-------------|
| `SchemaVersion` | `string` | Schema version |
| `TotalRepositories` | `int` | Total repo count |
| `Dirty` | `int` | Repos with uncommitted changes |
| `Unpushed` | `int` | Repos with local-only commits |
| `Stale` | `int` | Repos with no commits in 30+ days |
| `MergedBranches` | `int` | Repos with at least one merged (safe-to-delete) branch |
| `NeedingAttention` | `AttentionItem[]` | Repos that are dirty, have unpushed commits, or have merged branches |

## AttentionItem object

| Field | Type | Description |
|-------|------|-------------|
| `Path` | `string` | Repository path |
| `Reasons` | `string[]` | Why it needs attention (`"dirty"`, `"unpushed"`, `"merged-branches"`) |

## Exit codes

| Code | Meaning |
|------|---------|
| `0` | All repos clean and pushed |
| `1` | At least one repo has pending changes or unpushed commits |
