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

## Local files

GitWizard stores config and cache in `~/.GitWizard/`:
- `config.json` — Search paths, ignored paths
- `repositories.txt` — Cached repo list
- `report.json` — Cached report

## Release process

1. Update `ApplicationDisplayVersion` in `GitWizardUI/GitWizardUI.csproj`
2. Update version in CLI help text in `git-wizard/Program.cs`
3. Commit, tag (e.g., `v0.3.0`), push with tag
4. `dotnet publish` creates zip automatically via MSBuild target
5. Create GitHub release with `gh release create`, attach the zip
6. Check in the release zip to `Releases/`
