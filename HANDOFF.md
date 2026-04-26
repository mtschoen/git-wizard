# git-wizard — handoff

**Status:** Avalonia cross-platform UI implementation **COMPLETE**.
All 18 tasks committed across 17 commits (c29e7bc → 3ae1953).

**Host:** llamabox (Linux). The plan is Linux-first; MAUI build verification on
Windows is deferred to a manual pass after Avalonia parity lands.

**Known remaining items:**
- **Linux runtime smoke test** (Task 16): deferred — headless environment has no display. Needs a human with a GUI session to verify: scan populates, filters work, Settings opens, folder picker works.
- **Screenshot** (Task 16 step 2): not captured for same reason.
- **MAUI Windows build verification** (Task 9 step 6): needs Windows build with MSBuild.exe.
- **macOS testing**: needs Mac hardware.

## What this is

GitWizard scans git repos and produces structured reports for projdash + LLM
consumption. CLI + projdash integration + Windows MAUI UI all green; the
queued work is **standing up an Avalonia desktop UI** alongside the existing
MAUI app so the GUI runs natively on Linux + macOS without breaking the
Windows MAUI build.

See `CLAUDE.md` for the project map and `PLAN.md` for the priority list of
prior workstreams.

## What's next

**The Avalonia migration** — full plan at
[`docs/superpowers/plans/2026-04-26-avalonia-cross-platform-ui.md`](docs/superpowers/plans/2026-04-26-avalonia-cross-platform-ui.md).
The plan's preamble names `superpowers:subagent-driven-development` as the
required execution skill. Recommended launch:

```
Read docs/superpowers/plans/2026-04-26-avalonia-cross-platform-ui.md
and execute it via superpowers:subagent-driven-development with sonnet
subagents in parallel worktrees where possible.
```

Wave structure (mostly serial because tasks build on each other; some pairs
are parallel-safe):

| Phase | Tasks | Notes |
|---|---|---|
| Pre-flight | Task 0 | Verify .NET 10 SDK, git identity, Avalonia templates, `~/.GitWizard/config.json` has search paths |
| Service abstractions | Tasks 1–3 | `IUiDispatcher` + `IUserDialogs` + `IFolderPicker` — three sibling tasks, parallel-safe (each is its own file in `GitWizardUI.ViewModels/`) |
| VM neutralization | Task 4 | Convert MAUI-typed VM properties (`Color`, `Thickness`, `FontAttributes`) to strings/primitives |
| Extract VM project | Tasks 5–8 | New `GitWizardUI.ViewModels` class lib; move VMs in; extract page-codebehind logic into VM methods |
| MAUI re-wiring | Task 9 | MAUI page consumes shared VMs via `MauiUiDispatcher` etc. Manual Windows verification deferred. |
| Avalonia scaffold + impls | Tasks 10–12 | `GitWizardAvalonia/` project + service impls + `MainWindow.axaml` |
| Smoke + Settings | Tasks 13–14 | End-to-end on Linux; port `SettingsPage` |
| Platform gating + verify | Tasks 15–16 | Hide Windows-only UI off Windows; Linux smoke + screenshot |
| Docs | Task 17 | Append summary to `PLAN.md` |

## Prerequisites (Task 0 covers all)

- **.NET 10 SDK** — `dotnet --version` should print 10.x.
- **Git commit identity** — `git config --global user.name/user.email` set.
- **Avalonia templates** — `dotnet new install Avalonia.Templates` (idempotent).
- **`~/.GitWizard/config.json`** — must have at least one search path so smoke
  tests have repos to find. The current install scans `/home/schoen/` and
  works against pr-crew + llamalab + projdash.
- **Linux build constraint:** the CLI builds on Linux but skips the MFTLib
  native path (Windows-only). The MAUI app does NOT build on Linux at all
  (uses Windows-targeted TFM `net10.0-windows10.0.19041.0`); the plan only
  builds + verifies the new Avalonia project on Linux. MAUI's Windows build
  is verified manually after the migration.

## Conventions

- C# / .NET 10 / NUnit. xUnit assertions are NOT used in this repo.
- 18-task plan with TDD per task; each task ends in a commit.
- Compiled bindings strict in Avalonia (called out in plan preamble) — bindings
  use `x:DataType` for compile-time checking.
- The existing MAUI app (`GitWizardUI/`) is left building throughout the plan.
  Decision on retiring MAUI is deferred (see "Out of scope" at plan tail).

## Known gotchas (for the next session)

- **Don't restructure MAUI mid-plan.** The plan specifically keeps MAUI
  building so Windows users aren't broken. If a task instruction looks like
  it'd break MAUI, re-read the plan section — the order is deliberate.
- **`dotnet build` vs MSBuild.** MFTLib uses a native C++ project that needs
  v145 platform toolset on Windows. `dotnet build` only works when MFTLib is
  a PackageReference, not a ProjectReference. On Linux the CLI builds with
  `dotnet` (NuGet PackageReference path). See `CLAUDE.md` for the Windows
  MSBuild invocation if you swap to local ProjectReference.
- **Plan author already self-reviewed.** The plan's tail has a "Self-review
  checklist (already done — leaving here as a record)" — trust it on the
  prerequisites it's already validated; don't re-litigate.

## Session-end notes (2026-04-26)

The pr-crew sibling project shipped followup #12 (per-harness Gitea bot
identities) in the same window. As a side effect, `aider-bot`, `claude-bot`,
and `opencode-bot` are now read collaborators on `schoen/git-wizard` in
Gitea — so future agent-driven PRs against this repo can be opened by a bot
identity (and approved by schoen). No action required here; just an FYI.
