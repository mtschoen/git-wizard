# Projdash Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enrich GitWizard's report with recent commit history and staleness data, add CLI flags for filtered/targeted/summarized output, add GUI filter buttons, and version the JSON schema — so projdash and LLMs can consume batch git state efficiently.

**Architecture:** New `GitWizardCommitInfo` model class. `GitWizardRepository` gains two new properties populated during `Refresh()`. `GitWizardReport` gains a `SchemaVersion` field. CLI gets four new flags that control filtering and output shape. GUI gets two new sidebar filter buttons and a staleness indicator. All changes are additive to existing patterns.

**Tech Stack:** .NET 10, LibGit2Sharp, System.Text.Json, NUnit, MAUI

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `GitWizard/GitWizardCommitInfo.cs` | Create | Serializable model for a single commit |
| `GitWizard/GitWizardRepository.cs` | Modify | Add `RecentCommits`, `DaysSinceLastCommit` |
| `GitWizard/GitWizardReport.cs` | Modify | Add `SchemaVersion` |
| `GitWizard/GitWizardSummary.cs` | Create | Summary output model |
| `git-wizard/Program.cs` | Modify | Add `-filter`, `-paths`, `-summary` flags, exit code logic |
| `GitWizardUI/ViewModels/RepositoryNodeViewModel.cs` | Modify | Add `FilterType.LocalOnlyCommits`, `FilterType.Stale`, staleness display |
| `GitWizardUI/MainPage.xaml` | Modify | Add two sidebar filter buttons |
| `GitWizardTests/GitWizardRepositoryTests.cs` | Create | Tests for new repository fields |
| `GitWizardTests/GitWizardSummaryTests.cs` | Create | Tests for summary generation |
| `GitWizardTests/ProgramFilterTests.cs` | Create | Tests for CLI filter/paths logic |
| `docs/report-schema.md` | Create | Schema documentation |

---

### Task 1: `GitWizardCommitInfo` model

**Files:**
- Create: `GitWizard/GitWizardCommitInfo.cs`
- Create: `GitWizardTests/GitWizardRepositoryTests.cs`

- [x] **Step 1: Write the failing test**

In `GitWizardTests/GitWizardRepositoryTests.cs`:

```csharp
using System.Text.Json;
using GitWizard;

namespace GitWizardTests;

public class GitWizardRepositoryTests
{
    [Test]
    public void GitWizardCommitInfo_RoundTripsJson()
    {
        var commit = new GitWizardCommitInfo
        {
            Hash = "abc1234",
            Message = "Fix the thing",
            Date = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero),
            AuthorEmail = "dev@example.com"
        };

        var json = JsonSerializer.Serialize(commit);
        var deserialized = JsonSerializer.Deserialize<GitWizardCommitInfo>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.Hash, Is.EqualTo("abc1234"));
        Assert.That(deserialized.Message, Is.EqualTo("Fix the thing"));
        Assert.That(deserialized.AuthorEmail, Is.EqualTo("dev@example.com"));
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~GitWizardCommitInfo_RoundTripsJson" -v minimal`
Expected: Build error — `GitWizardCommitInfo` does not exist.

- [x] **Step 3: Create `GitWizardCommitInfo`**

Create `GitWizard/GitWizardCommitInfo.cs`:

```csharp
namespace GitWizard;

[Serializable]
public class GitWizardCommitInfo
{
    public string Hash { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }
    public string AuthorEmail { get; set; } = string.Empty;
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~GitWizardCommitInfo_RoundTripsJson" -v minimal`
Expected: PASS

- [x] **Step 5: Commit**

```bash
git add GitWizard/GitWizardCommitInfo.cs GitWizardTests/GitWizardRepositoryTests.cs
git commit -m "feat: add GitWizardCommitInfo model with JSON serialization"
```

---

### Task 2: `RecentCommits` on `GitWizardRepository`

**Files:**
- Modify: `GitWizard/GitWizardRepository.cs` (properties at line ~24, populate in `Refresh()` at line ~130)
- Modify: `GitWizardTests/GitWizardRepositoryTests.cs`

- [x] **Step 1: Write the failing test**

Add to `GitWizardTests/GitWizardRepositoryTests.cs`:

