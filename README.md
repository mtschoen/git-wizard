# GitWizard

Scan, organize, and manage all the git repositories on your machine. Find duplicates, spot uncommitted work, and clean up old project copies.

![GitWizard UI](Screenshots/GitWizardUI.png)

## Features

- **Fast repository discovery** - uses NTFS MFT parsing on Windows for near-instant scanning across all drives
- **Status at a glance** - see pending changes, unpushed commits, and errors per repo
- **Filter** by pending changes, detached heads, submodule issues, or repos you've committed to
- **Group** by drive or remote URL to find duplicate clones; drives group by letter regardless of path case
- **Sort** by path, last commit date, remote URL, or size on disk. Working Directory sorts paths in ascending order (case-insensitive on Windows, ordinal elsewhere), including within groups, and keeps arriving refresh and Live rows in path order
- **Repository sizes** appear beside every row after measurement, with totals on group headers
- **Search** to quickly find repos by path
- **Deep refresh** per repo with remote fetch and index update
- **Live mode** - opt-in repository monitoring (Windows USN journal, Linux fanotify with kernel 5.9+) updates changed, created, deleted, and renamed repositories after one elevation prompt
- **Cross-platform desktop** - Avalonia app runs natively on Windows, macOS, and Linux; library and CLI run everywhere

## Getting Started

