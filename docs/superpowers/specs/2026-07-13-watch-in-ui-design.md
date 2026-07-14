# Watch-in-UI — Live repository monitoring in GitWizardUI

**Status:** design (approved) · **Date:** 2026-07-13 · **Branch:** `feat/watch-in-ui`

## Goal

Surface git-wizard's existing live change-detection (today CLI-only, `git-wizard --watch`)
into the Avalonia UI as an opt-in **Live mode**: while enabled, the repository list
auto-updates as repos change on disk — no manual Refresh — including full lifecycle
handling (new, deleted, renamed repos). Cross-platform: Windows (USN journal, existing)
and Linux (fanotify, new), testable on llamabox.

## Decisions (from brainstorming)

1. **Core UX:** a **Live toggle**. On = auto-refreshing rows; Off (default) = no change
   source, no elevation prompt. Opt-in so the app never prompts for elevation on launch.
2. **Change scope:** full parity with a manual Refresh — react to **changed, created,
   deleted, and renamed** repos live.
3. **Failure handling:** on unexpected source death, **respawn silently only when it needs
   no new elevation prompt** (already elevated/root); otherwise drop Live→off and show a
   non-blocking status message, keeping last-known data. Declining the elevation prompt =
   straight to off + notify.
4. **Architecture:** Approach A — extract the watch pipeline out of the CLI into a
   platform-agnostic service in the `GitWizard` core library, consumed by both CLI and UI.
5. **Linux privilege:** **`pkexec`/polkit** (turnkey per-session prompt, no install step),
   not a setcap'd helper binary.

## Architecture

The only platform-specific seam is a single change-source interface. Everything above it
is platform-agnostic and unit-testable without elevation.

```
IVolumeChangeSource                       ← platform seam (Windows-gated impls)
  ├─ UsnVolumeChangeSource   (Windows)    wraps MFTLib JournalBrokerClient (USN journal)
  └─ FanotifyVolumeChangeSource (Linux)   new fanotify FAN_MARK_FILESYSTEM source
        │  yields batches of { FullPath, RawChangeKind }
        ▼
RepositoryWatchService (core, platform-agnostic)
        │  owns the cold-scan roots + search roots; maps paths → repo roots;
        │  recognizes .git creation/deletion; correlates delete+create → rename
        ▼  emits RepositoryChangeEvent { RepoRoot, Kind, NewPath? }
        │        Kind ∈ { Changed, Created, Deleted, Renamed }
   ┌────┴─────────────────────────────┐
   ▼                                  ▼
CLI RunWatchAsync (prints)     UI LiveWatchController (drives per-repo refresh)
```

### Components

- **`IVolumeChangeSource`** (new, core) — abstracts "stream filesystem change batches for a
  set of volumes/mounts, from one privileged handle." Methods: arm cold scan + catch-up,
  start live watch, expose an `IAsyncEnumerable` of change batches, report source death.
  This is essentially the shape already implicit in `JournalBrokerClient`, generalized.
- **`UsnVolumeChangeSource`** (new, core, `[SupportedOSPlatform("windows")]`) — thin adapter
  over the existing MFTLib `JournalBrokerClient` / `JournalBatchSource`. No new native work.
- **`FanotifyVolumeChangeSource`** (new, core, Linux) — a fanotify read-loop
  (`FAN_MARK_FILESYSTEM` + `FAN_REPORT_DFID_NAME`, kernel ≥5.1) over managed P/Invoke to
  libc `fanotify_init`/`fanotify_mark`, decoding create/delete/move events into the same
  batch shape. Runs in a `pkexec`-elevated child, mirroring the Windows broker child.
- **`RepositoryWatchService`** (new, core) — platform-agnostic. Selects the source via
  `OperatingSystem.IsWindows()`. Owns the cold-scan → known-roots map, the configured
  search roots, `.git`-lifecycle recognition, and delete+create → rename correlation.
  Emits structured `RepositoryChangeEvent`s. Contains no UI or console dependency.