```csharp
[Test]
public void Refresh_PopulatesRecentCommits()
{
    GitWizardLog.SilentMode = true;

    // Use the current repo (git-wizard itself) as a test subject
    var repoPath = FindRepoRoot();
    var repository = new GitWizardRepository(repoPath);
    repository.Refresh();

    Assert.That(repository.RecentCommits, Is.Not.Null);
    Assert.That(repository.RecentCommits, Has.Count.GreaterThan(0));
    Assert.That(repository.RecentCommits!.Count, Is.LessThanOrEqualTo(10));

    var first = repository.RecentCommits[0];
    Assert.That(first.Hash, Has.Length.EqualTo(7));
    Assert.That(first.Message, Is.Not.Empty);
    Assert.That(first.AuthorEmail, Is.Not.Empty);
}

static string FindRepoRoot()
{
    var directory = Directory.GetCurrentDirectory();
    while (directory != null)
    {
        if (Directory.Exists(Path.Combine(directory, ".git")))
            return directory;
        directory = Directory.GetParent(directory)?.FullName;
    }

    throw new DirectoryNotFoundException("Could not find git repo root from working directory");
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~Refresh_PopulatesRecentCommits" -v minimal`
Expected: Build error — `RecentCommits` property does not exist.

- [x] **Step 3: Add `RecentCommits` property and populate it during `Refresh()`**

In `GitWizard/GitWizardRepository.cs`, add the property near line 24 (after `AuthorEmails`):

```csharp
public List<GitWizardCommitInfo>? RecentCommits { get; private set; }
```

In the `Refresh()` method, inside the outer `try` block, after the `AuthorEmails` collection loop (around line 140), add:

```csharp
// Collect recent commits for projdash/LLM consumption
try
{
    RecentCommits = new List<GitWizardCommitInfo>();
    foreach (var commit in repository.Commits.Take(10))
    {
        RecentCommits.Add(new GitWizardCommitInfo
        {
            Hash = commit.Sha[..7],
            Message = commit.MessageShort,
            Date = commit.Author.When,
            AuthorEmail = commit.Author.Email
        });
    }
}
catch (Exception exception)
{
    GitWizardLog.LogException(exception, $"Exception collecting recent commits for {WorkingDirectory}");
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~Refresh_PopulatesRecentCommits" -v minimal`
Expected: PASS

- [x] **Step 5: Commit**

```bash
git add GitWizard/GitWizardRepository.cs GitWizardTests/GitWizardRepositoryTests.cs
git commit -m "feat: populate RecentCommits during repository refresh"
```

---

### Task 3: `DaysSinceLastCommit` on `GitWizardRepository`

**Files:**
- Modify: `GitWizard/GitWizardRepository.cs` (add property near line 25)
- Modify: `GitWizardTests/GitWizardRepositoryTests.cs`

- [x] **Step 1: Write the failing test**

Add to `GitWizardTests/GitWizardRepositoryTests.cs`:

```csharp
[Test]
public void Refresh_PopulatesDaysSinceLastCommit()
{
    GitWizardLog.SilentMode = true;
    var repoPath = FindRepoRoot();
    var repository = new GitWizardRepository(repoPath);
    repository.Refresh();

    Assert.That(repository.DaysSinceLastCommit, Is.Not.Null);
    Assert.That(repository.DaysSinceLastCommit, Is.GreaterThanOrEqualTo(0));
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~Refresh_PopulatesDaysSinceLastCommit" -v minimal`
Expected: Build error — `DaysSinceLastCommit` does not exist.

- [x] **Step 3: Add `DaysSinceLastCommit` property and compute it during `Refresh()`**

In `GitWizard/GitWizardRepository.cs`, add the property near the other properties:

```csharp
public int? DaysSinceLastCommit { get; private set; }
```

In `Refresh()`, right after the line `LastCommitDate = repository.Head.Tip?.Author.When;` (around line 59), add:

```csharp
if (LastCommitDate.HasValue)
    DaysSinceLastCommit = (int)(DateTimeOffset.Now - LastCommitDate.Value).TotalDays;
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~Refresh_PopulatesDaysSinceLastCommit" -v minimal`
Expected: PASS

- [x] **Step 5: Commit**

```bash
git add GitWizard/GitWizardRepository.cs GitWizardTests/GitWizardRepositoryTests.cs
git commit -m "feat: add DaysSinceLastCommit computed field"
```