Download the latest release from the [Releases](https://github.com/mtschoen/git-wizard/releases) page, or build from source. GitWizard depends on [MFTLib](https://github.com/mtschoen/MFTLib), pulled in as a git submodule and built from source until it ships on NuGet - see `AGENTS.md` for the full per-platform build (native + managed toolchains):

```bash
git submodule update --init

# CLI
dotnet build git-wizard/git-wizard.csproj

# Desktop GUI (Windows / macOS / Linux)
dotnet build GitWizardUI/GitWizardUI.csproj
```

On first run, configure your search paths in Settings. GitWizard will scan those paths for git repositories and cache the results for fast subsequent launches.

### Windows discovery scope and caching

Windows MFT discovery gathers filtered metadata across each whole selected volume: directories
plus `.git` marker files. This can include other users' directory names, even outside your
configured search paths. Search and ignore settings filter the repository candidates after
that metadata is gathered. During Live monitoring, hydration may add metadata for observed
ordinary files. A process running as your user can read the mapped or backing metadata.

GitWizard selects no-cache mode for this index, so it adds no persistent inventory cache.
Temporary block files still touch disk: the operating system deletes them when the last handle
closes, including on process exit. The existing `~/.GitWizard/repositories.txt` repository list
and `~/.GitWizard/report.json` cached report still persist; index no-cache mode does not disable
them. Recursive discovery on Linux and with `--no-mft` is unchanged.

### Temp directories

Discovery (MFT and recursive) and Live monitoring ignore temp directories by default: every
profile's `AppData\Local\Temp` on Windows (any `<drive>:\Users\<name>\AppData\Local\Temp`), the
process's own `%TEMP%`/`%TMP%`, and `/tmp` plus `$TMPDIR` on Linux. CI agents and test suites
create hundreds of short-lived repositories there; tracking them floods the log with refresh
errors for repositories that no longer exist. Set `"includeTempDirectories": true` in
`~/.GitWizard/config.json` (or enable **Include temp directories** in Settings) to include
them again. Explicit ignored paths always apply, and a search path configured inside a temp
directory opts that subtree back in. A repository whose directory vanishes before its refresh
runs is logged at Debug instead of Error.

### Live monitoring

CLI `--watch` recursively discovers repositories under the configured search paths
on every start and replaces `repositories.txt` before arming the watch. Repositories
created since the previous cache write are therefore tracked without a rename.
Discovery respects the configured ignore, hidden-directory and temp-directory
policies. This adds filesystem-scan latency at each CLI watch start; it does not
launch an additional MFT broker or elevation prompt. An empty discovery clears the
repository-list cache and the CLI reports that there is nothing to watch.

On Windows, discovery and the transfer into Live share one UAC prompt when the prepared index
is reusable. A watch failure on one drive is reported while healthy drives stay live. A repository created under a configured search path while Live runs is discovered and tracked from that point on; Live does not need a restart for it. If a drive's cached index checkpoint fell out of the USN journal, Live shows a hint for that drive: a journal of at least the named size would have avoided this rescan, alongside the current maximum size. The named size is a lower bound, rounded up to the journal's allocation unit plus one more unit of margin. When that size is too large to represent, the hint reports the rescan without naming a size. When the journal was recreated (different journal id), the hint says so and offers no size. The same hint also appears the moment a running watch dies because the journal moved past the position the watch had reached: live updates for that drive stop, its index keeps answering queries, and the hint says a rescan of the drive is needed to catch up. A hint from a faulted watch replaces the drive's earlier one, while a watch that fails for any other reason leaves it as it was. If Live ends because no drives remain watchable, the hints stay on screen. CLI `--watch` prints each drive's hint at startup and again when it changes. Nothing is resized automatically.

While Live is running, Settings shows an **Enlarge** button next to each drive hint, enabled where a larger journal would help and otherwise disabled with the reason beside it. It uses the existing
elevated broker session without another broker or UAC prompt, rechecks the current size, and
refuses equal or smaller targets. The result shows the actual before and after sizes in bytes;
Windows may round the requested size upward. **Do not warn again** saves the volume serial in
`config.json` under `ignoredJournalWarnings`, so the preference follows the volume rather than
its drive letter. Re-arming refreshes each drive's hint. Stopping Live releases its resources;
restarting can require a new scan and elevation prompt. Linux fanotify monitoring is unchanged.

The green Live state means "the pump started", never "catch-up has drained". Changes can still
be catching up after Live appears active; see [MFTLib#139](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/139).

### Machine-readable safety sweep

Use the focused sweep mode before travel, migration, or machine maintenance:

```bash
git-wizard --sweep --print-minified
git-wizard --sweep --paths /work/repo-a,/work/repo-b
```

It emits stable JSON containing each repository path, dirty tracked files, local branches with commits absent from all local remote-tracking refs, and stash count. The sweep is read-only and does not fetch, so remote-tracking freshness depends on the machine's last fetch. See [the sweep schema](docs/report-schema.md#repository-sweep-output---sweep) and the local/SSH agent workflow in [skills-dev/sweeping-git-work](skills-dev/sweeping-git-work/SKILL.md).

### Fleet-wide tag and branch inventory

Scan every clone on the machine (including submodule checkouts, nested worktrees, and `~/.pr-crew/repos` when present):

```bash
# Inventory matching tags and verify whether they still exist on any remote
git-wizard tags -m 'archive/*'

# Inventory local branches divergent from default or base branch, with unique-commit counts
git-wizard branches --not-merged main
```

#### Example: Reap tag fleet-wide and prove nothing is left

```bash
# 1. Identify all clones holding the tag and whether remotes still have it
git-wizard tags -m 'archive/2026-01'

# 2. In each clone holding the tag, delete it locally (and on remote if needed)
git-wizard tags -m 'archive/2026-01' | jq -r '.Repositories[].Path' | while read -r repo; do
    git -C "$repo" tag -d archive/2026-01
done

# 3. Prove zero occurrences remain across all repositories and submodules
git-wizard tags -m 'archive/2026-01' --print-minified
# Output: {"SchemaVersion":"1.0","MatchPattern":"archive/2026-01","Repositories":[]}
```

See the [tag and branch inventory schemas](docs/report-schema.md#tag-inventory-output-tags) for schema documentation.

### Preview a PR build

`/preview` on a git-wizard PR builds and publishes the desktop app for Windows or Linux; `scripts/run-preview.ps1` / `scripts/run-preview.sh` download and launch a published preview. Set `GITEA_BASE_URL` to the Gitea origin before running either launcher. See **CI infrastructure → Preview builds** in [AGENTS.md](AGENTS.md).

## Projects

| Project | Description |
|---------|-------------|
| **GitWizard/** | Core class library (cross-platform) |
| **git-wizard/** | CLI tool |
| **GitWizardUI/** | Avalonia desktop app (Windows, macOS, Linux); view models under `ViewModels/` |
| **skills-dev/** | Agent skills developed alongside the CLI |

## License

See [LICENSE](LICENSE) for details.
