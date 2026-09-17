# GitWizard

Multi-project solution for scanning and reporting on git repositories.

## Projects

- **GitWizard/** - Core class library (cross-platform: Windows, macOS, Linux)
- **git-wizard/** - CLI tool
- **GitWizardUI/** - Avalonia cross-platform desktop app (Windows/macOS/Linux); contains the view models under `ViewModels/` (behind `IUiDispatcher` / `IUserDialogs` / `IFolderPicker`)
- **GitWizardUI.Screenshot/** - Headless Avalonia screenshot tool (used by CI)
- **GitWizardTests/** - NUnit test project

## Build

MFTLib is a git **submodule** at `external/MFTLib`, built from source (MFTLib `0.3.0` isn't on
NuGet yet). Run `git submodule update --init` once after cloning - the tree does not build until
the submodule is checked out. It has a native C++ project (`MFTLibNative.vcxproj`, v143 toolset)
that only **VS MSBuild** can compile on Windows (CMake on Linux), while git-wizard's own projects
build entirely with the **`dotnet`** CLI. All five consumer projects target plain `net10.0`
and the whole solution builds on Linux. Build both managed submodule assemblies before the
consumer: `MFTLib.dll` and the test-only `MFTLibTestExtensions.dll`. A full native build uses
both toolchains:

```bash
git submodule update --init

# Windows: build the native lib with VS MSBuild (dotnet cannot load the .vcxproj), then
# everything managed - MFTLib.dll and the git-wizard solution - with dotnet.
"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" external/MFTLib/MFTLibNative/MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
dotnet build external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj -c Release -p:Platform=x64
dotnet restore git-wizard.slnx
dotnet build git-wizard.slnx -c Release --no-restore

# Linux: build the native lib with CMake/Ninja into the fixed external/MFTLib/build-linux
# binary dir, then MFTLib.dll and the solution with dotnet. Managed-only builds (and the
# whole test suite) work without the native step - only the runtime P/Invoke surface needs it.
cmake -S external/MFTLib/MFTLibNative -B external/MFTLib/build-linux -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build external/MFTLib/build-linux
dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
dotnet build external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj -c Release -p:Platform=x64
dotnet build git-wizard.slnx -c Release
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

To **run the NUnit suite**, build first, then run tests with `--no-build` (a plain `dotnet test`
re-invokes the build). Use the same configuration as the successful build. Assembly setup
creates a temporary home under `Environment.SpecialFolder.UserProfile`; a sandbox with a
read-only user profile cannot execute the suite, even when loopback sockets are allowed. In
that environment, the controller runs the suite outside the sandbox:

```bash
dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release --nologo
```

## Cleaning the working tree

`git clean -ffxd` must always be safe to run. It is the check that this checkout still matches a fresh clone, so it is run before starting new work, and it must never be the thing that loses something.

That safety comes from an invariant, not from a wrapper: **nothing unrecoverable lives in the working tree.** Every file here is either tracked and pushed, or reproducible by re-running a tool. There is no exclude list, because an exclude list would leave the tree unequal to a fresh clone and defeat the reason for cleaning.

Anything that fails that test belongs somewhere else. If a file is worth keeping, track it and push it; if it is only worth keeping on one machine, keep it outside the working tree. Do not add a file to this repository that is neither.

Git offers no way to protect a file from `git clean`. Aliases cannot shadow built-in commands, there is no `pre-clean` hook, and `-x` overrides `.git/info/exclude`. Only tracked files and files outside the tree are safe, which is why the invariant above is the whole mechanism.

What a clean removes and how each comes back:

| Removed | Restored by |
| --- | --- |
| `bin/`, `obj/`, `Debug/`, `.vs/`, `TestResults/` | rebuild / re-run the test suite |
| `build-diag.log`, `build.binlog`, `smoke-stderr.log` at the repository root | re-run the build or coverage command that produced them |
| `.claude/scripts/` scan output and tool caches (`.pi-subagents/`, `.ruff_cache/`, `.superpowers/`) | re-run the corresponding tool |
| `.claude/settings.local.json` | re-granted as needed; broad grants live in the user-scope settings, and any provisioned project-scope overrides are re-applied by the tool that wrote them |
| `.claude/AISLOP.md`, `.claude/CLAUDE.md` | `aislop hook install claude --project` |
| `external/MFTLib/` (submodule contents) | `git submodule update --init --recursive`, or `init.ps1`/`init.sh` below |

`.claude/AISLOP.md` and `.claude/CLAUDE.md` are generated boilerplate the aislop installer writes into a sentinel-fenced block, which is why they are not tracked. They do not restore themselves - nothing rewrites them until that command is run, so run it after a clean. It is also how to refresh them after an aislop upgrade.

### Getting back to work after a clean

`init.ps1` (Windows) and `init.sh` (Linux) at the repository root do the things a clean does not undo by itself - the `external/MFTLib` submodule checkout, the NuGet restore, and the generated agent files - and report any prerequisite they cannot install for you:

```powershell
git clean -ffxd && .\init.ps1          # restore only, a few seconds
git clean -ffxd && .\init.ps1 -Build   # also build MFTLib (native + managed) and the solution
```

From `cmd.exe`, use `init.bat`, which forwards to the same script:

```bat
git clean -ffxd && .\init.bat
git clean -ffxd && .\init.bat -Build
```

Keep the `.\` prefix. This machine sets `NoDefaultCurrentDirectoryInExePath=1`, so `cmd.exe` does not search the working directory for executables and a bare `init.bat` fails with "not recognized as an internal or external command".

```bash
git clean -ffxd && ./init.sh           # restore only
git clean -ffxd && ./init.sh --build   # also build native (cmake/ninja) + managed
```

Both are idempotent, so they are safe to run at any time, not only after a clean.

They also make one optional call: if a settings-provisioning tool is on PATH, they ask it to re-apply the project-scope settings it owns, since a clean removes `.claude/settings.local.json`. The call names a single feature rather than running the tool's whole pipeline, and the step is skipped entirely when the tool is absent, so nothing here depends on it.

They are separate scripts rather than one cross-platform script because the work genuinely differs: Windows resolves MSBuild through `vswhere` and builds `MFTLibNative.vcxproj` (the only toolchain that can), while Linux drives cmake/Ninja directly and restores/builds the four cross-platform projects individually rather than `git-wizard.slnx` as a whole, mirroring the Linux CI job exactly (`.gitea/workflows/ci.yml`).

Prerequisites checked but not installed - Windows: .NET SDK, Visual Studio with the MSVC C++ workload, aislop. Linux: .NET SDK, cmake, ninja, g++, aislop.

## Linting / lint gate

The bar is **0 findings** across every configured analyzer (see `TEST-REPORT.md` → Lint gate). Three tiers:

- **Analyzers + naming (CA*/IDE*/RCS*)** - `Directory.Build.props` sets `EnableNETAnalyzers`, `AnalysisLevel=latest-Recommended`, `EnforceCodeStyleInBuild`, and **`TreatWarningsAsErrors`**, so a normal `dotnet build`/MSBuild build *is* the gate. Roslynator 4.15.0 contributes only the explicitly pinned RCS1161 (explicit enum values), RCS1168 (contract-consistent parameter names), and RCS1238 (no nested conditional operators) rules from `.editorconfig`; every other RCS rule remains disabled by default, so dependency updates cannot silently expand the gate. AsyncFixer supplies the async checks, and `CA2000`/`CA1849`/`CA2016` the disposal, blocking-call, and token-forwarding ones. `EnforceCodeStyleInBuild` also turns the `.editorconfig` naming ruleset (IDE1006, at `warning`) into a **build error** - so the editorconfig is the **authoritative naming gate**, not just an IDE hint. MFTLib (outside this tree) does not inherit these.
- **Formatting + naming** - `.editorconfig` is the canonical fleet C# standard (source of truth: `~/.claude/notes/idioms_csharp_naming.md`): **utf-8 (no BOM)**, CRLF, 4-space, and the full naming ruleset at `warning` → build error (PascalCase types/members/const/static-readonly; `_camelCase` **all** private/internal fields incl. mutable static; camelCase params/locals; no `m_`/`k_`/`s_`). Formatting/charset check: `dotnet format git-wizard.slnx --verify-no-changes`; apply: `dotnet format git-wizard.slnx`. Local on-save hooks (`scripts/format-on-save.py`, wired in the tracked `.claude/settings.json` for Claude and `.agents/hooks.json` for Antigravity/AGY) run whitespace formatting per edit. The Python entry point is the single cross-platform implementation for both harnesses: it parses the hook JSON from stdin and runs `dotnet format whitespace` directly via `subprocess`, reporting one of three outcomes the calling harness can tell apart - a benign `skipped` (not applicable, or `dotnet` unavailable, exit 0), a successful `applied` (exit 0), or a loud `FAILED` when the formatter ran and returned a nonzero exit code (stderr, exit 2). (`dotnet format` fixes charset/whitespace/style but **cannot** auto-fix IDE1006 naming - rename via Rider/`jb cleanupcode`.)
- **Deep inspection** - `~/.dotnet/tools/jb.exe inspectcode git-wizard.slnx -o="$TEMP/jb.xml" --settings="git-wizard.slnx.DotSettings" --severity=WARNING --no-updates`, gated by `ci/parse-jb-report.py` (exits non-zero on any finding). Match ci.yml's invocation exactly - omitting `--settings`/`--severity` reports hundreds of suggestion-tier findings the gate never sees. Now a *supplementary* deep pass - the editorconfig (above) is the primary naming gate. **jb-CLI binds `InconsistentNaming` to the machine's *global* ReSharper config, not the solution `.DotSettings`** - the global config here is aligned to the canonical convention; Rider uses the solution `.DotSettings` (which has the ExtraRules jb-CLI ignores).
- **aislop** (AI-slop gate) - `.gitea/workflows/aislop.yml` gates the whole repo (C# + Python) at `failBelow: 100` in `.aislop/config.yml`. The C# engine lives **only in the personal fork `gitea.fleet.sticktoitive.net/schoen/aislop`** (the public npm `aislop` scores just the Python `ci/` tooling - 2 files; `github.com/mtschoen/aislop` is a mirror that can lag behind, so the workflow clones from Gitea, the canonical source), so the workflow clones + installs (`pnpm`, via `npx`) and builds that fork at the commit pinned in **`.aislop/fork-commit`** and runs the built `dist/cli.js`, then sanity-fails if the scan covers <10 files (catching a silent Python-only pass). The job carries no .NET toolchain: `lint.csharp.projectEvaluation` is `false`, so the project-backed C# tools stay in `ci.yml` and are not paid for twice. `failBelow: 100` enforces a 0-findings bar, same as the other analyzers; current score is tracked in `TEST-REPORT.md`, not asserted here. Findings map to fixes, not mutes: oversized files split into partials, long functions extracted, TODOs tracked as gitea issues, narrative comments reworded to trip aislop's why-marker exemption. The pin is normally bumped by the aislop fork's sync-consumers workflow via a PR that edits `.aislop/fork-commit`; a manual bump is the same edit, made deliberately (don't track the moving `schoen/main`).

CI wires all three (`.gitea/workflows/ci.yml`). Both jobs (Linux + Windows) check out the
`external/MFTLib` submodule, build it from source with the platform-appropriate toolchain (see the
**Build** section), then restore/build/test/format-check the solution green with the `dotnet` CLI.
(Before the submodule, CI was dormant/red-by-design because the committed local `ProjectReference`
to a sibling MFTLib checkout couldn't be restored on the runners.)

## MFTLib Local Development

MFTLib lives in the repo as a submodule at `external/MFTLib`, pinned to a specific commit.
git-wizard depends on MFTLib `0.3.0` (the `IElevationProvider` API), which is **not on NuGet
yet**. To iterate on MFTLib changes, edit files directly under `external/MFTLib/` and rebuild
(native + managed, per platform - see the **Build** section). To move git-wizard to a different
MFTLib commit:

```bash
cd external/MFTLib && git fetch && git checkout <ref> && cd ../..
git add external/MFTLib && git commit -m "build: bump MFTLib submodule to <ref>"
```

The submodule URL in `.gitmodules` is relative (`../MFTLib.git`), so it resolves to GitHub for a
local clone (origin = github.com/mtschoen/git-wizard) and to Gitea on the CI runner. **The pinned
commit must exist on both MFTLib remotes.**

**Retiring the submodule** (when MFTLib `0.3.0` ships to NuGet): see the retire-to-NuGet note in
`Directory.Build.targets`'s header for the exact steps (delete the submodule + that file, add a
`PackageReference` to `GitWizard/GitWizard.csproj`).

## Key Architecture

- **MFTLib** - Windows MFT discovery uses the packed columnar `MFTLib.Index.FileIndex`. `MftIndexSession` owns the index and its borrowed broker connection, disposing the index before the connection. It opens `BrokerMftBlockProducer` with the `DirectoryIndex` profile and `KeepFileNames = [".git"]`, so blocks retain directories plus `.git` markers. Discovery queries `FindByName(".git")` and `FileEntry.Parent`, applies consumer scope/ignore policy to logical Windows paths, then validates candidates through the Windows filesystem boundary. Linux and `--no-mft` retain recursive discovery. `TryFindAllRepositoriesUsingMftAsync` disposes its result after querying; the UI report can transfer a prepared owner for Live to adopt without another cold scan or UAC prompt.
- **Discovery scope and cache policy (P10)** - The broker gathers filtered whole-volume metadata: directories plus `.git` marker files across each selected volume, potentially including other users' directory names. Consumer search/ignore policy narrows repository candidates after collection. Live hydration may add observed ordinary-file metadata. A process running as the user can read the mapped or backing metadata. `FileIndex` has a persistent-cache capability, but this port selects `FileIndexOptions.NoCache = true`; persistent inventory caching is outside plan 4. Blocks are temporary OS delete-on-close files, removed when the last handle closes (including process exit), so no-cache mode still touches disk. The existing `~/.GitWizard/repositories.txt` and `~/.GitWizard/report.json` persist independently.
- **Marker matching (P14)** - `GitWizardApi.SelectGitEntries` deliberately adopts the case-insensitive matching of `FileIndex.FindByName(GitEntryName)`. A directory named `.GIT` is therefore discovered as a repository marker, subject to the same scope and filesystem validation as `.git`. This matches NTFS's default case-insensitive behavior; do not add an ordinal post-filter. `FileEntry.Path` is a logical Windows path, including for enumeration indexes over Linux trees. Keep filesystem validation at the Windows boundary; tests inspect entry structure rather than opening those logical paths.
- **Discovery ownership** - Exactly one consumer owns the transferable `MftIndexSession` at a time: discovery result -> report -> view model -> `IndexVolumeChangeSource` -> disposal. `FileIndex` is borrowed query access, not a second resource owner. Detach/take operations clear the previous holder; replacing an owner disposes the previous one. Untransferred discovery results and reports dispose their retained owner. `DiscoveryIndex` and its owner are never serialized; report schema remains **2.1** and Git metadata remains owned and reported by the existing Git repository layer. This port changes repository discovery, not Git reporting. Stopping Live detaches handlers and stops the pump before disposing the session, which disposes the index before its broker connection. Restart creates a new source and follows the same single-owner rule; a disposed, faulted, or drive-mismatched prepared owner is disposed and replaced, never reused.
- **MFTLib.ElevationUtilities** - Elevation helpers from MFTLib: `IsElevated()`, `CanSelfElevate()`, `TryRunElevated(args, timeoutMs)`. `TryRunElevated` is used by WindowsDefender for adding exclusions; MFT discovery always uses the journal broker, including when the caller is already elevated.
- **Windows Defender setup:** On Windows, Configure Windows Defender adds process-name exclusions for `dotnet.exe`, `git.exe`, `git-lfs.exe`, `git-wizard.exe`, and `GitWizardUI.exe`. The desktop executable also hosts its elevated MFT broker. The result and process list appear in the setup alert and the in-memory Errors window; this window includes successful Defender setup diagnostics as well as errors. These exclusions reduce protection for files opened by any executable with a matching name, regardless of location; they do not exclude the executable file itself or guarantee that every Defender capability is bypassed. No MFT cache-path, temporary-directory, or volume exclusion is added. GitWizard uses `NoCache = true` for index blocks.
- **Self-elevation** - Both CLI and GitWizardUI dispatch MFTLib's elevated `--broker` child mode via `ElevatedEntryPoint.TryHandle` (for broker-backed MFT discovery, `--watch`, and the UI's Live mode), plus the `--elevated-defender` hidden arg for Defender exclusions, before any normal/Avalonia startup.
- **IUpdateHandler** - Interface for progress reporting, used by both CLI (UpdateHandler) and GitWizardUI (MainViewModel).
- **Collection swap pattern** - `ApplyFilterAndGrouping()` builds a new `ObservableCollection` off-screen and swaps it in one shot to avoid per-item layout updates with 700+ repos.
- **UI command queue** - Background refresh threads enqueue `RepositoryUICommand` structs; a UI update thread drains them in batches on the main thread every 250ms.
- **RefreshStatus enum** - Per-repo status (Refreshing/Success/Timeout/Error) drives the status icon in column 0. `MarkRefreshFailed()` on `GitWizardRepository` handles timeout/error paths.
- **Journal watch (`--watch` CLI, and the UI's Live mode)** - Auto-detects changes in tracked repositories, one UAC prompt for the whole run. The pipeline lives in `GitWizard/Watch/` behind the `IVolumeChangeSource` platform seam.
  - **`IndexVolumeChangeSource`** (Windows) owns a `MftIndexSession`, adopting a prepared discovery owner only when it is usable and its normalized drive set matches. Otherwise it disposes that owner and opens a replacement. An arm that opens a fresh session forwards the broker's throttled ScanProgress frames as `IVolumeScanProgress.ScanProgress` (`VolumeScanProgress`: drive, 1-based drive ordinal/total, rows written and nullable total); an adopted prepared owner scans nothing and reports nothing. `FileIndex` owns live mutation, path resolution, replay, and resume cursors; the adapter maps `Changed` into volume batches and consumes `WatchFaulted`. A drive-specific fault raises optional `IVolumeChangeDiagnostics.DriveFailed` while healthy drives continue. `DriveStatus.WatchFailureMessage` is independent of `DriveState`: a failed watch may leave a `Ready`, queryable block. Source-wide or all-drive loss raises `SourceDied` once and closes the output channel. Subscriber faults reach the reader as errors. Shutdown detaches index handlers and stops the pump before disposing its owner.
  - **`FanotifyInterop`** (Linux) is the P/Invoke layer plus capability probe for the `fanotify` counterpart (`Available` / `RequiresElevation` / `UnsupportedKernel` / `UnsupportedFileSystem`, kernel floor 5.9). Probed by `MainViewModel` to enable Live mode on supported kernels and filesystems while gracefully disabling it with user-facing reasons on older kernels or unsupported filesystems.
  - **`FanotifyVolumeChangeSource`** (Linux) is the `IVolumeChangeSource` backend for whole-filesystem change detection via fanotify.
  - **`RepositoryWatchService`** is the platform-agnostic layer on top: it groups tracked roots by volume, maps change batches through `GitWizard.RepositoryChangeFilter.Classify`, and dedupes/debounces per window. It subscribes to optional drive diagnostics and optional `IVolumeScanProgress` before arming and detaches on shutdown, publishing immutable `ScanErrors` snapshots for initial scan errors and later watch failures and re-raising `ScanProgress`. `ScanErrorsChanged` fires outside the error lock, so a warning does not depend on a repository event. Sources without diagnostics retain their existing watch behavior.
  - **`RepositoryChangeFilter`** is pure and cross-platform-testable. `Classify` is path-based and emits Changed/Created/Deleted; it compares through `GitWizard.Watch.WatchPath`, which retains a path's syntax (Windows-rooted vs POSIX) instead of rewriting separators, so a POSIX root is reported back as `/repos/repo-a` rather than `\repos\repo-a`.
  - **Consumers.** `Program.cs` dispatches the elevated `--broker` child through `ElevatedEntryPoint.TryHandle` before normal startup. `--watch` uses the platform source factory (`IndexVolumeChangeSource` on Windows), prints `changed:` / `created:` / `deleted: <root>`, and prints each new or changed `scan error on <drive>: <message>` once while the stream is live. `LiveWatchController` forwards `ScanErrorsChanged` and `ScanProgress`; the UI shows "Scanning D: (n of N), x of y records" with a progress bar next to the amber Live button until the watch reports started (green), the start fails, or Live stops. The UI posts warning changes through its dispatcher even without repository events. A drive-specific failure keeps Live active on healthy drives. Terminal loss retains the existing stop policy and at most three elevated respawns. `Started` and the green Live state describe pump startup, never a catch-up completion fence ([MFTLib#139](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/139)). The UI still correlates delete/create pairs by remote URL.

## Local files

GitWizard stores config and cache in `~/.GitWizard/`:
- `config.json` - Search paths, ignored paths
- `repositories.txt` - Cached repo list
- `report.json` - Cached report

## Debugging

.NET diagnostic CLI tools are installed globally:
- `dotnet-dump collect -p <pid>` - capture a dump of a hung/crashed process
- `dotnet-dump analyze <dump>` - inspect threads, stacks, exceptions (`pe` for last exception, `clrstack` for managed stacks)
- `dotnet-stack report -p <pid>` - quick stack trace of all threads without killing the process

Use these to debug crashes/freezes instead of guessing. For the GUI app, run the exe in the background, get its PID, and collect a dump if it hangs.

VS2026 is installed at `C:/Program Files/Microsoft Visual Studio/18/Community/` for interactive debugging when CLI tools aren't enough.

## Screenshots

### CI (recommended for release prep)

Trigger the Gitea Actions workflow manually from the Actions tab or via API:

```bash
curl -fsSL -X POST -H "Authorization: token $(cat ~/.gitea-token)" \
    -H "Content-Type: application/json" \
    -d '{"ref":"main"}' \
    "https://gitea.fleet.sticktoitive.net/api/v1/repos/schoen/git-wizard/actions/workflows/screenshot.yml/dispatches"
```

The workflow runs on `windows-latest`, captures the screenshot using Avalonia headless rendering, and uploads `GitWizardUI.png` as a workflow artifact. Download the artifact and commit it to `Screenshots/`.

## Release checklist

1. Update `<Version>` in `GitWizardUI/GitWizardUI.csproj`
2. Update version in CLI help text in `git-wizard/Program.cs`
3. Update screenshot: trigger CI workflow (see above) or run locally
4. Commit version bump, screenshot, and all pending changes
5. `git tag v0.x.y && git push origin main --tags`
6. CI builds and publishes the release with all artifacts attached, then mirrors it to `github.com/mtschoen/git-wizard`. Watch `https://gitea.fleet.sticktoitive.net/schoen/git-wizard/actions`. The release workflow's publish job asserts the tag matches the csproj `<Version>` and fails fast on drift.

The release attaches: `git-wizard-{ver}-{rid}.zip` and `GitWizardUI-{ver}-{rid}.zip` for `rid in {win-x64, linux-x64, osx-x64}`. See `.gitea/workflows/release.yml` and the **CI infrastructure** section below.

## CI infrastructure

Workflows live in `.gitea/workflows/` (`ci.yml`, `release.yml`) - the YAML is the source of truth for jobs and steps. Operational setup that is *not* in the YAML:

- **Runners:** `ci.yml` splits across two self-hosted Gitea runners - `llamabox-ubuntu` (label `ubuntu-latest`, cross-platform build + the NUnit suite with coverage) and `llamabox-windows` (label `windows-latest`, full-solution build + all NUnit tests). Both runners run the test suite; only the Linux job collects coverage.
- **Coverage gate:** the Linux job runs `ci/post-coverage-status.py`, which (a) posts the informational `pr-crew/coverage` commit status, (b) writes a line/branch table to the job summary, and (c) **fails the job when line coverage drops below `--gate-line` (currently 55%)**. Re-baselined to the whole `GitWizardUI` assembly after the VM fold (2026-05-26) - the app's untestable Avalonia glue (Views/App/Program) now sits in coverage scope; ratchets up as ViewModel coverage grows (issue #36). The Cobertura XML is also uploaded as the `coverage-cobertura` artifact.
- **`ci-bot` identity:** the release workflow authenticates as a dedicated Gitea user `ci-bot` (reusable across personal repos). Provision on the Gitea host:
  ```bash
  sudo -u git gitea admin user create --username ci-bot --email ci-bot@llamabox.internal --random-password --must-change-password=false
  sudo -u git gitea admin user generate-access-token --username ci-bot --token-name git-wizard-ci --scopes write:repository --raw
  ```
  Add `ci-bot` as a **Write** collaborator on `schoen/git-wizard` (needed to create releases + upload assets). `write:repository` is the only scope required.
- **Secret:** store `ci-bot`'s PAT as the repo secret **`CI_GITEA_TOKEN`** (Settings → Actions → Secrets). `release.yml` reads `${{ secrets.CI_GITEA_TOKEN }}`. To rotate: re-run `generate-access-token` and re-paste the value - no code change.
- **GitHub release mirror:** after a tag push creates the Gitea release and uploads all assets, `release.yml` creates the matching release at `github.com/mtschoen/git-wizard` and uploads the same zip files. The mirror is deliberately skipped by PR publish-smoke runs. A mirror failure fails the release job loudly; the Gitea release may already exist, so repair or remove the partial releases before rerunning the tag workflow.
- **GitHub mirror secret:** store a GitHub PAT with `repo` scope for `mtschoen/git-wizard` as the Gitea repo Actions secret **`GITHUB_RELEASE_TOKEN`**. The workflow validates that credential before creating the Gitea release, so a missing or revoked token fails without leaving a Gitea-only release. Before the first real release, use a throwaway branch to set the app and CLI versions to `0.0.0-mirror-test`, tag it `v0.0.0-mirror-test`, and push that tag. Verify both prerelease pages and their assets, then delete both releases and the test tag.
- **Branch protection** (`main`, configured in the Gitea UI, not in any file): require status checks `build-linux` + `test-windows`, require up-to-date branches, restrict force-pushes. Configure *after* the first green CI run so you don't lock yourself out.
- **Deliberate non-goals:** no binary signing, no MAUI (retired 2026-05-26 - GitWizardUI is the Avalonia app, covering cross-platform desktop), and no macOS CI runner.
- **Artifact-upload cert gap:** TLS verification stays enabled in both workflows without exception. Rather than bypassing verification, `ci.yml` tolerates upload failure (`continue-on-error: true`) for the coverage-artifact upload because the Linux runner's Node does not trust the mkcert CA on the act_runner artifact backend. The coverage gate reads the cobertura file directly, so the upload is a nice-to-have. Tracked in `PLAN.md` -> Infrastructure.

### Preview builds (`/preview`)

`/preview` on a git-wizard PR (pr-crew comment command or the ops-dashboard button) builds and runs that PR's `GitWizardUI` on the platform you ask for. Default platform is **windows**; `/preview linux` targets llamabox.

- **Windows** (`.gitea/workflows/preview.yml`, dispatched on `windows-latest`): MSVC native build → self-contained `win-x64` publish → zip → upload to the gitea generic package registry at `…/packages/schoen/generic/git-wizard-pr-<N>/<head-sha>/GitWizardUI-windows-x64.zip` (auth: `CI_GITEA_TOKEN`). The upload is existence-guarded so a re-dispatch for the same SHA is a no-op.
- **Linux** (`.preview/up`, run by pr-crew on llamabox in a detached PR-head worktree): CMake native build → self-contained `linux-x64` publish → zip → registry upload (auth: `PREVIEW_GITEA_TOKEN`). If a live graphical session exists it launches the app on llamabox's hyprland display via XWayland — GitWizardUI is Avalonia 11.2 (X11-only on Linux), so `DISPLAY` is the load-bearing var (`kind: "app"`, PID in `$PREVIEW_PID_FILE`); otherwise it posts artifact links only (`kind: "artifact"`). `.preview/down` kills the launched process.
- **Run a published artifact yourself:** `scripts/run-preview.ps1 [<PR#>]` (Windows) or `scripts/run-preview.sh [<PR#>]` (Linux). No arg fetches the newest git-wizard preview package; both download the current-OS zip from the registry, unzip to a temp dir, and launch `GitWizardUI`. Set `GITEA_BASE_URL` to the Gitea origin; set `GITEA_TOKEN` if anonymous registry reads are refused.

Enrollment (adding git-wizard to pr-crew's `[preview]` config on llamabox) is an operator step, not in this repo — see the plan's deploy notes.

## Tips

- `deepRefresh` parameter skips expensive `git update-index --refresh` during auto-refresh; per-repo ↻ button triggers it on demand
- **Avalonia commands:** the view models expose a *custom* `GitWizardUI.ViewModels.ICommand` (not `System.Windows.Input.ICommand`), so Avalonia `Button.Command="{Binding ...}"` silently ghosts/disables the button. Wire Avalonia buttons with `Click=` handlers that call `command.Execute(null)` - the whole app follows this convention.
- **Avalonia bindings don't apply the target property's `TypeConverter`** - binding a *string* to a `Thickness` property - e.g. `RepositoryNodeViewModel.ItemPaddingString` → `Padding` - throws `InvalidCastException` on every realized row during scroll. Avalonia's default converter *does* handle string→primitive, string→enum (`FontWeight`), and string→`IBrush` (`StatusColorHex`→`Foreground`), but NOT `Thickness`. Use an explicit `IValueConverter` (`StringToThicknessConverter`) and keep the VM framework-agnostic (string in, convert in the view).
- **`LibGit2Sharp.Repository.IsValid(path)` is not fully non-throwing:** it returns `false` for a plain non-repo directory, but *throws* `LibGit2SharpException` for other conditions - notably git's "repository path ... is not owned by current user" ownership protection. `GitWizardRepository.Refresh` guards `new Repository(...)` with `IsValid` to skip stale/non-repo cache entries without spamming first-chance `RepositoryNotFoundException`, but that check lives **inside** the existing try so ownership/other errors still fall through to the catch as a normal refresh error (`RefreshConcurrencyTest` covers this). Don't hoist the `IsValid` call outside the try.
- **Refresh vs discovery/UAC:** ordinary GUI Refresh re-checks cached repository paths (`~/.GitWizard/repositories.txt`) without MFT discovery or UAC. A missing cache triggers discovery. Shift+click Refresh clears the cache and rediscovers; replacing the prepared discovery owner disposes the previous one. Starting Live transfers that owner to the Windows source; a disposed, faulted, or mismatched owner is replaced.
- **Avalonia UI updates:** `MainViewModel.SendUpdateMessage` coalesces status posts (at most one UI update in flight) so a large/recursive scan can't flood the dispatcher and wedge the window ("Not Responding"). Don't revert it to `_ui.Post` per message.
- **Refresh runs `ApplyFilterAndGrouping` off the UI thread:** `RefreshAsync` awaits with `ConfigureAwait(false)`, so its tail (incl. `ApplyFilterAndGrouping` and the `Repositories` swap) runs on a thread-pool thread. Avalonia *tolerates* the off-thread collection-property swap, but visual-tree access (e.g. `ScrollViewer.Offset`) calls `VerifyAccess()` and throws `Call from invalid thread`. The swap + its `AfterRepositoriesSwap` hook are therefore marshaled to the UI thread via `_ui.Post` when `!_ui.IsOnUiThread` (the off-screen build stays off-thread for the 700-repo perf win). Keep any UI-touching swap work inside that marshaled block.
- **Scroll preservation on refresh (RESOLVED - `MainWindow.RestoreScrollAnchor`):** the `Repositories` swap resets the `ListBox` to the top. Restoring is NOT a one-shot `ScrollViewer.Offset` set - under `VirtualizingStackPanel` the scrollable extent is an *estimate* that's wrong until layout settles (AvaloniaUI/Avalonia#17460/#17848), so a single offset set lands against a stale extent and is silently clamped (the old symptom: "scroll did nothing"). The working recipe is a **closed loop**: `ScrollIntoView(anchor)` *once* to materialize it, then a bounded `DispatcherTimer` measures the anchor row's actual Y each layout pass and corrects `offset += containerY + above` until it reaches the top (converges in 1-2 passes). Don't re-call `ScrollIntoView` each pass (it re-aligns a partly-off-top row and fights the nudge → oscillation). `ScrollIntoView` alone does NOT top-align, and a two-step ScrollIntoView trick is unreliable with variable row heights. Validated headless + real window by the `avalonia-vsp-scroll-top` spike (`~/.claude/notes/spike_avalonia-vsp-scroll-top.md`). **Headless does NOT reproduce the bug** (a single offset set sticks when layout is pumped synchronously) - validate scroll/extent timing in a real `--headed` window.
- The during-refresh jump-to-top (list empties + streams back from row 0) is a *separate, accepted* behavior, not the swap reset: `RefreshAsync` `Repositories.Clear()`s at the start (line ~1005) and `AddRepository` streams rows back via `Repositories.Add`. Keeping scroll through the whole scan would mean not mutating the visible list during refresh (losing live per-row feedback) or an in-place diff (fights the sort/group atomic-swap perf path). Deliberately left as snap-back-after-refresh.
- **URL-valued or stale `branch.<name>.remote`, including through a conditional include:** git permits a branch's remote to be a URL ("fetch from this URL") or a remote that is no longer configured, and either can be true only via a `gitdir:` conditional include that a standalone, repository-less config read never evaluates. `GitWizardRepository.ResolveTrackedBranch` (`GitWizardRepository.UpstreamDiagnostics.cs`) is the single decision point `CollectBranches` and `RefreshRemoteDivergence` both call: it resolves the upstream through native `Branch.TrackedBranch` (which reads the repository's own config and therefore does evaluate `includeIf`), catches only `InvalidSpecificationException` (the URL form, git-wizard#185), and otherwise treats a `null` result as untracked. A `Configuration.BuildFrom` read of the common repository's `config` file (never `Branch.RemoteName` or `Repository.Config`, which would drop a linked worktree's shared config - see the file header) supplies the value named in the diagnostic when `TrackedBranch` merely returns `null`; only when it throws `InvalidSpecificationException` instead - typically because a conditional include overrides an otherwise-valid base value to a URL, which that repository-less read never evaluates - does `GetEffectiveUpstreamRemoteViaGit` run `git config --null --get branch.<name>.remote` in the repository's own working directory instead, since only that (or native resolution) evaluates `includeIf`; neither read decides anything, each only names the value the diagnostic reports. The branch reports as untracked (`HasUpstream = false`, remote divergence 0/0) with one Warning per (common repository, branch, value) for the lifetime of one refresh (`UnresolvedUpstreamDiagnosticScope`, opened once per outermost `Refresh`/`GitWizardReport.Refresh` call and flowing into parallel worker branches via `AsyncLocal`), deduped across worktree paths via the common repository directory read from the gitdir's `commondir` file (falling back to the gitdir itself when no `commondir` file exists), not a path-suffix heuristic. The same unguarded `TrackedBranch` call remains in `BranchInventoryReport.CreateBranchInfo`; tracked as a follow-up issue.