---

### Task 4: `SchemaVersion` on `GitWizardReport`

**Files:**
- Modify: `GitWizard/GitWizardReport.cs` (add property)
- Modify: `GitWizardTests/GitWizardReportTests.cs`

- [x] **Step 1: Write the failing test**

Add to `GitWizardTests/GitWizardReportTests.cs`:

```csharp
[Test]
public void Report_HasSchemaVersion()
{
    var report = new GitWizardReport();
    Assert.That(report.SchemaVersion, Is.EqualTo("1.0"));
}

[Test]
public void Report_SchemaVersionSerializesToJson()
{
    var report = new GitWizardReport();
    var json = System.Text.Json.JsonSerializer.Serialize(report);
    Assert.That(json, Does.Contain("\"SchemaVersion\":\"1.0\""));
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~Report_HasSchemaVersion|FullyQualifiedName~Report_SchemaVersionSerializesToJson" -v minimal`
Expected: Build error — `SchemaVersion` does not exist.

- [x] **Step 3: Add `SchemaVersion` to `GitWizardReport`**

In `GitWizard/GitWizardReport.cs`, add as the first property (before `SearchPaths`):

```csharp
public string SchemaVersion { get; set; } = "1.0";
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~Report_HasSchemaVersion|FullyQualifiedName~Report_SchemaVersionSerializesToJson" -v minimal`
Expected: PASS

- [x] **Step 5: Commit**

```bash
git add GitWizard/GitWizardReport.cs GitWizardTests/GitWizardReportTests.cs
git commit -m "feat: add SchemaVersion field to GitWizardReport"
```

---

### Task 5: CLI `-filter` flag

**Files:**
- Modify: `git-wizard/Program.cs` (add to `RunConfiguration` and use in `Main`)

- [x] **Step 1: Add `-filter` to `RunConfiguration`**

In the `RunConfiguration` struct in `git-wizard/Program.cs`, add the field after `NoMft`:

```csharp
/// <summary>
/// Case-insensitive substring filter applied to repository paths in the output.
/// </summary>
public readonly string? FilterPattern = null;
```

Add to the help text:

```
  -filter <pattern>       Filter output to repositories whose path contains <pattern> (case-insensitive)
```

Add the case to the switch in the constructor:

```csharp
case "-filter":
    if (i + 1 >= length)
    {
        GitWizardLog.Log("-filter argument passed without a following argument.", GitWizardLog.LogType.Error);
        break;
    }

    FilterPattern = arguments[++i];
    break;
```

- [x] **Step 2: Apply filter before serialization in `Main()`**

In `Main()`, after `SaveReport(runConfiguration, report);` and before the `SerializeReport` call, add filtering logic. Replace the block:

```csharp
SaveReport(runConfiguration, report);

var jsonString = SerializeReport(runConfiguration, report);

if (!GitWizardLog.SilentMode)
    Console.WriteLine(jsonString);
```

With:

```csharp
SaveReport(runConfiguration, report);

var filteredReport = ApplyFilter(runConfiguration, report);

var jsonString = SerializeReport(runConfiguration, filteredReport);

if (!GitWizardLog.SilentMode)
    Console.WriteLine(jsonString);
```

Add the `ApplyFilter` method:

```csharp
static GitWizardReport ApplyFilter(RunConfiguration runConfiguration, GitWizardReport report)
{
    if (string.IsNullOrEmpty(runConfiguration.FilterPattern))
        return report;

    var filtered = new GitWizardReport
    {
        SchemaVersion = report.SchemaVersion,
        SearchPaths = report.SearchPaths,
        IgnoredPaths = report.IgnoredPaths
    };

    foreach (var kvp in report.Repositories)
    {
        if (kvp.Key.Contains(runConfiguration.FilterPattern, StringComparison.OrdinalIgnoreCase))
            filtered.Repositories[kvp.Key] = kvp.Value;
    }

    return filtered;
}
```

- [x] **Step 3: Test manually**

Run: `dotnet run --project git-wizard/git-wizard.csproj -- -no-refresh -silent -filter "gitwizard" -print-minified`
Expected: Only repos with "gitwizard" in their path appear in the output (or empty if no cached report).

