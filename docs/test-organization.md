# GitWizardTests Organization Guide

This document describes the directory layout, naming conventions, and verification procedures for tests in `GitWizardTests/`.

## Layout and Naming Principles

1. **One test class per file**: Each file declaring a test fixture declares exactly one test class.
2. **Matching file name**: The file name matches the test class name exactly: `<FixtureName>.cs`.
3. **Globally unique simple fixture names**: No two test fixtures in the test assembly share a simple class name, even across different namespaces or folders.
4. **No partial test classes**: Test fixtures are not split across `partial` class declarations. When a test family grows large, it is split into independent fixtures by concern.
5. **No nested test fixtures**: Test fixtures are top-level classes. Non-fixture helpers and test doubles may be nested when private to that fixture, or placed in `TestSupport/` when shared.
6. **Production-area folders**: Every test fixture lives in a subdirectory corresponding to its production area, never in the test project root.
7. **Namespace stability**: Moving a file into a subfolder preserves its existing namespace unless a namespace migration is explicitly planned. `GlobalTestSetup` remains in `namespace GitWizardTests` so its assembly-level setup covers all fixtures.

## Production-Area Map

| Directory | Area and Responsibility |
| --- | --- |
| `UI/` | Desktop views, view models, converters, UI services, and `MainViewModel` fixtures grouped by concern. |
| `Cli/` | CLI parsing, argument binding, command dispatch, console output formatting, and progress reporting. |
| `Repository/` | Repository metadata, branch and worktree inventory, upstream tracking, divergence, and sizing. |
| `Repository/Refresh/` | Repository refresh lifecycle, phase observation, and concurrency gating. |
| `Reports/` | Report data contracts, persistence, merging, and summary formatting. |
| `Persistence/` | Local configuration files and cached local file guards. |
| `Discovery/` | Repository discovery on filesystem and index, marker admission, and broker discovery. |
| `Discovery/Index/` | Consumer-owned index sessions, cache maintenance, cache policy, and index status diagnostics. |
| `Infrastructure/` | Shared formatting, logging, platform elevation checks, and temporary directory management. |
| `Watch/` | File and volume change monitoring, event debounce, classification, and Linux fanotify integration. |
| `TestSupport/` | Shared doubles, test builders, stubs, process runners, and assembly isolation fixtures. |

## MainViewModel Fixture Mapping

Fixtures exercising `MainViewModel` and its extracted helpers live together under `GitWizardTests/UI/` and correspond to the following production sources:

| Production Source | Fixture Name(s) | Concern |
| --- | --- | --- |
| `MainViewModel.cs` | `MainViewModelTests`<br>`MainViewModelNoMftTests` | Construction, default property states, injected services, error button labeling, and refresh selection. |
| `PreparedDiscoveryIndexStore.cs` | `MainViewModelPreparedSessionTests` | Prepared discovery index session retention, single-owner replacement disposal, and active cache status capture. |
| `StatusHeaderViewModel.cs` | `StatusHeaderViewModelTests` | Status header text, refresh baseline retention, filter/group suffix formatting, and empty-repositories messaging. |
| `MainViewModel.Commands.cs` | `MainViewModelCommandTests`<br>`MainViewModelClipboardTests` | Command execution, repository selection, clipboard copying. |
| `MainViewModel.Grouping.cs`<br>`RepositoryGroupKeys.cs` | `MainViewModelGroupingTests`<br>`MainViewModelGroupingInteractionTests` | Grouping hierarchies, publication, expansion, filter swapping, search debounce. |
| `RepositorySortOrder.cs` | `MainViewModelWorkingDirectorySortTests` | Platform-aware path sorting, drive grouping ordering, sorted insertion. |
| `MainViewModel.RefreshBatch.cs` | `MainViewModelRefreshBatchTests` | Bulk batch merges, drain deduplication, and batch sort reduction. |
| `MainViewModel.UiDrain.cs` | `MainViewModelUiDrainTests` | Background-to-UI message drain throttling and batching. |
| `MainViewModel.UpdateHandler.cs` | `MainViewModelUpdateHandlerTests`<br>`MainViewModelProgressNotificationTests` | Queued scan callbacks and progress status notifications. |
| `CancellationTokenSourceDisposal.cs` | `CancellationTokenSourceDisposalTests` | Cooperative cancellation callback completion and disposal sequencing. |
| `MainViewModel.IndexCache.cs` | `MainViewModelIndexCacheTests` | Index cache integration, cache hit indicators, and session status. |
| `MainViewModel.Refresh.cs` | `MainViewModelAddRepositoryTests`<br>`MainViewModelUpdateCompletedRepositoryTests`<br>`MainViewModelPrePopulateTests`<br>`MainViewModelCompleteRefreshTests`<br>`MainViewModelRefreshLifecycleTests` | Adding scanned repositories, report pre-population, completion updates, full refresh lifecycle. |
| `MainViewModel.RefreshPrune.cs` | `MainViewModelCachedPathsTests`<br>`MainViewModelCachedNodeReplacementTests`<br>`MainViewModelRefreshPruneTests` | Cached path updates, node replacement, and stale path pruning. |
| `MainViewModel.Live.cs`<br>`MainViewModel.LiveStartup.cs`<br>`MainViewModel.LiveProgress.cs`<br>`MainViewModel.LiveReadiness.cs` | `MainViewModelLiveEventTests`<br>`MainViewModelLiveCapabilityTests`<br>`MainViewModelLiveStartupTests`<br>`MainViewModelLiveProgressTests`<br>`MainViewModelLiveReadinessTests` | Live event dispatch, toggle capabilities, startup arming, progress display, readiness tooltips. |

