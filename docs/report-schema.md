# GitWizard Report JSON Schema

**Current version:** `2.1`

The `SchemaVersion` field at the top of every report indicates the schema version. Breaking changes increment this value.

## Version history

- **2.1** - Behind-remote detection ([#78](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/78)). Added repository `BehindRemoteCount` / `AheadOfRemoteCount` (the checked-out branch vs its upstream - see the disambiguation note under **Repository object** below), `LastFetchTime`, and `IsPublishReady`, plus summary `BehindRemote` / the `"behind-remote"` attention reason.
- **2.0** - Per-branch divergence. Added repository `Branches` (`BranchInfo[]`) and top-level `BranchScope`, plus repository `DefaultBranch`, `MatchingBranchName`, and `SizeOnDisk`, and summary `MergedBranches` / the `"merged-branches"` attention reason.
- **1.1** - `NumberOfPendingChanges` counts untracked + added + renamed files (matches `HasPendingChanges`); added `LocalCommitCount`.
- **1.0** - Initial schema.

## JSON serialization note

The report is written with `System.Text.Json`'s `DefaultIgnoreCondition = WhenWritingDefault`. **Numeric fields equal to `0`, boolean fields equal to `false`, and reference fields equal to `null` are omitted from the JSON output.** Consumers should treat absent fields as their default value (`0`/`false`/`null`), not as "unknown". Every non-nullable field documented below is guaranteed to be present *in spirit* - the serializer may simply elide it. (Empty non-null collections are still written, e.g. `[]`.)

### Discovery resources are not report data

The packed-index port changes how repositories are found; report schema remains **2.1** and
Git metadata ownership and the fields below are unchanged. Git repository inspection still
supplies status, branches, remotes, and commits.

`DiscoveryIndex` is non-serialized, borrowed `FileIndex` query access while the report owns its
`MftIndexSession`. Neither `DiscoveryIndex` nor that resource owner is ever serialized. The
single owner can transfer from discovery result to report to view model to
`IndexVolumeChangeSource`, which disposes it when finished. Detaching clears the previous
holder; a holder that does not transfer the owner disposes it.

Index no-cache mode does not disable the existing `~/.GitWizard/repositories.txt` or cached
`~/.GitWizard/report.json`. Saving a report persists Git report data, never the discovery index
or its broker resources.

## Concurrency

The report file has **no lockfile**. Writes are atomic at the file level - the CLI's
`--merge` flag (and the underlying `GitWizardReport.SaveAtomic`) writes to a temp file in
the same directory and renames it over the destination, so a concurrent reader always sees
either the complete old file or the complete new file, never a half-written one.

There is **no protection against concurrent writers**, however. If two callers refresh
**disjoint** sets of repos at the same time (e.g. projdash issuing a targeted `--merge` for
a freshly-cloned repo while a full rescan is also in flight), each one reads the report,
applies its own changes to that snapshot, and writes the whole file back. **Last writer
wins for the file as a whole** - the later write replaces the file produced by the earlier
write, so the earlier writer's entries can be lost even though the two callers touched
different repos. This is an accepted trade-off: the merge use case (projdash filling in a
cache miss) is low-frequency and self-correcting on the next full scan, and a lockfile would
add cross-process coordination complexity for little benefit. Callers that need a guaranteed
merge of disjoint updates must serialize their writes externally.

## Top-level fields

| Field | Type | Description |
|-------|------|-------------|
| `SchemaVersion` | `string` | Schema version (currently `"2.1"`) |
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
| `NumberOfPendingChanges` | `int` | no | Count of modified + staged + removed + added + untracked + renamed files, including deleted tracked files whether or not the deletion is staged. Always matches `HasPendingChanges` (0 iff false). |
| `IsWorktree` | `bool` | no | Whether this is a git worktree |
| `LocalOnlyCommits` | `bool` | no | Whether any local branch has unpushed commits |
| `LocalCommitCount` | `int` | no | Total unpushed commits across all local branches (sums `AheadOfDefault`-style divergence for tracked branches and all commits on untracked branches). |
| `LastCommitDate` | `string` (ISO 8601) | yes | Timestamp of most recent commit on HEAD |
| `DaysSinceLastCommit` | `int` | yes | Days since last commit (computed at refresh time) |
| `DefaultBranch` | `string` | yes | The repo's default branch name (e.g. `main`); the divergence baseline for `Branches` |
| `MatchingBranchName` | `string` | yes | When HEAD is detached, a local branch whose tip matches HEAD (offer to check out); else null |
| `Branches` | `BranchInfo[]` | yes | Local branches and their divergence from `DefaultBranch`. Filtered to "actionable" branches unless `BranchScope` is `"all"`. Null when none qualify. |
| `BehindRemoteCount` | `int` | no | Commits on the checked-out branch's **upstream remote-tracking branch** not reachable from the checked-out branch - how far behind the remote this checkout is. 0 when there is no upstream or HEAD is detached. See disambiguation note below. |
| `AheadOfRemoteCount` | `int` | no | Commits on the checked-out branch not reachable from its upstream - unpushed commits on the branch actually checked out (narrower than `LocalOnlyCommits`, which sums across *all* local branches). 0 when there is no upstream or HEAD is detached. |
| `LastFetchTime` | `string` (ISO 8601) | yes | When GitWizard itself last completed a `fetchRemotes` refresh for this repository. Null if GitWizard has never fetched it - in that case `BehindRemoteCount`/`AheadOfRemoteCount` reflect whatever remote-tracking state happens to be cached locally (possibly from a manual `git fetch`, possibly stale) rather than a comparison GitWizard can vouch for. |
| `IsPublishReady` | `bool` | no | `true` when the checkout is clean (`!HasPendingChanges`), not behind its remote (`BehindRemoteCount == 0`), and currently on `DefaultBranch` - the literal "safe to publish from" check. Derived from the fields above at read time (not a separately refreshed field), so it can go stale the same way `BehindRemoteCount` can; check `LastFetchTime` for freshness. |
| `SizeOnDisk` | `long` (bytes) | no | Repository size on disk in bytes (0/absent if not computed) |
| `RefreshTimeSeconds` | `float` | no | How long the last refresh took |
| `RefreshError` | `string` | yes | Error message if refresh failed |
| `RemoteUrls` | `string[]` | no | List of remote URLs |
| `AuthorEmails` | `string[]` | yes | Unique author emails from last 200 commits |
| `RecentCommits` | `CommitInfo[]` | yes | Last 10 commits on HEAD |
| `Submodules` | `object` | yes | Map of path → repository object (null if uninitialized) |
| `Worktrees` | `object` | yes | Map of path → repository object |

### `BehindDefault`/`AheadOfDefault` vs `BehindRemoteCount`/`AheadOfRemoteCount`

These two field pairs compare against different baselines and answer different questions - the
naming is easy to conflate, hence this callout:

- `BranchInfo.BehindDefault`/`AheadOfDefault` compare a **local** branch against the repository's
  **local default branch** (`main`/`master`/`develop`, whichever `ResolveDefaultBranch` picks).
  They feed branch cleanup ("is this branch safe to delete", "is it stale relative to where the
  team's mainline moved locally") and say nothing about any remote.
- `GitWizardRepository.BehindRemoteCount`/`AheadOfRemoteCount` compare the **checked-out branch**
  against **its own upstream remote-tracking branch** (`origin/<branch>` via
  `Branch.TrackedBranch`). This is the freshness/publish-safety signal: a checkout can be fully
  merged into the local default branch (`BehindDefault == 0`) while still being behind `origin`
  (`BehindRemoteCount > 0`) if the local default branch itself hasn't been fetched recently - see
  `LastFetchTime` above.

## BranchInfo object

A local branch's relationship to the repository's default branch. By default the `Branches` list is filtered to "actionable" branches (see `BranchScope`) - it is not a complete inventory unless the report was produced with `--all-branches`.

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Branch short name (e.g. `feature/x`) |
| `IsMerged` | `bool` | True when fully merged into the default branch (`AheadOfDefault == 0`) - safe to delete |
| `MergedInto` | `string` | Default branch name when merged (safe to delete), else null |
| `AheadOfDefault` | `int` | Commits on this branch not reachable from the default branch |
| `BehindDefault` | `int` | Commits in the default branch not reachable from this branch |
| `LastCommitDate` | `string` (ISO 8601) | Author timestamp of the branch tip; null if the branch has no commits |
| `HasUpstream` | `bool` | True when the branch tracks a remote (false ⇒ unpushed local work). A branch whose `branch.<name>.remote` is a URL or names a remote that is not configured reports false (git-wizard#185), matching git, which does not treat a URL-valued remote as tracking anything. |

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
| `BehindRemote` | `int` | Repos whose checked-out branch is behind its upstream remote (`BehindRemoteCount > 0`) |
| `NeedingAttention` | `AttentionItem[]` | Repos that are dirty, have unpushed commits, have merged branches, or are behind their remote |

## AttentionItem object

| Field | Type | Description |
|-------|------|-------------|
| `Path` | `string` | Repository path |
| `Reasons` | `string[]` | Why it needs attention (`"dirty"`, `"unpushed"`, `"merged-branches"`, `"behind-remote"`) |

## Exit codes

| Code | Meaning |
|------|---------|
| `0` | All repos clean, pushed, and not behind their remote |
| `1` | At least one repo has pending changes, unpushed commits, or is behind its remote (`BehindRemoteCount > 0`) |

## Repository sweep output (`--sweep`)

The automation-focused sweep is a separate contract from the cached report above. Its schema version starts at `1.0`, and its arrays plus `StashCount` are always serialized even when empty or zero.

```json
{
  "SchemaVersion": "1.0",
  "Repositories": [
    {
      "Path": "/work/example",
      "DirtyTrackedFiles": ["README.md"],
      "UnpushedBranches": [
        { "Name": "feature/local", "CommitCount": 2 }
      ],
      "StashCount": 1
    }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `SchemaVersion` | `string` | Sweep contract version, currently `"1.0"` |
| `Repositories` | `RepositorySweepItem[]` | Path-sorted repository results |
| `Path` | `string` | Repository path supplied by explicit paths, cache, or discovery |
| `DirtyTrackedFiles` | `string[]` | Path-sorted staged or unstaged tracked changes; untracked files are excluded |
| `UnpushedBranches` | `UnpushedBranchInfo[]` | Local branches reaching commits absent from every local remote-tracking ref |
| `Name` | `string` | Local branch name |
| `CommitCount` | `int` | Commits reachable from this branch and no remote-tracking ref |
| `StashCount` | `int` | Entries under `refs/stash` |
| `Error` | `string` | Optional repository-local scan error; other repositories are still reported |

`--sweep` does not contact remotes. Run an explicitly approved fetch first when the audit requires current server state. A completed sweep exits `0` even when it finds work; findings are data. Parser or process failures retain the CLI's non-zero error behavior.

## Tag inventory output (`tags`)

`git-wizard tags` inspects every clone (including submodule checkouts and linked worktrees) for tags matching a pattern, and queries remotes via `git ls-remote --tags` to verify whether the tag still exists upstream.

```json
{
  "SchemaVersion": "1.0",
  "MatchPattern": "archive/*",
  "Repositories": [
    {
      "Path": "/work/repo-a",
      "Tags": [
        {
          "Name": "archive/2026-01",
          "CommitSha": "d670460b4b4aece5915caf5c68d12f560a9fe3e4",
          "ExistsOnRemote": true,
          "Remotes": ["origin"]
        }
      ]
    }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `SchemaVersion` | `string` | Contract version, currently `"1.0"` |
| `MatchPattern` | `string` | Glob pattern matched against tag names (e.g. `archive/*`, default `*`) |
| `Repositories` | `RepositoryTagItem[]` | Repositories holding at least one matching tag |
| `Path` | `string` | Absolute path to repository |
| `Tags` | `TagInfo[]` | Matching tags in this repository |
| `Name` | `string` | Tag name |
| `CommitSha` | `string` | Target commit SHA |
| `ExistsOnRemote` | `bool` | Whether the tag exists on any configured remote |
| `Remotes` | `string[]` | Remote names that still hold this tag |
| `Error` | `string` | Optional repository error if repository could not be inspected |

## Branch inventory output (`branches`)

`git-wizard branches` inspects every clone for local branches, comparing against the specified base branch (`--not-merged <base>` / `--not <base>`) or each repository's default branch. It calculates upstream tracking divergence and a `UniqueCommitCount` that proves zero data loss before branch deletion.

Upstream tracking is resolved only when the branch's configured remote exactly
matches a configured remote name. URL-valued remotes, missing remote names, and
the local-upstream value `.` are reported as untracked rather than failing the
repository inventory. In those cases `HasUpstream` is false, `UpstreamBranch` is
null, and both upstream divergence counts are zero. Base-branch divergence and
`UniqueCommitCount` are still calculated normally. When a configured remote's
tracking branch reference is absent locally, upstream divergence counts are zero.

```json
{
  "SchemaVersion": "1.0",
  "BaseBranch": "main",
  "Repositories": [
    {
      "Path": "/work/repo-a",
      "DefaultBranch": "main",
      "Branches": [
        {
          "Name": "feature/unpushed",
          "IsMerged": false,
          "AheadOfBase": 2,
          "BehindBase": 0,
          "HasUpstream": false,
          "AheadOfUpstream": 0,
          "BehindUpstream": 0,
          "UniqueCommitCount": 2,
          "LastCommitDate": "2026-08-24T00:00:00+00:00"
        }
      ]
    }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `SchemaVersion` | `string` | Contract version, currently `"1.0"` |
| `BaseBranch` | `string` | Base branch specified on CLI (null if using default branch per repository) |
| `Repositories` | `RepositoryBranchItem[]` | Repositories holding local branches |
| `Path` | `string` | Absolute path to repository |
| `DefaultBranch` | `string` | The repository's default branch name |
| `Branches` | `BranchInventoryInfo[]` | Local branches in this repository |
| `Name` | `string` | Local branch name |
| `IsMerged` | `bool` | Whether the branch is merged into the base branch (`AheadOfBase == 0`) |
| `AheadOfBase` | `int` | Commits on this branch not reachable from the base branch |
| `BehindBase` | `int` | Commits in the base branch not reachable from this branch |
| `HasUpstream` | `bool` | Whether this branch tracks an upstream remote branch |
| `UpstreamBranch` | `string` | Name of upstream tracking branch |
| `AheadOfUpstream` | `int` | Commits ahead of upstream branch |
| `BehindUpstream` | `int` | Commits behind upstream branch |
| `UniqueCommitCount` | `int` | Count of commits reachable only from this branch and not on base branch or any remote branch. When 0, the branch is safe to delete with zero data loss. |
| `LastCommitDate` | `string` (ISO 8601) | Timestamp of the branch tip commit |
| `Error` | `string` | Optional repository error if repository could not be inspected |