- **`RepositoryChangeFilter`** (extend existing) — today narrows entries to known roots.
  Add: `.git` **directory-create** under a search root → `Created`; known-root/`.git`
  **delete** → `Deleted`. Rename stays a surfaced `(Deleted, Created)` pair the consumer
  reconciles.
- **CLI `RunWatchAsync`** (refactor) — becomes a thin consumer of `RepositoryWatchService`;
  prints `changed: <root>` (and now new/deleted/renamed lines). Behavior preserved.
- **`LiveWatchController`** (new, `GitWizardUI/Services`) — consumes the service, marshals
  events to the UI thread, and applies them to `MainViewModel`.

## Platform strategy

| | Windows | Linux |
|---|---|---|
| Backend | MFTLib USN journal broker (exists) | fanotify `FAN_MARK_FILESYSTEM` (new) |
| Privilege | UAC elevated child (exists) | `CAP_SYS_ADMIN` via `pkexec`/polkit child |
| Watch unit | drive letter | mount point |
| Kernel/OS floor | any supported Windows | Linux kernel ≥ 5.1 (degrade gracefully below) |

MFTLib's Linux port covered MFT *parsing*, not USN — so the Linux source is genuinely new
code (fanotify), not MFTLib. The `inotify` / .NET `FileSystemWatcher` route is explicitly
rejected: per-directory watches don't scale (8192 `max_user_watches` default; .NET spawns
one inotify instance + thread per directory), whereas fanotify watches a whole filesystem
from one fd — the true structural twin of the USN journal.

## UI wiring

- **Live toggle** — a toolbar button bound to `MainViewModel.IsLive`. Off by default.
- On enable → start `RepositoryWatchService`; per event (marshaled to UI thread):
  - `Changed` → re-run status for that one repo, call existing `UpdateCompletedRepository`.
  - `Created` → discover the repo, call existing `AddRepository`.
  - `Deleted` → remove via existing path-removal logic (`RemoveRenamedReposFromUi` sibling).
  - `Renamed` → reuse existing `FindRenamedRepo` reconciliation.
- On disable → tear down the service + elevated child cleanly.
- **Debounce/coalesce** — per-repo coalescing over a ~500ms window (hard-coded v1) so a
  build or branch-switch touching thousands of files triggers one `git status` per repo per
  burst, not one per file event.

## Elevation & failure handling

- **One privileged handle per Live session** — a single UAC prompt (Windows) or single
  `pkexec`/polkit prompt (Linux), matching the CLI's single-broker model.
- On unexpected source death: respawn silently **iff** already elevated/root; else set
  `IsLive`→false and surface a non-blocking status ("Live watch stopped: <reason>"), keeping
  the last-known repo data on screen. Declining the elevation prompt → off + notify.

## Testing

- **`RepositoryWatchService` unit tests** against a **fake `IVolumeChangeSource`** (scripted
  event batches): changed/created/deleted/renamed mapping, rename correlation, and debounce
  are all exercised with zero elevation, cross-platform. This carries the bulk of coverage
  and keeps the 100% managed gate honest.
- **Windows integration** — existing MFTLib broker path (admin-gated).
- **Linux integration** — `FanotifyVolumeChangeSource` against real fanotify (root) on
  **llamabox**.

## Scope / YAGNI

- **In:** Live toggle; live per-repo refresh; new/deleted/renamed; Windows + Linux.
- **Out (v1):** watching drives/mounts never scanned; per-repo watch-history log;
  user-configurable debounce window; auto-persisting the Live toggle across app restarts.

## Risks

- **Linux privilege UX** — `pkexec` requires a polkit agent in the session; headless/SSH
  runs need `pkexec` policy or a root shell. Fine for a desktop session and for llamabox
  testing with a root shell; documented as a floor.
- **Kernel floor** — fanotify `FAN_MARK_FILESYSTEM` needs ≥5.1; detect and degrade (Live
  toggle disabled with an explanatory tooltip on unsupported kernels).
- **Event volume** — new-`.git` detection means watching whole volumes/mounts, not only
  known roots; the extended `RepositoryChangeFilter` must stay cheap on the hot path.