## Verification Procedures

### Source-Layout Check

The following script validates that all C# source files under `GitWizardTests/` adhere to the naming, folder, single-class, and non-partial rules:

```powershell
$paths = @('GitWizardTests')
@'
from collections import defaultdict
from pathlib import Path
import re
import sys

declaration = re.compile(
    r'^(?:(?:public|internal|file|sealed|static|abstract|partial)\s+)*class\s+(\w+)',
    re.MULTILINE,
)
test_attribute = re.compile(r'^\s*\[(?:Test|TestCase|TestCaseSource|AvaloniaTest)(?:[\s,(\]])', re.MULTILINE)
files = set()
for argument in sys.argv[1:]:
    path = Path(argument)
    if not path.exists():
        raise SystemExit(f'missing requested path: {path}')
    files.update(path.rglob('*.cs') if path.is_dir() else [path])
files = sorted(path for path in files if not {'bin', 'obj'}.intersection(path.parts))
if not files:
    raise SystemExit('no C# files selected')
failures = []
owners = defaultdict(list)
for path in files:
    text = path.read_text(encoding='utf-8-sig')
    classes = declaration.findall(text)
    fixtures = [name for name in classes if name.endswith('Tests')]
    if test_attribute.search(text) or fixtures or path.stem.endswith('Tests'):
        if len(fixtures) != 1 or path.stem != fixtures[0]:
            failures.append(f'{path}: expected one matching fixture, found {fixtures}')
        if path.parent == Path('GitWizardTests'):
            failures.append(f'{path}: fixture belongs in a production-area folder')
        if re.search(r'^.*\bpartial\s+class\b', text, re.MULTILINE):
            failures.append(f'{path}: split by concern instead of partial declarations')
    for name in fixtures:
        owners[name].append(str(path))
    if re.search(r'^[ \t]+.*\bclass\s+\w*Tests\b', text, re.MULTILINE):
        failures.append(f'{path}: nested fixture is not supported by the layout convention')
    if re.search(r'CoverageBoost|CoverageSafetyBoost|Additional|More|SmallFiles', path.name):
        failures.append(f'{path}: non-subject filename')
for name, paths in owners.items():
    if len(paths) != 1:
        failures.append(f'{name}: duplicate simple name: {paths}')
print('\n'.join(failures) if failures else f'PASS: checked {len(files)} source files')
raise SystemExit(bool(failures))
'@ | python - @paths
if ($LASTEXITCODE -ne 0) { throw 'Test layout check failed' }
```

### Batch Test and Coverage Comparison

Run these PowerShell blocks from the repository root, with the prerequisites in [building and testing](testing.md) installed. Start on the revision before the batch. The commands create their evidence directories and assign both comparison paths. Each invocation builds Release before collecting coverage, and keeps its own TRX and Cobertura artifacts outside the checkout. Compare the three application assemblies and the managed MFTLib dependency; test-support assemblies are not production coverage.

