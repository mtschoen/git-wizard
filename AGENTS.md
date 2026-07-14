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
build entirely with the **`dotnet`** CLI - so a full build uses both toolchains:

```bash
git submodule update --init

# Windows: build the native lib with VS MSBuild (dotnet cannot load the .vcxproj), then
# everything managed - MFTLib.dll and the git-wizard solution - with dotnet.
"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" external/MFTLib/MFTLibNative/MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
dotnet restore git-wizard.slnx
dotnet build git-wizard.slnx -c Release --no-restore

# Linux: build the native lib with CMake/Ninja into the fixed external/MFTLib/build-linux
# binary dir, then MFTLib.dll and the solution with dotnet. Managed-only builds (and the
# whole test suite) work without the native step - only the runtime P/Invoke surface needs it.
cmake -S external/MFTLib/MFTLibNative -B external/MFTLib/build-linux -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build external/MFTLib/build-linux
dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
dotnet build git-wizard.slnx -c Release
```

Every git-wizard project consumes MFTLib as a built **assembly** (a HintPath `<Reference>` in the
repo-root `Directory.Build.targets`), not a `<ProjectReference>` - a `ProjectReference` would drag
the native `.vcxproj` into every consumer's `dotnet` build graph, which `dotnet` cannot load
(MSB4278). `Directory.Build.targets` also copies the built native library beside each project's
output on both platforms. `dotnet publish` works normally against the source-built submodule -
there is no publish guard. The header of `Directory.Build.targets` carries the retire-to-NuGet
note (swap to a `PackageReference` when MFTLib `0.3.0` ships); see it for the exact steps rather
than duplicating them here.

To **run the NUnit suite**, build first, then run tests with `--no-build` (a plain `dotnet test`
re-invokes the build):

```bash
dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release --nologo
```

## Linting / lint gate

The bar is **0 findings** across every configured analyzer (see `TEST-REPORT.md` → Lint gate). Three tiers:

