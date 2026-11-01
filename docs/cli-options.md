# CLI options

Run `git-wizard --help`, `git-wizard tags --help`, `git-wizard branches --help`
or `git-wizard prune --help` for command-specific usage. This is the canonical
flag list. `AgentInstructionsTests` checks it against parser source and help.

## Root command

| Flag | Meaning |
| --- | --- |
| `--help` | Print usage (`-h`, `-?`). |
| `--version` | Print the version inherited from `Directory.Build.props`. |
| `--verbose` | Enable verbose logging (`-v`). |
| `--silent` | Suppress console logging (`-s`). |
| `--rebuild-report` | Build the report instead of loading the cached report. |
| `--no-refresh` | Print cached report without refreshing repositories. |
| `--rebuild-repo-list` | Discover current membership instead of reading the cached list. |
| `--rebuild-all` | Rebuild both list and report. |
| `--setup-defender` | Configure Windows process-name exclusions, with UAC. |
| `--scan-only` | Print discovered paths and exit; implies membership rebuild. |
| `--no-mft` | Select uncached WalkOnly indexes, without MFT scanning, elevation or NTFS journal watch. |
| `--no-local-commit-count` | Skip per-branch local commit counting. |
| `--json` | Print the ordinary report as strict JSON. |
| `--sweep` | Print automation JSON for tracked dirty files, unique branch commits and stashes. |
| `--merge` | Merge explicit paths into the report at the required save path. |
| `--clear-cache` | Delete cached list/report, preserving config and index blocks. |
| `--clear-index-cache` | Clear unlocked supported index blocks and exit; preserve lock files. |
| `--index-cache-status` | Inspect cache policy and exit; cannot combine with deletion flags. |
| `--delete-all-local-files` | Refuse if the index subtree exists; otherwise delete local files. |
| `--print-minified` | Compact JSON for automation commands only. |
| `--minified` | Compact JSON output; ordinary reports require `--json`. |
| `--db-size` | Print local-files directory size and exit. |
| `--all-branches` | Include clean/merged/no-work branches. |
| `--watch` | Stream repository changes and readiness; verbose adds timestamps and drive/startup detail. |
| `--filter` | Case-insensitive path substring (`-f <PATTERN>`). |
| `--paths` | Explicit repositories: newline-separated file or comma-separated list (`-p <PATHS>`). |
| `--save-path` | Write report to the supplied path. |
| `--config-path` | Read the supplied configuration file. |

## Inventory subcommands

`tags`, `branches` and `prune` share paths, filter, save-path, config-path, print-minified,
no-mft, verbose, silent and help from the table above.

| Flag | Command | Meaning |
| --- | --- | --- |
| `--match` | tags | Tag glob (`-m`), default `*`. |
| `--not-merged` | branches | Keep branches not merged into the supplied branch (default repository branch if omitted). |
| `--not` | branches | Alias of not-merged. |
| `--local-only` | branches | Keep unmerged branches with commits on no remote-tracking ref, and unmerged stashes. |
| `--branches` | prune | Consider local branches (neither flag means branches and stashes). |
| `--stashes` | prune | Consider stashes (report-only; neither flag means branches and stashes). |
| `--apply` | prune | Delete eligible branches (stashes are report-only); without it prune is a dry run. |
| `--base` | prune | Base branch to compare against (default: origin's default branch, then local main/master/develop). |

Implementation: `git-wizard/CliParser.cs`, `CliParser.Subcommands.cs` and
`RunConfiguration.cs`. Elevated child dispatch is internal and is not a user option.
