# Projdash Integration — Batch Git Reports for LLM Consumption

GitWizard scans ~700 repos and caches state in `report.json`. Projdash currently shells out to `git` per-repo (slow, sequential). This work makes GitWizard produce structured batch reports that projdash and LLMs via MCP can consume efficiently, and surfaces new data in the GUI.

## 1. Core Library Changes

### 1.1 `GitWizardCommitInfo` (new class)

New serializable class in `GitWizard/` namespace:

```csharp
public class GitWizardCommitInfo
{
    public string Hash { get; set; }       // 7-char short hash
    public string Message { get; set; }    // first line only
    public DateTimeOffset Date { get; set; }
    public string AuthorEmail { get; set; }
}
```

### 1.2 `GitWizardRepository` additions

- `List<GitWizardCommitInfo>? RecentCommits` — last 10 commits, populated during `Refresh()` from `repository.Commits.Take(10)`. Serialized into report JSON.
- `int? DaysSinceLastCommit` — computed from `LastCommitDate` at refresh time and stored (not a live getter), so cached reports have a meaningful snapshot value.

### 1.3 `GitWizardReport` additions

- `string SchemaVersion` — set to `"1.0"` on construction. Serialized at the top of the JSON output. Consumers can check this to detect breaking changes.

## 2. CLI Changes (`git-wizard/Program.cs`)

### 2.1 `--filter` flag

`-filter <pattern>` filters report output to repos whose `WorkingDirectory` contains the pattern (case-insensitive string match). Applied after refresh, before serialization — does not change what's cached, only what's printed/saved.

### 2.2 `--paths` flag

`-paths <file-or-csv>` accepts either:
- A path to a newline-separated file listing repo paths
- A comma-separated list of repo paths inline

These become the `repositoryPaths` input, bypassing discovery. If a path isn't in the cached repo list it's still refreshed as long as it's a valid git repo.

### 2.3 `--summary` flag

`-summary` outputs a condensed summary instead of the full report:

```json
{
    "SchemaVersion": "1.0",
    "TotalRepositories": 692,
    "Dirty": 12,
    "Unpushed": 5,
    "Stale": 3,
    "NeedingAttention": [
        { "Path": "C:\\...\\projdash", "Reasons": ["dirty", "unpushed"] }
    ]
}
```

"Stale" = `DaysSinceLastCommit > 30`. Replaces normal JSON output when set.

### 2.4 Exit code for attention-needed

Return exit code 1 when any repo in the (filtered) output has `HasPendingChanges || LocalOnlyCommits`. Applies to both `--summary` and default output modes. `--scan-only` is unaffected.

## 3. GUI Changes (`GitWizardUI/`)

### 3.1 Staleness indicator in display text

`RepositoryNodeViewModel.UpdateDisplayText()` appends a staleness indicator for repos where `DaysSinceLastCommit > 30`, e.g. the existing display text plus a subtle age suffix. The existing `* (N)` for pending and `arrow-up` for unpushed patterns are extended, not replaced.

### 3.2 "Local Only Commits" filter button

New sidebar filter button. The data already exists on `GitWizardRepository.LocalOnlyCommits` — this just adds the UI entry point. New `FilterType.LocalOnlyCommits` enum value, matched in `RepositoryNodeViewModel.MatchesFilter()`.

### 3.3 "Stale" filter button

New sidebar filter button. Filters to repos where `DaysSinceLastCommit > 30`. New `FilterType.Stale` enum value.

## 4. Schema Documentation

New file `docs/report-schema.md` documenting all fields in `report.json`: types, nullability, and semantics. References `SchemaVersion` and notes that breaking changes will increment it.

## 5. Out of Scope

- Projdash consumer-side changes (tracked in projdash's own plan)
- Configurable staleness threshold (hardcoded to 30 days; can be parameterized later)
- Configurable recent commit count (hardcoded to 10)
- CLI argument library migration (noted as existing TODO)
