# Watch-in-UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface git-wizard's CLI-only live change detection into the Avalonia UI as an opt-in **Live mode** that auto-updates the repository list (changed/created/deleted/renamed) as repos change on disk, cross-platform (Windows USN journal + Linux fanotify).

**Architecture:** Extract the watch pipeline out of the CLI into a platform-agnostic `RepositoryWatchService` in the `GitWizard` core library, sitting behind a single `IVolumeChangeSource` seam. Windows implements the seam over the existing MFTLib `JournalBrokerClient` (USN); Linux implements it with a new fanotify source. Both CLI and UI consume the service; the UI adds a `LiveWatchController` that marshals structured change events onto the view-model's existing per-repo add/update/remove hooks.

**Tech Stack:** C# / .NET 10, Avalonia 11.2 (MVVM, manual `OnPropertyChanged` + custom `RelayCommand`/`AsyncRelayCommand`), MFTLib 0.3 `JournalBrokerClient` (Windows), libc `fanotify` P/Invoke (Linux), NUnit (`GitWizardTests`).

## Global Constraints

- **MFTLib pin:** `external/MFTLib` at the repo's current pin. Do not downgrade.
- **Coverage gate:** 100% managed line/branch is the bar (`TEST-REPORT.md`); new logic must land with tests. Platform-gated native paths (fanotify/USN broker) are covered by a fake `IVolumeChangeSource` for the mapping logic + integration tests for the real sources.
- **aislop gate:** `failBelow: 100`. No narrative comments, no swallowed exceptions, no oversized functions. Run `aislop scan .` before declaring a task done.
- **Charset:** utf-8 (no BOM), CRLF, 4-space. New `.cs` files written via tooling that emits LF must be CRLF-normalized before the format gate (`dotnet format git-wizard.slnx --verify-no-changes`).
- **Naming:** PascalCase types/members; `_camelCase` all private/internal fields; camelCase params/locals. No `m_`/`k_`/`s_`.
- **Platform gating:** Windows-only code carries `[SupportedOSPlatform("windows")]`; Linux-only carries `[SupportedOSPlatform("linux")]`. Core service and mapping stay platform-neutral.
- **Elevation model:** exactly one privileged handle per Live session (one UAC / one pkexec prompt). On unexpected source death, respawn silently ONLY if no new prompt is needed (already elevated/root); otherwise stop Live + notify.

---

## Status

Phase 3 (Linux fanotify backend) is the remaining work. It builds on the watch stack
already in the tree: `IVolumeChangeSource` / `RepositoryWatchService` /
`LiveWatchController`, plus the fanotify interop layer
(`GitWizard/Watch/Linux/FanotifyInterop.cs` with its kernel-floor probe and tests).
Live mode is currently Windows-only.

## Phase 3: Linux fanotify backend

### Task 12: FanotifyVolumeChangeSource

**Files:**
- Create: `GitWizard/Watch/Linux/FanotifyVolumeChangeSource.cs`
- Test: `GitWizardTests/Watch/Linux/FanotifyVolumeChangeSourceTests.cs` (`[Category("RequiresRoot")]`)

**Interfaces:**
- Produces: `[SupportedOSPlatform("linux")] sealed class FanotifyVolumeChangeSource : IVolumeChangeSource` — arms marks per mount, decodes the fanotify event stream (`FAN_CREATE`/`FAN_DELETE`/`FAN_MOVED_FROM`/`FAN_MOVED_TO`/`FAN_MODIFY`) into `VolumeChangeBatch` using `FAN_REPORT_DFID_NAME` (dir fd + name → full path via `open_by_handle_at`/`/proc/self/fd`). Cold scan = a directory walk of the search roots (synthetic `RecordId`).

- [x] **Step 1:** Test on llamabox: create/delete/rename files under a temp mount and assert the decoded `VolumeChangeEntry` kinds + paths.
- [x] **Step 2:** Run — verify FAIL.
- [x] **Step 3:** Implement the read loop + decode. Keep the fd read loop small; extract path-resolution into a helper. Map moves: `FAN_MOVED_FROM`→Deleted, `FAN_MOVED_TO`→Created (the service correlates into rename downstream).
- [x] **Step 4:** Run on llamabox — verify PASS. Then run `RepositoryWatchServiceTests` on Linux too (they use the fake — must stay green cross-platform).
- [x] **Step 5:** Format, aislop, commit: `git commit -m "feat(watch): FanotifyVolumeChangeSource (Linux whole-fs watch)"`

Landed as PR #135 (issue #130): `FanotifyVolumeChangeSource` plus its root-gated tests, merged 2026-08-23 with all CI contexts green.

### Task 13: pkexec elevation + OS source selection

**Files:**
- Create: `GitWizard/Watch/VolumeChangeSourceFactory.cs`
- Modify: CLI + `LiveWatchController` construction sites to use the factory.
- Test: `GitWizardTests/Watch/VolumeChangeSourceFactoryTests.cs`

**Interfaces:**
- Produces: `static IVolumeChangeSource Create(...)` selecting `UsnVolumeChangeSource` on Windows, `FanotifyVolumeChangeSource` on Linux; on Linux, if not already root, launch the watch child via `pkexec` (mirrors the Windows elevated-broker child). Exposes `static bool IsElevated()` (Windows: token elevation; Linux: `geteuid()==0`) for the respawn decision.

- [ ] **Step 1:** Test: factory returns the platform-correct type; `IsElevated()` matches the current process on each OS (gated per-OS).
- [ ] **Step 2:** Run — verify FAIL.
- [ ] **Step 3:** Implement the factory + `pkexec` child launch + `IsElevated`. Wire `LiveWatchController`'s `isElevated` and `sourceFactory` to it; wire the CLI to it (replacing the direct `UsnVolumeChangeSource` from Task 5 with the factory).
- [ ] **Step 4:** Run — verify PASS on Windows; on llamabox, `git-wizard --watch` as non-root triggers one pkexec prompt then streams events.
- [ ] **Step 5:** Format, aislop, commit: `git commit -m "feat(watch): OS source selection + pkexec elevation on Linux"`

### Task 14: Linux Live-mode graceful degrade + docs

**Files:**
- Modify: `GitWizardUI/ViewModels/MainViewModel.cs` (call `FanotifyInterop.Probe()` on Linux; disable `CanToggleLive` with a reason for `UnsupportedKernel` or `UnsupportedFileSystem`, while `Available` and `RequiresElevation` remain enabled)
- Modify: `README.md`, `AGENTS.md`

- [ ] **Step 1:** Test injected `FanotifyCapabilityResult` values: `UnsupportedKernel` and `UnsupportedFileSystem` disable `CanToggleLive` with the matching reason; `Available` enables it; `RequiresElevation` also enables it so Task 13 can request one pkexec elevation.
- [ ] **Step 2:** Implement, verify PASS.
- [ ] **Step 3:** **Docs** - document Linux Live mode: fanotify, kernel 5.9 or newer, pkexec prompt, llamabox as the test host. `git commit -m "docs(watch): Linux Live mode (fanotify, pkexec, kernel floor)"`

---

