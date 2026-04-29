# GitWizard

Multi-project solution for scanning and reporting on git repositories.

## Projects

- **GitWizard/** — Core class library (cross-platform: Windows, macOS, Linux)
- **git-wizard/** — CLI tool
- **GitWizardUI/** — .NET MAUI desktop app (Windows, macOS, Android)

## Build

Use VS2026's MSBuild (not `dotnet build`) when building projects that depend on MFTLib, because MFTLib includes a native C++ project that requires the v145 platform toolset:

```bash
# CLI (via MSBuild — required when using MFTLib ProjectReference)
"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" git-wizard/git-wizard.csproj -t:Build -p:Configuration=Debug -nologo -v:minimal

# MAUI UI (via MSBuild)
"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" GitWizardUI/GitWizardUI.csproj -t:Build -p:Configuration=Debug -nologo -v:minimal

# Publish MAUI (creates Releases/GitWizardUI-{version}.zip automatically)
dotnet publish GitWizardUI/GitWizardUI.csproj -f net10.0-windows10.0.19041.0 -c Release
```

`dotnet build` works when MFTLib is a NuGet PackageReference (the native DLL comes pre-built in the package), but fails when using a local ProjectReference because `dotnet` CLI can't build the MFTLibNative.vcxproj C++ project.

## MFTLib Local Development

MFTLib source lives at `C:\Users\mtsch\source\repos\MFTLib`. To iterate on MFTLib changes locally without publishing a new NuGet package, swap the PackageReference to a ProjectReference in `GitWizard/GitWizard.csproj`:

```xml
<!-- NuGet (normal — use for commits and publishing) -->
<PackageReference Include="MFTLib" Version="0.2.0" />

<!-- Local development (swap in to test MFTLib changes) -->
<ProjectReference Include="..\..\..\source\repos\MFTLib\MFTLib\MFTLib.csproj" SetPlatform="Platform=x64" />
```

**SetPlatform="Platform=x64"** is required because MFTLib only defines x64/x86 platforms, while git-wizard projects use AnyCPU. Without it, MSBuild looks for MFTLib's ref assembly at the wrong obj path and fails with "Metadata file could not be found."

The native DLL (MFTLibNative.dll) is handled by a `None Include` item in the csproj that copies it from MFTLib's build output. This only activates when the DLL exists at `../../../source/repos/MFTLib/x64/$(Configuration)/MFTLibNative.dll`, so it's harmless when using the NuGet package.

**Important:** The csproj has a `BlockLocalMFTLibOnPublish` target that errors if you try to `dotnet publish` with a ProjectReference to MFTLib. Always swap back to the PackageReference before publishing.

**Swap checklist:**
1. Change the reference in `GitWizard/GitWizard.csproj` (comment one, uncomment the other)
2. Build with VS2026 MSBuild (see Build section above)
3. Swap back to PackageReference before committing

## Key Architecture

- **MFTLib** — Used on Windows for fast .git discovery via NTFS MFT parsing. Requires elevation; the app self-elevates via UAC when needed.
- **MFTLib.ElevationUtilities** — Elevation helpers from MFTLib: `IsElevated()`, `CanSelfElevate()`, `TryRunElevated(args, timeoutMs)`. Used by GitWizardApi for MFT scanning and WindowsDefenderException for adding exclusions.
- **Self-elevation** — Both CLI and MAUI handle `--elevated-mft` and `--elevated-defender` hidden args for child process elevation.
- **IUpdateHandler** — Interface for progress reporting, used by both CLI (UpdateHandler) and MAUI (MainViewModel).
- **CollectionView** — UI uses MAUI CollectionView (not TreeView/UraniumUI) with `ItemsUpdatingScrollMode="KeepScrollOffset"` to prevent scroll reset during refresh. Group expand/collapse is simulated by inserting/removing children in a flat list.
- **Collection swap pattern** — `ApplyFilterAndGrouping()` builds a new `ObservableCollection` off-screen and swaps it in one shot to avoid per-item layout updates with 700+ repos.
- **UI command queue** — Background refresh threads enqueue `RepositoryUICommand` structs; a UI update thread drains them in batches on the main thread every 250ms.
- **RefreshStatus enum** — Per-repo status (Refreshing/Success/Timeout/Error) drives the status icon in column 0. `MarkRefreshFailed()` on `GitWizardRepository` handles timeout/error paths.

## Local files

GitWizard stores config and cache in `~/.GitWizard/`:
- `config.json` — Search paths, ignored paths
- `repositories.txt` — Cached repo list
- `report.json` — Cached report

## Debugging

.NET diagnostic CLI tools are installed globally:
- `dotnet-dump collect -p <pid>` — capture a dump of a hung/crashed process
- `dotnet-dump analyze <dump>` — inspect threads, stacks, exceptions (`pe` for last exception, `clrstack` for managed stacks)
- `dotnet-stack report -p <pid>` — quick stack trace of all threads without killing the process

Use these to debug crashes/freezes instead of guessing. For the MAUI UI, run the exe in the background, get its PID, and collect a dump if it hangs.

VS2026 is installed at `C:/Program Files/Microsoft Visual Studio/18/Community/` for interactive debugging when CLI tools aren't enough.

## Screenshots

The UI test project captures screenshots automatically:

```bash
# Capture a screenshot (builds, launches app, captures window, kills app)
dotnet test GitWizardUI.UITests/GitWizardUI.UITests.csproj --filter "FullyQualifiedName~CaptureMainWindowScreenshot"
```

Screenshots are saved to `Screenshots/GitWizardUI.png`. The test uses Win32 `PrintWindow` API for accurate capture including DPI scaling.

## Release checklist

1. Update `ApplicationDisplayVersion` and `PackageVersion` in `GitWizardUI/GitWizardUI.csproj`
2. Update version in CLI help text in `git-wizard/Program.cs`
3. Update screenshot: `dotnet test GitWizardUI.UITests/...` (see above)
4. Commit, then `git tag v0.x.y && git push origin main --tags`
5. CI builds and publishes the release with all artifacts attached. Watch `https://gitea.llamabox.internal/schoen/git-wizard/actions`. The release workflow's first step asserts the tag matches `ApplicationDisplayVersion` and fails fast on drift.

The release attaches: `git-wizard-{ver}-{rid}.zip` and `GitWizardAvalonia-{ver}-{rid}.zip` for `rid in {win-x64, linux-x64, osx-x64}`, plus `GitWizardUI-{ver}.zip` (MAUI Windows). See `.gitea/workflows/release.yml` and `docs/superpowers/specs/2026-04-28-gitea-ci-design.md`.

## Tips

- To kill GitWizardUI from bash (Git Bash mangles `/IM`): `cmd.exe //c "taskkill /IM GitWizardUI.exe /F"`
- MAUI CollectionView scroll fix: `ItemsUpdatingScrollMode="KeepScrollOffset"` — without this, adding items resets scroll to top
- Avoid `IsEnabled` binding on sidebar buttons during refresh — CollectionView handles concurrent updates fine now
- `deepRefresh` parameter skips expensive `git update-index --refresh` during auto-refresh; per-repo ↻ button triggers it on demand
