# GitWizard

Scan, organize, and manage all the git repositories on your machine. Find duplicates, spot uncommitted work, and clean up old project copies.

![GitWizard UI](Screenshots/GitWizardUI.png)

## Features

- **Fast repository discovery** — uses NTFS MFT parsing on Windows for near-instant scanning across all drives
- **Status at a glance** — see pending changes, unpushed commits, and errors per repo
- **Filter** by pending changes, detached heads, submodule issues, or repos you've committed to
- **Group** by drive or remote URL to find duplicate clones
- **Sort** by path, last commit date, or remote URL
- **Search** to quickly find repos by path
- **Deep refresh** per repo with remote fetch and index update
- **Cross-platform desktop** — Avalonia app runs natively on Windows, macOS, and Linux; library and CLI run everywhere

## Getting Started

Download the latest release from the [Releases](https://github.com/mtschoen/git-wizard/releases) page, or build from source:

```bash
# CLI
dotnet build git-wizard/git-wizard.csproj

# Desktop GUI (Windows / macOS / Linux)
dotnet build GitWizardUI/GitWizardUI.csproj
```

On first run, configure your search paths in Settings. GitWizard will scan those paths for git repositories and cache the results for fast subsequent launches.

## Projects

| Project | Description |
|---------|-------------|
| **GitWizard/** | Core class library (cross-platform) |
| **git-wizard/** | CLI tool |
| **GitWizardUI/** | Avalonia desktop app (Windows, macOS, Linux); view models under `ViewModels/` |

## License

See [LICENSE](LICENSE) for details.
