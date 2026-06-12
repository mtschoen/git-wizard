# GitWizard

Multi-project solution for scanning and reporting on git repositories.

## Projects

- **GitWizard/** - Core class library (cross-platform: Windows, macOS, Linux)
- **git-wizard/** - CLI tool
- **GitWizardUI/** - Avalonia cross-platform desktop app (Windows/macOS/Linux); contains the view models under `ViewModels/` (behind `IUiDispatcher` / `IUserDialogs` / `IFolderPicker`)
- **GitWizardUI.Screenshot/** - Headless Avalonia screenshot tool (used by CI)
- **GitWizardTests/** - NUnit test project

## Build

Plain `dotnet build` is the norm and works on Windows/macOS/Linux. **Currently** that works via a **TEMPORARY vendored MFTLib** bridge: prebuilt MFTLib `0.3.0` DLLs are checked in at `lib/MFTLib/` and referenced centrally in the repo-root `Directory.Build.targets` (managed `<Reference>` for all projects + a Windows native-DLL copy). This exists because MFTLib `0.3.0` isn't on NuGet yet; it's retired (swap to a `PackageReference`, delete `lib/MFTLib/` + `Directory.Build.targets`) when `0.3.0` ships - see `lib/MFTLib/README.md` and the **MFTLib Local Development** section. (Once it's a NuGet **PackageReference**, plain `dotnet build` keeps working because the package ships the native `MFTLibNative.dll` pre-built.)

```bash
# Whole solution
dotnet build git-wizard.slnx

# GUI desktop app
dotnet build GitWizardUI/GitWizardUI.csproj
```

The only time you need VS2026's MSBuild is when MFTLib is swapped to a local **ProjectReference** (MFTLib local dev - see below): the `dotnet` CLI can't build MFTLib's native `MFTLibNative.vcxproj` (it needs the v145 platform toolset). In that mode only:

```bash
"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" git-wizard/git-wizard.csproj -t:Build -p:Configuration=Debug -nologo -v:minimal
```

To **run the NUnit suite** in ProjectReference mode, build with MSBuild first, then run tests with `--no-build` - a plain `dotnet test` re-invokes the dotnet build and fails on the native `.vcxproj`. The pre-built native DLL only ships for **Release** (`C:\Users\mtsch\MFTLib\x64\Release\MFTLibNative.dll`), so build Release:

```bash
"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" GitWizardTests/GitWizardTests.csproj -t:Restore -p:Configuration=Release -nologo -v:minimal
"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" GitWizardTests/GitWizardTests.csproj -t:Build    -p:Configuration=Release -nologo -v:minimal
dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release --nologo
```

## Linting / lint gate

The bar is **0 findings** across every configured analyzer (see `TEST-REPORT.md` → Lint gate). Three tiers:

- **Analyzers + naming (CA*/IDE*/RCS*)** - `Directory.Build.props` sets `EnableNETAnalyzers`, `AnalysisLevel=latest-Recommended`, `EnforceCodeStyleInBuild`, and **`TreatWarningsAsErrors`**, so a normal `dotnet build`/MSBuild build *is* the gate (Roslynator 4.15.0 included). `EnforceCodeStyleInBuild` also turns the `.editorconfig` naming ruleset (IDE1006, at `warning`) into a **build error** - so the editorconfig is the **authoritative naming gate**, not just an IDE hint. MFTLib (outside this tree) does not inherit these.
- **Formatting + naming** - `.editorconfig` is the canonical fleet C# standard (source of truth: `~/.claude/notes/idioms_csharp_naming.md`): **utf-8 (no BOM)**, CRLF, 4-space, and the full naming ruleset at `warning` → build error (PascalCase types/members/const/static-readonly; `_camelCase` **all** private/internal fields incl. mutable static; camelCase params/locals; no `m_`/`k_`/`s_`). Formatting/charset check: `dotnet format git-wizard.slnx --verify-no-changes`; apply: `dotnet format git-wizard.slnx`. A local on-save hook (`.claude/scripts/format-on-save.ps1`, wired in `settings.local.json`) runs whitespace formatting per edit. (`dotnet format` fixes charset/whitespace/style but **cannot** auto-fix IDE1006 naming - rename via Rider/`jb cleanupcode`.)
- **Deep inspection** - `~/.dotnet/tools/jb.exe inspectcode git-wizard.slnx -o="$TEMP/jb.xml" --settings="git-wizard.slnx.DotSettings" --severity=WARNING --no-updates`, gated by `ci/parse-jb-report.py` (exits non-zero on any finding). Match ci.yml's invocation exactly - omitting `--settings`/`--severity` reports hundreds of suggestion-tier findings the gate never sees. Now a *supplementary* deep pass - the editorconfig (above) is the primary naming gate. **jb-CLI binds `InconsistentNaming` to the machine's *global* ReSharper config, not the solution `.DotSettings`** - the global config here is aligned to the canonical convention; Rider uses the solution `.DotSettings` (which has the ExtraRules jb-CLI ignores).
- **aislop** (AI-slop gate) - `.gitea/workflows/aislop.yml` gates the whole repo (C# + Python) at `failBelow: 100` in `.aislop/config.yml`. The C# engine lives **only in the personal fork `github.com/mtschoen/aislop`** (the public npm `aislop` scores just the Python `ci/` tooling - 2 files), so the workflow clones + `npm install`-builds that fork at a **pinned commit** (`env.AISLOP_FORK_COMMIT`) and runs the built `dist/cli.js`; it also `dotnet tool install`s `roslynator.dotnet.cli` (the C# lint engine shells out to it) and sanity-fails if the scan covers <10 files (catching a silent Python-only pass). The repo scores **100/100 (0 findings across every engine)**; `failBelow: 100` enforces the same 0-findings bar as the other analyzers. Findings map to fixes, not mutes: oversized files split into partials, long functions extracted, TODOs tracked as gitea issues, narrative comments reworded to trip aislop's why-marker exemption. Bump the pinned commit deliberately (don't track the moving `schoen/main`).

CI wires all three (`.gitea/workflows/ci.yml`). It is now **active**: the TEMPORARY vendored MFTLib bridge (`lib/MFTLib/` + `Directory.Build.targets`, see the **Build** section) lets the runners build the solution with plain `dotnet build` - no MFTLib source or native toolchain needed. Both jobs (Linux + Windows) restore/build/test/format-check green locally with the `dotnet` CLI. (Before vendoring, CI was dormant/red-by-design because the committed local `ProjectReference` to a sibling MFTLib checkout couldn't be restored on the runners.)

## MFTLib Local Development

MFTLib source lives at `C:\Users\mtsch\MFTLib`. git-wizard depends on MFTLib `0.3.0` (the `IElevationProvider` API), which is **not on NuGet yet**. There are **three reference modes**:

1. **VENDORED (committed default).** Prebuilt MFTLib `0.3.0` DLLs are checked in at `lib/MFTLib/` and referenced centrally by the repo-root `Directory.Build.targets` - a managed `<Reference>` for every project (a raw assembly reference is non-transitive, and the CLI/UI expose `IElevationProvider` through GitWizard API signatures, so they'd hit CS0012 without it) plus a Windows native-DLL copy into each output. Plain `dotnet build`/`dotnet test` work everywhere; **this is what CI uses and what you commit.** TEMPORARY - retire when `0.3.0` ships (see `lib/MFTLib/README.md`).

2. **PUBLISH (`PackageReference`).** When MFTLib `0.3.0` ships to NuGet: delete `lib/MFTLib/` + `Directory.Build.targets`, add `<PackageReference Include="MFTLib" Version="0.3.0" />` to `GitWizard/GitWizard.csproj` (flows transitively; the package ships the native DLL). The `BlockVendoredMFTLibOnPublish` target in `Directory.Build.targets` errors on any `dotnet publish` while the vendored reference is active, to stop a vendored DLL shipping in a release.

3. **LOCAL MFTLib DEV (`ProjectReference`).** To iterate on MFTLib source: in `Directory.Build.targets` comment out the vendored `<Reference>`, and add to `GitWizard/GitWizard.csproj`:
   ```xml
   <ProjectReference Include="..\..\MFTLib\MFTLib\MFTLib.csproj" SetPlatform="Platform=x64" />
   ```
   **`SetPlatform="Platform=x64"`** is required (MFTLib defines only x64/x86; git-wizard is AnyCPU - without it MSBuild looks for the ref assembly at the wrong obj path: "Metadata file could not be found"). This mode needs **VS2026 MSBuild** (the `dotnet` CLI can't build the native `.vcxproj`); build Release and `dotnet test --no-build` (see the Build section). To re-vendor after MFTLib changes: rebuild MFTLib Release (`-p:Platform=x64`) and copy `MFTLib/bin/x64/Release/net8.0/MFTLib.dll` + `MFTLibNative/x64/Release/MFTLibNative.dll` into `lib/MFTLib/` (+ `win-x64/`). **Swap back to mode 1 before committing.**

## Key Architecture

- **MFTLib** - Used on Windows for fast .git discovery via NTFS MFT parsing. Requires elevation; the app self-elevates via UAC when needed.
- **MFTLib.ElevationUtilities** - Elevation helpers from MFTLib: `IsElevated()`, `CanSelfElevate()`, `TryRunElevated(args, timeoutMs)`. Used by GitWizardApi for MFT scanning and WindowsDefenderException for adding exclusions.
- **Self-elevation** - Both CLI and GitWizardUI handle `--elevated-mft` and `--elevated-defender` hidden args for child process elevation.
- **IUpdateHandler** - Interface for progress reporting, used by both CLI (UpdateHandler) and GitWizardUI (MainViewModel).
- **Collection swap pattern** - `ApplyFilterAndGrouping()` builds a new `ObservableCollection` off-screen and swaps it in one shot to avoid per-item layout updates with 700+ repos.
- **UI command queue** - Background refresh threads enqueue `RepositoryUICommand` structs; a UI update thread drains them in batches on the main thread every 250ms.
- **RefreshStatus enum** - Per-repo status (Refreshing/Success/Timeout/Error) drives the status icon in column 0. `MarkRefreshFailed()` on `GitWizardRepository` handles timeout/error paths.

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
