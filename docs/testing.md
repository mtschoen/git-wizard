# Building and testing

## Build

MFTLib is a git **submodule** at `external/MFTLib`, built from source. Run
`git submodule update --init` once after cloning. `ci/gates.sh native` owns the
platform recipe; Windows delegates native compilation to `ci/windows-gates.ps1`.
`Directory.Build.targets` owns the assembly-reference bridge and native copying.
Use these files rather than copying a machine-specific MSBuild path.

```bash
git submodule update --init
bash ci/gates.sh native
dotnet restore git-wizard.slnx
dotnet build git-wizard.slnx -c Release --no-restore
```

Every git-wizard project consumes MFTLib as a built **assembly** (a HintPath `<Reference>` in the
repo-root `Directory.Build.targets`), not a `<ProjectReference>` - a `ProjectReference` would drag
the native `.vcxproj` into every consumer's `dotnet` build graph, which `dotnet` cannot load
(MSB4278). `Directory.Build.targets` also copies the built native library beside each project's
output on both platforms. `dotnet publish` works normally against the source-built submodule -
there is no publish guard. The header of `Directory.Build.targets` carries the retire-to-NuGet
note (swap to a `PackageReference` when MFTLib `0.3.0` ships); see it for the exact steps rather
than duplicating them here. The bridge resolves both managed assemblies from their `net10.0`
output directories, controlled by `_MFTLibTfm`, and fails before reference resolution if either
required assembly is missing. Only `GitWizardTests` references `MFTLibTestExtensions`.

## NUnit and evidence retention

To **run the NUnit suite**, build first, then run tests with `--no-build` (a plain `dotnet test`
re-invokes the build). Use the same configuration as the successful build. Assembly setup
creates a temporary home under `Environment.SpecialFolder.UserProfile`; a sandbox with a
read-only user profile cannot execute the suite, even when loopback sockets are allowed. In
that environment, the controller runs the suite outside the sandbox:

```powershell
$results = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ("GitWizard/test-results/" + [Guid]::NewGuid().ToString('N'))
dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release --nologo --results-directory "$results"
```

