# GitWizard

Multi-project solution for scanning and reporting on git repositories.

## Projects

- **GitWizard/** — Core class library (cross-platform: Windows, macOS, Linux)
- **git-wizard/** — CLI tool
- **GitWizardUI/** — .NET MAUI desktop app (Windows, macOS, Android)

## Build

```bash
# CLI
dotnet build git-wizard/git-wizard.csproj

# MAUI UI (Windows)
dotnet build GitWizardUI/GitWizardUI.csproj -f net10.0-windows10.0.19041.0

# Publish MAUI (creates Releases/GitWizardUI-{version}.zip automatically)
dotnet publish GitWizardUI/GitWizardUI.csproj -f net10.0-windows10.0.19041.0 -c Release
```

## Key Architecture

- **MFTLib** — Used on Windows for fast .git discovery via NTFS MFT parsing. Requires elevation; the app self-elevates via UAC when needed.
- **Self-elevation** — Both CLI and MAUI handle `--elevated-mft` and `--elevated-defender` hidden args for child process elevation.
- **ElevatedProcessHelper** — Manages launching self as admin with `Verb = "runas"`.
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

VS2026 is also installed at `C:/Program Files/Microsoft Visual Studio/2022/Community/` for interactive debugging when CLI tools aren't enough.

## Screenshots

The UI test project captures screenshots automatically:

```bash
# Capture a screenshot (builds, launches app, captures window, kills app)
dotnet test GitWizardUI.UITests/GitWizardUI.UITests.csproj --filter "FullyQualifiedName~CaptureMainWindowScreenshot"
```

Screenshots are saved to `Screenshots/GitWizardUI.png`. The test uses Win32 `PrintWindow` API for accurate capture including DPI scaling.

## Release checklist

1. Update `ApplicationDisplayVersion` in `GitWizardUI/GitWizardUI.csproj`
2. Update version in CLI help text in `git-wizard/Program.cs`
3. Update screenshot: `dotnet test GitWizardUI.UITests/...` (see above)
4. Commit version bump, screenshot, and all pending changes
5. `dotnet publish` creates zip automatically via MSBuild target (also deletes old zip from `Releases/`)
6. `git add` the new zip AND the deleted old zip, then commit
7. Tag (e.g., `v0.4.0`)
8. Push with tag: `git push origin main --tags`
9. Create GitHub release with `gh release create`, attach the zip

## Tips

- To kill GitWizardUI from bash (Git Bash mangles `/IM`): `cmd.exe //c "taskkill /IM GitWizardUI.exe /F"`
- MAUI CollectionView scroll fix: `ItemsUpdatingScrollMode="KeepScrollOffset"` — without this, adding items resets scroll to top
- Avoid `IsEnabled` binding on sidebar buttons during refresh — CollectionView handles concurrent updates fine now
- `deepRefresh` parameter skips expensive `git update-index --refresh` during auto-refresh; per-repo ↻ button triggers it on demand