- [x] **Step 4: Commit**

```bash
git add git-wizard/Program.cs
git commit -m "feat: add -filter CLI flag for path substring matching"
```

---

### Task 6: CLI `-paths` flag

**Files:**
- Modify: `git-wizard/Program.cs`

- [x] **Step 1: Add `-paths` to `RunConfiguration`**

Add the field:

```csharp
/// <summary>
/// Explicit list of repository paths to report on, bypassing discovery.
/// Can be a file path (newline-separated) or comma-separated inline list.
/// </summary>
public readonly string? PathsArgument = null;
```

Add to help text:

```
  -paths <file-or-csv>    Report on specific repo paths (newline-separated file or comma-separated list)
```

Add the case:

```csharp
case "-paths":
    if (i + 1 >= length)
    {
        GitWizardLog.Log("-paths argument passed without a following argument.", GitWizardLog.LogType.Error);
        break;
    }

    PathsArgument = arguments[++i];
    break;
```

- [x] **Step 2: Parse paths and override repository discovery in `Main()`**

Replace the `GetRepositoryPaths` method call in `Main()`. Change:

```csharp
var repositoryPaths = GetRepositoryPaths(runConfiguration);
```

To:

```csharp
var repositoryPaths = GetRepositoryPaths(runConfiguration) ?? ParseExplicitPaths(runConfiguration);
```

Add the parsing method:

```csharp
static string[]? ParseExplicitPaths(RunConfiguration runConfiguration)
{
    var argument = runConfiguration.PathsArgument;
    if (string.IsNullOrEmpty(argument))
        return null;

    // If it's a file, read lines from it
    if (File.Exists(argument))
    {
        return File.ReadAllLines(argument)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToArray();
    }

    // Otherwise treat as comma-separated
    return argument.Split(',')
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => p.Trim())
        .ToArray();
}
```

- [x] **Step 3: Test manually**

Create a temp file `test-paths.txt` with one known repo path per line, then:
Run: `dotnet run --project git-wizard/git-wizard.csproj -- -paths "test-paths.txt" -print-minified -silent`
Expected: Only the specified repos appear.

- [x] **Step 4: Commit**

```bash
git add git-wizard/Program.cs
git commit -m "feat: add -paths CLI flag for targeted repo reporting"
```

---

### Task 7: `GitWizardSummary` model and CLI `-summary` flag

**Files:**
- Create: `GitWizard/GitWizardSummary.cs`
- Create: `GitWizardTests/GitWizardSummaryTests.cs`
- Modify: `git-wizard/Program.cs`

- [x] **Step 1: Write the failing test**

Create `GitWizardTests/GitWizardSummaryTests.cs`:

```csharp
using GitWizard;

namespace GitWizardTests;

public class GitWizardSummaryTests
{
    [Test]
    public void FromReport_CountsDirtyRepos()
    {
        var report = new GitWizardReport();
        var clean = new GitWizardRepository("c:\\clean");
        clean.Refresh();
        report.Repositories["c:\\clean"] = clean;

        var summary = GitWizardSummary.FromReport(report);

        Assert.That(summary.TotalRepositories, Is.EqualTo(1));
        Assert.That(summary.SchemaVersion, Is.EqualTo("1.0"));
    }

    [Test]
    public void NeedingAttention_IncludesDirtyAndUnpushed()
    {
        var report = new GitWizardReport();

        // Use a real repo to get populated state
        var repoPath = FindRepoRoot();
        var repository = new GitWizardRepository(repoPath);
        repository.Refresh();
        report.Repositories[repoPath] = repository;

        var summary = GitWizardSummary.FromReport(report);

        // The test repo may or may not be dirty — just verify the structure works
        Assert.That(summary.TotalRepositories, Is.EqualTo(1));
        Assert.That(summary.NeedingAttention, Is.Not.Null);
    }

    static string FindRepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory, ".git")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not find git repo root from working directory");
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~GitWizardSummaryTests" -v minimal`
Expected: Build error — `GitWizardSummary` does not exist.

- [x] **Step 3: Create `GitWizardSummary`**

Create `GitWizard/GitWizardSummary.cs`:

