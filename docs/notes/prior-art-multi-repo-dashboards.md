# Prior art — multi-repo dashboards & related tooling

Captured 2026-05-05 during a brainstorm for `schoen-fleet` (cross-host project state sync, projdash issue #7). Notes filed here because GitWizard sits in the part of the ecosystem most exposed to this prior art — the "scan many repos, surface dirty/unpushed state" niche is crowded.

## Tools that overlap with GitWizard's niche (single-host multi-repo state)

| Tool | Shape | Overlap with GitWizard |
|---|---|---|
| [`multi-git-status`](https://github.com/fboender/multi-git-status) | Terminal report of uncommitted/untracked/unpushed across repos | Direct — same job, less batched, no JSON cache |
| [`git-dashboard` (kojung)](https://github.com/kojung/git-dashboard) | GUI grid of repo statuses | UI shape differs; data layer is the same problem |
| [`Git-Dashboard` (jplflyer)](https://github.com/jplflyer/Git-Dashboard) | "Leave on a spare monitor" GUI | Live-updating ambient view |
| [`git-dash` (jvm)](https://github.com/jvm/git-dash) | Fast TUI, repo discovery + management | TUI competitor |
| [`gitpane`](https://www.linuxlinks.com/gitpane-multi-repo-git-workspace-dashboard/) | Workspace-level multi-repo terminal view | Terminal competitor |
| [`gita`](https://github.com/nosarthur/gita) | CLI for many repos, csv-config | Multi-repo CLI; config sync via shared csv |
| [`myrepos` / `mr`](https://gist.github.com/rmi1974/9e06453f1db1b9327933ea5510a97522) | `mr` command, `~/.mrconfig` | Multi-repo VCS wrapper |
| [`mrgit`](https://github.com/cksource/mrgit) | Multi-repo project mgmt | Build-oriented multi-repo |
| [`Repo Dashboard`](https://albertoroura.com/repo-dashboard-local-github-visibility-tool/) | Local-first GitHub aggregator | Pulls from GitHub API; less local-fs-driven |

**Takeaway for GitWizard:** the "dirty/unpushed across N repos" job is well-trodden. GitWizard's distinctive moves vs. this set:
- Caches results to `~/.GitWizard/report.json` (most competitors recompute on each invocation)
- Schema 1.1 includes `LocalCommitCount` (real unpushed count, not just a bool) — most tools only do bool dirty/clean
- Cross-tool contract: projdash already consumes `report.json` as a faster-than-subprocess data source. That's a niche none of the above occupy.
- Hybrid walk + caps for `scan_temp_files` (recent fix in projdash) — GitWizard's scan policy is where some of these dashboards struggle on large trees.

## Tools adjacent to projdash (lifecycle / task / "side project" angle)

These don't overlap with GitWizard but are worth knowing exist:

- [`STACKFOLO`](https://dev.to/stackfolo/how-i-track-github-commits-across-all-my-side-projects-in-one-place-5a89) — multi-repo GitHub commit timeline. GitHub-only, no local scanning.
- [hoatrinhdev's CLI](https://dev.to/hoatrinhdev/i-kept-abandoning-side-projects-so-i-built-a-tool-to-fix-the-real-problem-451n) — shell-hooked context recovery: on `cd` into a project, echoes last checkpoint. Different feature (context, not status).
- [`bin-expire`](https://github.com/Yashb404/bin-expire) — Rust CLI for stale *binaries*. Wrong target (artifacts, not project dirs), but the staleness-scoring shape is similar.
- ["Side Project Graveyard" (Evan Frawley)](https://evan.gg/blog/side-project-graveyard) — concept piece, not a tool.

No tool I could find ships projdash's combination of: PLAN.md/TODO.md adapters that read AND write, lifecycle states, classifications, MCP tool surface for LLM-driven workflows.

## Cross-host state exchange (the schoen-fleet space)

I tried hard to find a personal-scale tool that does *peer-to-peer state exchange* between machines for a project list and couldn't.

- `gita` and `myrepos` sync via *config files* you commit and pull manually.
- `Repo Dashboard` aggregates GitHub API state, not local-filesystem state.
- llamalab's gossip layer (in `~/llamalab/src/llamalab/dashboard/gossip.py`) is the only thing in this house that does it, and it's domain-specific to LLM model libraries.

The cross-host gossip part of schoen-fleet appears to be a genuine gap, not a re-tread.

## Pointers GitWizard may want to revisit

- If GitWizard ever grows a "compare git state across machines" feature, the `schoen-fleet` library is the right transport. GitWizard would gossip `report.json` snapshots, projdash would gossip its metadata, sync-memory could gossip the notes/ tree.
- The `multi-git-status` output format is very close to what `git-wizard --paths` produces — a quick competitive read of how others format the same data could inform a future GitWizard CLI flag.