- **Analyzers + naming (CA*/IDE*/RCS*)** - `Directory.Build.props` sets `EnableNETAnalyzers`, `AnalysisLevel=latest-Recommended`, `EnforceCodeStyleInBuild`, and **`TreatWarningsAsErrors`**, so a normal `dotnet build`/MSBuild build *is* the gate (Roslynator 4.15.0 included). `EnforceCodeStyleInBuild` also turns the `.editorconfig` naming ruleset (IDE1006, at `warning`) into a **build error** - so the editorconfig is the **authoritative naming gate**, not just an IDE hint. MFTLib (outside this tree) does not inherit these.
- **Formatting + naming** - `.editorconfig` is the canonical fleet C# standard (source of truth: `~/.claude/notes/idioms_csharp_naming.md`): **utf-8 (no BOM)**, CRLF, 4-space, and the full naming ruleset at `warning` → build error (PascalCase types/members/const/static-readonly; `_camelCase` **all** private/internal fields incl. mutable static; camelCase params/locals; no `m_`/`k_`/`s_`). Formatting/charset check: `dotnet format git-wizard.slnx --verify-no-changes`; apply: `dotnet format git-wizard.slnx`. Local on-save hooks (`.claude/scripts/format-on-save.ps1`, wired in `.claude/settings.local.json` for Claude and `.agents/hooks.json` for Antigravity/AGY) run whitespace formatting per edit. (`dotnet format` fixes charset/whitespace/style but **cannot** auto-fix IDE1006 naming - rename via Rider/`jb cleanupcode`.)
- **Deep inspection** - `~/.dotnet/tools/jb.exe inspectcode git-wizard.slnx -o="$TEMP/jb.xml" --settings="git-wizard.slnx.DotSettings" --severity=WARNING --no-updates`, gated by `ci/parse-jb-report.py` (exits non-zero on any finding). Match ci.yml's invocation exactly - omitting `--settings`/`--severity` reports hundreds of suggestion-tier findings the gate never sees. Now a *supplementary* deep pass - the editorconfig (above) is the primary naming gate. **jb-CLI binds `InconsistentNaming` to the machine's *global* ReSharper config, not the solution `.DotSettings`** - the global config here is aligned to the canonical convention; Rider uses the solution `.DotSettings` (which has the ExtraRules jb-CLI ignores).
- **aislop** (AI-slop gate) - `.gitea/workflows/aislop.yml` gates the whole repo (C# + Python) at `failBelow: 100` in `.aislop/config.yml`. The C# engine lives **only in the personal fork `github.com/mtschoen/aislop`** (the public npm `aislop` scores just the Python `ci/` tooling - 2 files), so the workflow clones + `npm install`-builds that fork at a **pinned commit** (`env.AISLOP_FORK_COMMIT`) and runs the built `dist/cli.js`; it also `dotnet tool install`s `roslynator.dotnet.cli` + `JetBrains.ReSharper.GlobalTools` (the C# lint engine shells out to `roslynator` and `jb` inspectcode, which targets the whole `.slnx`) and `dotnet restore`s the solution so jb can build it, and sanity-fails if the scan covers <10 files (catching a silent Python-only pass). `failBelow: 100` enforces a 0-findings bar, same as the other analyzers; current score is tracked in `TEST-REPORT.md`, not asserted here. Findings map to fixes, not mutes: oversized files split into partials, long functions extracted, TODOs tracked as gitea issues, narrative comments reworded to trip aislop's why-marker exemption. Bump the pinned commit deliberately (don't track the moving `schoen/main`).

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

- **MFTLib** - Used on Windows for fast .git discovery via NTFS MFT parsing. When already elevated, `GitWizardApi.TryFindAllRepositoriesUsingMftAsync` scans in-process; when not, it takes the cold scan through MFTLib's journal broker (`JournalBrokerClient`, one UAC prompt) and pulls repository roots from the returned `ScanRecord`s - no temp-file handoff.
- **MFTLib.ElevationUtilities** - Elevation helpers from MFTLib: `IsElevated()`, `CanSelfElevate()`, `TryRunElevated(args, timeoutMs)`. `TryRunElevated` is used by WindowsDefenderException for adding exclusions; MFT discovery elevates through the journal broker instead.
- **Self-elevation** - Both CLI and GitWizardUI dispatch MFTLib's elevated `--broker` child mode via `ElevatedEntryPoint.TryHandle` (for broker-backed MFT discovery and `-watch`), plus the `--elevated-defender` hidden arg for Defender exclusions, before any normal/Avalonia startup.
- **IUpdateHandler** - Interface for progress reporting, used by both CLI (UpdateHandler) and GitWizardUI (MainViewModel).
- **Collection swap pattern** - `ApplyFilterAndGrouping()` builds a new `ObservableCollection` off-screen and swaps it in one shot to avoid per-item layout updates with 700+ repos.
- **UI command queue** - Background refresh threads enqueue `RepositoryUICommand` structs; a UI update thread drains them in batches on the main thread every 250ms.
- **RefreshStatus enum** - Per-repo status (Refreshing/Success/Timeout/Error) drives the status icon in column 0. `MarkRefreshFailed()` on `GitWizardRepository` handles timeout/error paths.
- **Journal watch (`-watch`, Windows only, CLI)** - Auto-detects changes in tracked repositories via MFTLib's `VolumeBroker` (`JournalBrokerClient`/`JournalBrokerHost`), one UAC prompt for the whole run. `Program.cs` dispatches MFTLib's elevated `--broker` child mode via `ElevatedEntryPoint.TryHandle` before any other startup work (see `Program.Watch.cs`'s `TryHandleElevatedBrokerEntry` seam); `-watch` itself groups tracked repository roots by drive, arms one broker client's cold scan + live watch across those drives, and prints `changed: <root>` as journal batches arrive. `GitWizard.RepositoryChangeFilter` (pure, cross-platform-testable) maps a drive's cold-scan `ScanRecord`s to repository roots by MFT record number, then filters live `UsnJournalEntry` batches down to the distinct repos they touched (parent-record match, or the record's own number for a directory entry; longest-root-first so a nested repo wins over its container). Not wired into GitWizardUI.

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
    "https://gitea.llamabox.sticktoitive.net/api/v1/repos/schoen/git-wizard/actions/workflows/screenshot.yml/dispatches"
```

The workflow runs on `windows-latest`, captures the screenshot using Avalonia headless rendering, and uploads `GitWizardUI.png` as a workflow artifact. Download the artifact and commit it to `Screenshots/`.

## Release checklist

1. Update `<Version>` in `GitWizardUI/GitWizardUI.csproj`
2. Update version in CLI help text in `git-wizard/Program.cs`
3. Update screenshot: trigger CI workflow (see above) or run locally
4. Commit version bump, screenshot, and all pending changes
5. `git tag v0.x.y && git push origin main --tags`
6. CI builds and publishes the release with all artifacts attached. Watch `https://gitea.llamabox.sticktoitive.net/schoen/git-wizard/actions`. The release workflow's `publish-cross` job asserts the tag matches the csproj `<Version>` and fails fast on drift.

The release attaches: `git-wizard-{ver}-{rid}.zip` and `GitWizardUI-{ver}-{rid}.zip` for `rid in {win-x64, linux-x64, osx-x64}`. See `.gitea/workflows/release.yml` and the **CI infrastructure** section below.

## CI infrastructure

Workflows live in `.gitea/workflows/` (`ci.yml`, `release.yml`) - the YAML is the source of truth for jobs and steps. Operational setup that is *not* in the YAML:

- **Runners:** `ci.yml` splits across two self-hosted Gitea runners - `llamabox-ubuntu` (label `ubuntu-latest`, cross-platform build + the NUnit suite with coverage) and `llamabox-windows` (label `windows-latest`, full-solution build + all NUnit tests). Both runners run the test suite; only the Linux job collects coverage.
- **Coverage gate:** the Linux job runs `ci/post-coverage-status.py`, which (a) posts the informational `pr-crew/coverage` commit status, (b) writes a line/branch table to the job summary, and (c) **fails the job when line coverage drops below `--gate-line` (currently 45%)**. Re-baselined to the whole `GitWizardUI` assembly after the VM fold (2026-05-26) - the app's untestable Avalonia glue (Views/App/Program) now sits in coverage scope; ratchets up as ViewModel coverage grows (issue #36). The Cobertura XML is also uploaded as the `coverage-cobertura` artifact.
- **`ci-bot` identity:** the release workflow authenticates as a dedicated Gitea user `ci-bot` (reusable across personal repos). Provision on the Gitea host:
  ```bash
  sudo -u git gitea admin user create --username ci-bot --email ci-bot@llamabox.internal --random-password --must-change-password=false
  sudo -u git gitea admin user generate-access-token --username ci-bot --token-name git-wizard-ci --scopes write:repository --raw
  ```
  Add `ci-bot` as a **Write** collaborator on `schoen/git-wizard` (needed to create releases + upload assets). `write:repository` is the only scope required.
- **Secret:** store `ci-bot`'s PAT as the repo secret **`CI_GITEA_TOKEN`** (Settings → Actions → Secrets). `release.yml` reads `${{ secrets.CI_GITEA_TOKEN }}`. To rotate: re-run `generate-access-token` and re-paste the value - no code change.
- **Branch protection** (`main`, configured in the Gitea UI, not in any file): require status checks `build-linux` + `test-windows`, require up-to-date branches, restrict force-pushes. Configure *after* the first green CI run so you don't lock yourself out.
- **Deliberate non-goals:** no binary signing, no MAUI (retired 2026-05-26 - GitWizardUI is the Avalonia app, covering cross-platform desktop), and no macOS CI runner.
- **Cert workaround:** both workflows set `NODE_TLS_REJECT_UNAUTHORIZED=0` because the Windows runner's Node doesn't trust the self-signed llamabox Caddy cert. Tracked for removal in `PLAN.md` → Infrastructure.

### Preview builds (`/preview`)

`/preview` on a git-wizard PR (pr-crew comment command or the ops-dashboard button) builds and runs that PR's `GitWizardUI` on the platform you ask for. Default platform is **windows**; `/preview linux` targets llamabox.

- **Windows** (`.gitea/workflows/preview.yml`, dispatched on `windows-latest`): MSVC native build → self-contained `win-x64` publish → zip → upload to the gitea generic package registry at `…/packages/schoen/generic/git-wizard-pr-<N>/<head-sha>/GitWizardUI-windows-x64.zip` (auth: `CI_GITEA_TOKEN`). The upload is existence-guarded so a re-dispatch for the same SHA is a no-op.
- **Linux** (`.preview/up`, run by pr-crew on llamabox in a detached PR-head worktree): CMake native build → self-contained `linux-x64` publish → zip → registry upload (auth: `PREVIEW_GITEA_TOKEN`). If a live graphical session exists it launches the app on llamabox's hyprland display via XWayland — GitWizardUI is Avalonia 11.2 (X11-only on Linux), so `DISPLAY` is the load-bearing var (`kind: "app"`, PID in `$PREVIEW_PID_FILE`); otherwise it posts artifact links only (`kind: "artifact"`). `.preview/down` kills the launched process.
- **Run a published artifact yourself:** `scripts/run-preview.ps1 [<PR#>]` (Windows) or `scripts/run-preview.sh [<PR#>]` (Linux). No arg fetches the newest git-wizard preview package; both download the current-OS zip from the registry, unzip to a temp dir, and launch `GitWizardUI`. Set `GITEA_TOKEN` if anonymous registry reads are refused.

Enrollment (adding git-wizard to pr-crew's `[preview]` config on llamabox) is an operator step, not in this repo — see the plan's deploy notes.

## Tips

- `deepRefresh` parameter skips expensive `git update-index --refresh` during auto-refresh; per-repo ↻ button triggers it on demand
- **Avalonia commands:** the view models expose a *custom* `GitWizardUI.ViewModels.ICommand` (not `System.Windows.Input.ICommand`), so Avalonia `Button.Command="{Binding ...}"` silently ghosts/disables the button. Wire Avalonia buttons with `Click=` handlers that call `command.Execute(null)` - the whole app follows this convention.
- **Avalonia bindings don't apply the target property's `TypeConverter`** - binding a *string* to a `Thickness` property - e.g. `RepositoryNodeViewModel.ItemPaddingString` → `Padding` - throws `InvalidCastException` on every realized row during scroll. Avalonia's default converter *does* handle string→primitive, string→enum (`FontWeight`), and string→`IBrush` (`StatusColorHex`→`Foreground`), but NOT `Thickness`. Use an explicit `IValueConverter` (`StringToThicknessConverter`) and keep the VM framework-agnostic (string in, convert in the view).
- **`LibGit2Sharp.Repository.IsValid(path)` is not fully non-throwing:** it returns `false` for a plain non-repo directory, but *throws* `LibGit2SharpException` for other conditions - notably git's "repository path ... is not owned by current user" ownership protection. `GitWizardRepository.Refresh` guards `new Repository(...)` with `IsValid` to skip stale/non-repo cache entries without spamming first-chance `RepositoryNotFoundException`, but that check lives **inside** the existing try so ownership/other errors still fall through to the catch as a normal refresh error (`RefreshConcurrencyTest` covers this). Don't hoist the `IsValid` call outside the try.
- **Refresh vs discovery/UAC:** a normal GUI Refresh re-checks the cached repo list (`~/.GitWizard/repositories.txt`) and never runs MFT discovery or self-elevates. MFT discovery (and the UAC prompt) fire only when the cache is absent. Shift+click Refresh = hard refresh (clear cache + rediscover) is the GUI's way to force discovery/UAC.
- **Avalonia UI updates:** `MainViewModel.SendUpdateMessage` coalesces status posts (at most one UI update in flight) so a large/recursive scan can't flood the dispatcher and wedge the window ("Not Responding"). Don't revert it to `_ui.Post` per message.
- **Refresh runs `ApplyFilterAndGrouping` off the UI thread:** `RefreshAsync` awaits with `ConfigureAwait(false)`, so its tail (incl. `ApplyFilterAndGrouping` and the `Repositories` swap) runs on a thread-pool thread. Avalonia *tolerates* the off-thread collection-property swap, but visual-tree access (e.g. `ScrollViewer.Offset`) calls `VerifyAccess()` and throws `Call from invalid thread`. The swap + its `AfterRepositoriesSwap` hook are therefore marshaled to the UI thread via `_ui.Post` when `!_ui.IsOnUiThread` (the off-screen build stays off-thread for the 700-repo perf win). Keep any UI-touching swap work inside that marshaled block.
- **Scroll preservation on refresh (RESOLVED - `MainWindow.RestoreScrollAnchor`):** the `Repositories` swap resets the `ListBox` to the top. Restoring is NOT a one-shot `ScrollViewer.Offset` set - under `VirtualizingStackPanel` the scrollable extent is an *estimate* that's wrong until layout settles (AvaloniaUI/Avalonia#17460/#17848), so a single offset set lands against a stale extent and is silently clamped (the old symptom: "scroll did nothing"). The working recipe is a **closed loop**: `ScrollIntoView(anchor)` *once* to materialize it, then a bounded `DispatcherTimer` measures the anchor row's actual Y each layout pass and corrects `offset += containerY + above` until it reaches the top (converges in 1-2 passes). Don't re-call `ScrollIntoView` each pass (it re-aligns a partly-off-top row and fights the nudge → oscillation). `ScrollIntoView` alone does NOT top-align, and a two-step ScrollIntoView trick is unreliable with variable row heights. Validated headless + real window by the `avalonia-vsp-scroll-top` spike (`~/.claude/notes/spike_avalonia-vsp-scroll-top.md`). **Headless does NOT reproduce the bug** (a single offset set sticks when layout is pumped synchronously) - validate scroll/extent timing in a real `--headed` window.
- The during-refresh jump-to-top (list empties + streams back from row 0) is a *separate, accepted* behavior, not the swap reset: `RefreshAsync` `Repositories.Clear()`s at the start (line ~1005) and `AddRepository` streams rows back via `Repositories.Add`. Keeping scroll through the whole scan would mean not mutating the visible list during refresh (losing live per-row feedback) or an in-place diff (fights the sort/group atomic-swap perf path). Deliberately left as snap-back-after-refresh.