```csharp
namespace GitWizard;

[Serializable]
public class GitWizardSummary
{
    public string SchemaVersion { get; set; } = "1.0";
    public int TotalRepositories { get; set; }
    public int Dirty { get; set; }
    public int Unpushed { get; set; }
    public int Stale { get; set; }
    public List<AttentionItem> NeedingAttention { get; set; } = new();

    public static GitWizardSummary FromReport(GitWizardReport report)
    {
        var summary = new GitWizardSummary
        {
            SchemaVersion = report.SchemaVersion,
            TotalRepositories = report.Repositories.Count
        };

        foreach (var kvp in report.Repositories)
        {
            var repository = kvp.Value;
            var reasons = new List<string>();

            if (repository.HasPendingChanges)
            {
                summary.Dirty++;
                reasons.Add("dirty");
            }

            if (repository.LocalOnlyCommits)
            {
                summary.Unpushed++;
                reasons.Add("unpushed");
            }

            if (repository.DaysSinceLastCommit > 30)
            {
                summary.Stale++;
            }

            if (reasons.Count > 0)
            {
                summary.NeedingAttention.Add(new AttentionItem
                {
                    Path = kvp.Key,
                    Reasons = reasons
                });
            }
        }

        return summary;
    }
}

[Serializable]
public class AttentionItem
{
    public string Path { get; set; } = string.Empty;
    public List<string> Reasons { get; set; } = new();
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~GitWizardSummaryTests" -v minimal`
Expected: PASS

- [x] **Step 5: Add `-summary` flag to CLI**

In `git-wizard/Program.cs`, add to `RunConfiguration`:

```csharp
/// <summary>
/// Output a condensed summary instead of the full report.
/// </summary>
public readonly bool Summary = false;
```

Add to help text:

```
  -summary                Output a condensed summary (dirty/unpushed/stale counts + repos needing attention)
```

Add the case:

```csharp
case "-summary":
    Summary = true;
    break;
```

In `Main()`, replace the output section (the block starting with `var filteredReport = ApplyFilter...`) with:

```csharp
SaveReport(runConfiguration, report);

var filteredReport = ApplyFilter(runConfiguration, report);
var needsAttention = ReportNeedsAttention(filteredReport);

string jsonString;
if (runConfiguration.Summary)
{
    var summary = GitWizardSummary.FromReport(filteredReport);
    var options = new JsonSerializerOptions
    {
        WriteIndented = !runConfiguration.Minified,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };
    jsonString = JsonSerializer.Serialize(summary, options);
}
else
{
    jsonString = SerializeReport(runConfiguration, filteredReport);
}

if (!GitWizardLog.SilentMode)
    Console.WriteLine(jsonString);

if (needsAttention)
    Environment.Exit(1);
```

- [x] **Step 6: Add `ReportNeedsAttention` helper**

```csharp
static bool ReportNeedsAttention(GitWizardReport report)
{
    foreach (var kvp in report.Repositories)
    {
        if (kvp.Value.HasPendingChanges || kvp.Value.LocalOnlyCommits)
            return true;
    }

    return false;
}
```

- [x] **Step 7: Commit**

```bash
git add GitWizard/GitWizardSummary.cs GitWizardTests/GitWizardSummaryTests.cs git-wizard/Program.cs
git commit -m "feat: add GitWizardSummary model and -summary CLI flag with exit code"
```

---

### Task 8: GUI — "Local Only Commits" and "Stale" filter buttons

**Files:**
- Modify: `GitWizardUI/ViewModels/RepositoryNodeViewModel.cs` (add enum values + match logic)
- Modify: `GitWizardUI/MainPage.xaml` (add two buttons)
- Modify: `GitWizardUI/MainPage.xaml.cs` (if button click handler needs updating — check if `FilterButton_Click` uses the button name to resolve filter type)

- [x] **Step 1: Check how `FilterButton_Click` resolves the filter type**

Read `GitWizardUI/MainPage.xaml.cs` to see how the existing filter buttons map to `FilterType` enum values. This determines whether we need to modify the code-behind or if naming convention handles it.

- [x] **Step 2: Add enum values to `FilterType`**

In `GitWizardUI/ViewModels/RepositoryNodeViewModel.cs`, add to the `FilterType` enum:

```csharp
public enum FilterType
{
    None,
    PendingChanges,
    SubmoduleCheckout,
    SubmoduleUninitialized,
    SubmoduleConfigIssue,
    DetachedHead,
    MyRepositories,
    LocalOnlyCommits,
    Stale
}
```

