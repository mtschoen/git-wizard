# GitWizard Report JSON Schema

**Current version:** `1.0`

The `SchemaVersion` field at the top of every report indicates the schema version. Breaking changes will increment this value.

## Top-level fields

| Field | Type | Description |
|-------|------|-------------|
| `SchemaVersion` | `string` | Schema version (e.g. `"1.0"`) |
| `SearchPaths` | `string[]` | Configured search paths |
| `IgnoredPaths` | `string[]` | Configured ignored paths |
| `Repositories` | `object` | Map of repo path → repository object |

## Repository object

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `WorkingDirectory` | `string` | yes | Absolute path to repo working directory |
| `CurrentBranch` | `string` | yes | Current branch name |
| `IsDetachedHead` | `bool` | no | Whether HEAD is detached |
| `HasPendingChanges` | `bool` | no | Whether repo has uncommitted changes |
| `NumberOfPendingChanges` | `int` | no | Count of modified + staged + removed files |
| `IsWorktree` | `bool` | no | Whether this is a git worktree |
| `LocalOnlyCommits` | `bool` | no | Whether any local branch has unpushed commits |
| `LastCommitDate` | `string` (ISO 8601) | yes | Timestamp of most recent commit on HEAD |
| `DaysSinceLastCommit` | `int` | yes | Days since last commit (computed at refresh time) |
| `RefreshTimeSeconds` | `float` | no | How long the last refresh took |
| `RefreshError` | `string` | yes | Error message if refresh failed |
| `RemoteUrls` | `string[]` | no | List of remote URLs |
| `AuthorEmails` | `string[]` | yes | Unique author emails from last 200 commits |
| `RecentCommits` | `CommitInfo[]` | yes | Last 10 commits on HEAD |
| `Submodules` | `object` | yes | Map of path → repository object (null if uninitialized) |
| `Worktrees` | `object` | yes | Map of path → repository object |

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
| `NeedingAttention` | `AttentionItem[]` | Repos that are dirty or have unpushed commits |

## AttentionItem object

| Field | Type | Description |
|-------|------|-------------|
| `Path` | `string` | Repository path |
| `Reasons` | `string[]` | Why it needs attention (`"dirty"`, `"unpushed"`) |

## Exit codes

| Code | Meaning |
|------|---------|
| `0` | All repos clean and pushed |
| `1` | At least one repo has pending changes or unpushed commits |