```powershell
$evidence = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ("GitWizard/test-results/test-layout-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence | Out-Null

git submodule update --init
if ($LASTEXITCODE -ne 0) { throw 'Submodule initialization failed' }
bash ci/gates.sh native
if ($LASTEXITCODE -ne 0) { throw 'Dependency build failed' }

function Invoke-LayoutBatch([string]$Label, [string]$Filter) {
    dotnet build git-wizard.slnx -c Release | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }
    $directory = Join-Path $evidence ($Label + '-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory | Out-Null
    $Filter | Set-Content (Join-Path $directory 'filter.txt')
    git rev-parse HEAD | Set-Content (Join-Path $directory 'revision.txt')
    $filterArguments = @()
    if ($Filter) { $filterArguments = @('--filter', $Filter) }
    dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build --nologo `
        --settings GitWizardTests/coverlet.runsettings --collect 'XPlat Code Coverage' `
        @filterArguments --logger 'trx;LogFileName=tests.trx' --results-directory $directory | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Test failure; retain $directory" }
    Write-Host "Preserve artifacts: $directory"
    return $directory
}

$filter = '' # Full suite; use an unchanged subject filter only for a scoped batch.
$before = Invoke-LayoutBatch 'batch-before' $filter
```

Apply the mechanical batch now, or switch to its after revision. Keep this PowerShell session open so `$evidence`, `$filter` and `$before` remain assigned. If the dependency pin changed, repeat `git submodule update --init` and `bash ci/gates.sh native`, checking their exit codes as above. Then collect the after snapshot; the function rebuilds it rather than reusing stale binaries:

```powershell
$after = Invoke-LayoutBatch 'batch-after' $filter
```

Compare `$before` and `$after` evidence directories:

```powershell
@'
from collections import Counter, defaultdict
from pathlib import Path
from xml.etree import ElementTree as ET
import sys

def evidence(directory):
    root = Path(directory)
    reports = list(root.rglob('coverage.cobertura.xml'))
    if not reports:
        raise SystemExit(f'no Cobertura input: {root}')
    covered = defaultdict(set)
    executable = defaultdict(set)
    for report in reports:
        for package in ET.parse(report).getroot().findall('./packages/package'):
            assembly = package.attrib['name']
            if assembly not in {'GitWizard', 'git-wizard', 'GitWizardUI', 'MFTLib'}:
                continue
            for entry in package.findall('./classes/class'):
                filename = entry.attrib['filename'].replace('\\', '/')
                components = filename.split('/')
                if assembly in components:
                    filename = '/'.join(components[components.index(assembly):])
                key = (assembly, filename)
                for line in entry.findall('./lines/line'):
                    number = int(line.attrib['number'])
                    executable[key].add(number)
                    if int(line.attrib['hits']) > 0:
                        covered[key].add(number)
    if not executable:
        raise SystemExit(f'no production lines: {root}')
    results = Counter()
    for report in root.glob('*.trx'):
        for result in ET.parse(report).getroot().iter():
            if result.tag.rsplit('}', 1)[-1] == 'UnitTestResult':
                results[(result.attrib['testName'], result.attrib['outcome'])] += 1
    if not results:
        raise SystemExit(f'no TRX test results: {root}')
    return covered, executable, results

before, before_lines, before_tests = evidence(sys.argv[1])
after, after_lines, after_tests = evidence(sys.argv[2])
differences = []
for key in sorted(set(before_lines) | set(after_lines)):
    removed = before.get(key, set()) - after.get(key, set())
    added = after.get(key, set()) - before.get(key, set())
    changed_shape = before_lines.get(key, set()) != after_lines.get(key, set())
    print(f'{key}: before={len(before.get(key, set()))} after={len(after.get(key, set()))}')
    if removed or added or changed_shape:
        differences.append((key, sorted(removed), sorted(added), changed_shape))
if before_tests != after_tests:
    raise SystemExit(f'TRX drift: removed={before_tests-after_tests}, added={after_tests-before_tests}')
if differences:
    raise SystemExit(f'coverage drift (file, lost, gained, executable changed): {differences}')
print('PASS: identical test-case/outcome multisets and production line sets')
'@ | python - $before $after
if ($LASTEXITCODE -ne 0) { throw 'Batch preservation failed' }
```

Preserve and investigate any drift rather than accepting aggregate coverage. Fixture renames can change TRX test names without changing cases; account for those names explicitly. When production source changes between revisions, map line identities through the source diff before interpreting lost or gained line numbers. For an unchanged source line that varies with scheduling, rerun the before revision twice to establish that variation independently of the batch.