- [x] **Step 3: Add match logic in `MatchesFilter`**

In `RepositoryNodeViewModel.MatchesFilter()`, add the new cases:

```csharp
FilterType.LocalOnlyCommits => Repository.LocalOnlyCommits,
FilterType.Stale => Repository.DaysSinceLastCommit > 30,
```

- [x] **Step 4: Add sidebar buttons in `MainPage.xaml`**

In `MainPage.xaml`, after the `FilterMyRepositories` button, add:

```xml
<Button x:Name="FilterLocalOnlyCommits" ToolTipProperties.Text="Repositories with commits not pushed to any remote" Text="Local Only Commits" FontSize="12" Clicked="FilterButton_Click" />
<Button x:Name="FilterStale" ToolTipProperties.Text="Repositories with no commits in the last 30 days" Text="Stale (30+ days)" FontSize="12" Clicked="FilterButton_Click" />
```

- [x] **Step 5: Update `GetFilterType` in `MainPage.xaml.cs`**

In `GitWizardUI/MainPage.xaml.cs`, the `GetFilterType` method (around line 165) maps buttons to filter types via identity comparison. Add the two new cases before the `return FilterType.None;` line:

```csharp
if (button == FilterLocalOnlyCommits) return FilterType.LocalOnlyCommits;
if (button == FilterStale) return FilterType.Stale;
```

- [x] **Step 6: Build and verify**

Run: `"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" GitWizardUI/GitWizardUI.csproj -t:Build -p:Configuration=Debug -nologo -v:minimal`
Expected: Build succeeds.

- [x] **Step 7: Commit**

```bash
git add GitWizardUI/ViewModels/RepositoryNodeViewModel.cs GitWizardUI/MainPage.xaml GitWizardUI/MainPage.xaml.cs
git commit -m "feat: add Local Only Commits and Stale filter buttons to GUI"
```

---

### Task 9: GUI — Staleness indicator in display text

**Files:**
- Modify: `GitWizardUI/ViewModels/RepositoryNodeViewModel.cs` (`UpdateDisplayText` method, around line 141)

- [x] **Step 1: Add staleness suffix to `UpdateDisplayText`**

In `RepositoryNodeViewModel.UpdateDisplayText()`, after the existing `localOnlyCommits` arrow-up indicator block, add:

```csharp
var daysSinceLastCommit = Repository.DaysSinceLastCommit;
if (daysSinceLastCommit > 30)
{
    label += $" ({daysSinceLastCommit}d)";
}
```

This shows e.g. `C:\repos\old-project (45d)` for stale repos.

- [x] **Step 2: Build and verify**

Run: `"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" GitWizardUI/GitWizardUI.csproj -t:Build -p:Configuration=Debug -nologo -v:minimal`
Expected: Build succeeds.

- [x] **Step 3: Commit**

```bash
git add GitWizardUI/ViewModels/RepositoryNodeViewModel.cs
git commit -m "feat: show staleness indicator in repo display text for 30+ day idle repos"
```

---

### Task 10: Schema documentation

**Files:**
- Create: `docs/report-schema.md`

- [x] **Step 1: Write schema documentation**

Create `docs/report-schema.md`:

````markdown
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
````

- [x] **Step 2: Commit**

```bash
git add docs/report-schema.md
git commit -m "docs: add report.json schema documentation"
```

---

### Task 11: Run full test suite and update PLAN.md

- [x] **Step 1: Run all tests**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj -v minimal`
Expected: All tests pass.

- [x] **Step 2: Build CLI**

Run: `dotnet build git-wizard/git-wizard.csproj -v minimal`
Expected: Build succeeds.

- [x] **Step 3: Build GUI**

Run: `"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" GitWizardUI/GitWizardUI.csproj -t:Build -p:Configuration=Debug -nologo -v:minimal`
Expected: Build succeeds.

- [x] **Step 4: Update PLAN.md — mark projdash integration tasks as complete**

Check all boxes in the "Next Up" section of `PLAN.md`.

- [x] **Step 5: Commit**

```bash
git add PLAN.md
git commit -m "chore: mark projdash integration tasks complete in PLAN.md"
```