The results directory is deliberately outside a disposable checkout, so it survives a
`git clean -ffxd` or a worktree teardown. `GitWizardTests/coverlet.runsettings` enables TRX
logging even without a `--logger` argument. Preserve the results path VSTest prints on every
failed run before cleaning or removing a worktree; never overwrite it with a rerun. Use a unique
directory outside the checkout for full-suite and stress runs. On Linux, use a unique directory
under `${XDG_STATE_HOME:-$HOME/.local/state}/git-wizard/test-results`. CI uploads
`test-results-linux` and `test-results-windows` even when tests fail, but successful artifact
upload and download must still be verified. See [CI artifact retention](ci-and-release.md#ci-infrastructure).

For test-duration investigations, preserve the Windows and Linux `test-results-*` TRX artifacts from the same CI revision. Rank `UnitTestResult.duration`, retaining `testName`, its `TestDefinitions` class, and outcome. Record the CI Test-step wall clock separately from TRX start-to-finish and summed test durations. Keep runner identity and coverage settings with the comparison; do not turn these observations into elapsed-time assertions in NUnit. Individual-test duration reductions do not establish a full-step improvement under runner variability; verify and record both before and after CI Test-step timestamps and linked job/TRX evidence, and retain an incomplete disposition for the tracking issue if the required full-step reduction is not demonstrated.

`LiveWatchControllerTests.RespawnLimit_Stress` is an `[Explicit]` stress entry point that runs
the respawn-limit scenario 200 times across four concurrent workers; it is skipped by an
ordinary run and must be selected explicitly:

```powershell
dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release --nologo `
    --filter "FullyQualifiedName=GitWizardTests.UI.LiveWatchControllerTests.RespawnLimit_Stress" `
    --results-directory "$results"
```

`AsyncCondition.WaitUntilAsync` (`GitWizardTests/TestSupport/AsyncCondition.cs`) fails on the
first predicate exception rather than retrying, and its assertion message includes the original
exception's `ToString()` (stack included), the polled expression text, the poll count, and the
elapsed duration - do not read a passing stress run as proof that any specific historical failure
reproduced; it only demonstrates that this revised scenario is not itself racy.

Tests supply owned temporary cache directories. `GitWizardTests/JournalIsolationInitializer.cs`
activates journal isolation; `GlobalTestSetup` independently redirects `GITWIZARD_HOME`.
For cache and journal contracts, read MFTLib's [test isolation](../external/MFTLib/docs/test-isolation.md).

## Test organization and consolidation

Tests live under production-area folders in `GitWizardTests/`. Each fixture has a unique simple class name and one matching `<ClassName>.cs` file. Large fixture families split into independent concern-named classes with a one-line rationale. Shared helpers live in `TestSupport`; file moves preserve namespace-scoped setup. See [test organization](test-organization.md) for the area/partial map, layout check, and before/after per-file coverage procedure. `AgentInstructionsTests` verifies that test classes named here exist. Cite test classes and members by name, without source line numbers.

Place behavioral tests beside the fixture for their production subject. Use
table-driven cases for inputs with the same setup and assertions. Test filenames
describe the subject and exclude `CoverageBoost`, `CoverageSafetyBoost`,
`Additional`, `More`, and `SmallFiles`.

Before deleting a repeated test, record its surviving test/case and compare
covered production line sets by assembly and file before and after the subject
batch, using the same host, build configuration, filter, and
`GitWizardTests/coverlet.runsettings`. Preserve the TRX and Cobertura results
outside the checkout. A passing 55% aggregate gate alone does not prove that a
specific covered line or assertion survived.

Filename check (PowerShell, repository root):

    $bad = Get-ChildItem GitWizardTests -Recurse -File -Filter *.cs |
        Where-Object { $_.Name -match 'CoverageBoost|CoverageSafetyBoost|Additional|More|SmallFiles' }
    if ($bad) { throw ($bad.FullName -join [Environment]::NewLine) }

Expected: no output and successful exit.

## Shared test support (`GitWizardTests/TestSupport/`)

Central test scaffolding lives in `GitWizardTests/TestSupport/`:
- `TempDirectory`: Owned temporary directory fixture providing disposable isolated filesystem trees and directory cleanup (`Path.GetTempPath()` is restricted to this fixture).
- `LocalFilesSandbox`: Disposable test sandbox for `GITWIZARD_HOME` redirects, restoring prior environment state and clearing static caches on disposal.
- `RecordingUpdateHandler`: Unified concurrent recording `IUpdateHandler` double with snapshot array properties and optional invocation hooks (`StartingProgress`, `CreatingRepository`, `CompletingRefresh`, etc.).
- `QueuedUiDispatcher`: Synchronous or queued `IUiDispatcher` supporting manual draining, stepping, and off-UI-thread simulation without background threads.
- `ThreadedUiDispatcher`: Dedicated-thread FIFO `IUiDispatcher` pumping callbacks for realistic UI-thread affinity and debounce assertions.
- `TestViewModels`: Centralized factory methods for `MainViewModel` and `SettingsViewModel` with defaults to prevent scattered stub construction.
- `ScriptedVolumeChangeSource`: Unified `IVolumeChangeSource` and `IVolumeScanProgress` double for scripted batch delivery, arm gates, failure simulation, and late subscriber death replay.
- `FakeFanotifyNative` / `FanotifyMarkCall`: Rich in-memory Linux fanotify native mock supporting initialization, descriptor maps, directory opening, marks, read/poll, and handle resolution.
- `RootStatuses`: Consistent factory for synthetic `RootStatus` instances with independent `watchSupported` policy.
- `TestTimeouts`: Canonical timeouts exposed as get-only properties (`HangGuard` at 10s, `BrokerHangGuard` at 30s, `ShortHangGuard` at 5s).
- `AsyncCondition`: Deterministic condition poller (`WaitUntilAsync`) reporting poll count, elapsed time, and expression details upon timeout or predicate failure.
- `RefreshOverlapProbe`: Deterministic phase-boundary overlap detector for outer and nested refresh concurrency tests.

### Scaffolding regression checks

Regression checks for test scaffolding:

```bash
# Direct temp path allocation is forbidden outside TestSupport
grep -rn 'Path.GetTempPath()' GitWizardTests

# Update handlers must be consolidated in TestSupport
grep -rnE 'class .*: IUpdateHandler' GitWizardTests

# Hang guards must use TestTimeouts properties, not local static fields
grep -rn 'static readonly TimeSpan HangGuard' GitWizardTests
```

Expected: only `TestSupport/TempDirectory.cs` matches for temp path, only `TestSupport/RecordingUpdateHandler.cs` matches for `IUpdateHandler`, and zero matches for `static readonly TimeSpan HangGuard`.

## Debugging

- `dotnet-dump collect -p <pid>` - capture a dump of a hung/crashed process
- `dotnet-dump analyze <dump>` - inspect threads, stacks, exceptions (`pe` for last exception, `clrstack` for managed stacks)
- `dotnet-stack report -p <pid>` - quick stack trace of all threads without killing the process

Use these to debug crashes/freezes instead of guessing. For the GUI app, run the exe in the background, get its PID, and collect a dump if it hangs.

## Manual testing

Attended verification steps that cannot run on CI (real elevation prompts, a real desktop
session) live in [the manual test plan](manual-test-plan.md). Run its
**CLI live smoke** pass first on any attended re-test, before GitWizardUI testing -
`scripts/cli-live-smoke.ps1` drives it end to end and prints PASS/FAIL per step.

## Screenshots

### Local

Run `scripts/update-screenshots.ps1` (any shell with PowerShell; no display, elevation or WinAppDriver needed). It builds and runs `GitWizardUI.Screenshot` (headless Avalonia, in `git-wizard.slnx`) against a synthetic three-repository fixture (`GITWIZARD_HOME` redirected under `artifacts/`, `GITWIZARD_NO_MFT=1` so no UAC broker launches) under a hard timeout, and writes `Screenshots/GitWizardUI.png`; `-OutputPath <file>` writes elsewhere. Agents that need to "see" the UI run it and read the image. Never run the tool against this machine's real repositories: a live refresh of hundreds of them never settles within the capture window.

The Windows job in `ci.yml` runs the script to `artifacts/GitWizardUI-ci.png` on every non-pin-only run and uploads it as the `GitWizardUI-screenshot` artifact, so the generator cannot rot unseen. Both CI capture entry points call the same fixture wrapper, a 10-minute step deadline also covers build/setup, and missing screenshot artifacts fail their upload step. Manually run development tools are kept and exercised in CI, never deleted for lacking a CI caller; `ci/test_parse_jb_report.py` and `ci/test_verify_windows.py` do this for `ci/parse-jb-report.py` and `scripts/verify-windows.sh` in the contracts gate.

### CI (recommended for release prep)

Trigger `.gitea/workflows/screenshot.yml` manually from the Actions tab or the
repository's workflow-dispatch REST endpoint, using the configured forge and bot identity.

The workflow runs on `windows-latest`, captures the screenshot via the same fixture wrapper with a 10-minute step deadline, and uploads `GitWizardUI.png` as a workflow artifact (failing if missing). Download the artifact and commit it to `Screenshots/`.

## Verification sources

The build recipe is `ci/gates.sh` and `ci/windows-gates.ps1`; the reference paths and
pre-build guards are in `Directory.Build.targets`. NUnit setup is in
`GitWizardTests/GlobalTestSetup.cs` and `GitWizardTests/coverlet.runsettings`.
Stress workers and iterations are in `GitWizardTests/UI/LiveWatchControllerTests.cs`.
Screenshot isolation and capture are in `scripts/update-screenshots.ps1` and
`GitWizardUI.Screenshot/Program.cs`. CI upload names and conditions are in
`.gitea/workflows/ci.yml` and `.gitea/workflows/screenshot.yml`.
