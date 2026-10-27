# Watch and recovery

The pipeline lives in `GitWizard/Watch/` behind `IVolumeChangeSource`. Library
behavior belongs to MFTLib's [watch lifetime](../external/MFTLib/docs/watch-lifetime.md),
[checkpoint loss](../external/MFTLib/docs/checkpoint-loss.md),
[broker lifetime](../external/MFTLib/docs/broker-lifetime.md) and
[block ownership](../external/MFTLib/docs/block-ownership.md) contracts. This page
describes git-wizard's adapters and consumers, not a second library specification.

## Windows adapter

`IndexVolumeChangeSource` owns a `MftIndexSession`, adopting a prepared discovery owner only when it is usable and its normalized drive set matches. Otherwise it disposes that owner and opens a replacement. An arm that opens a fresh session forwards the broker's throttled ScanProgress frames as `IVolumeScanProgress.ScanProgress` (`VolumeScanProgress`: drive, 1-based drive ordinal/total, rows written and nullable total); an adopted prepared owner scans nothing and reports nothing.

The adapter maps `Changed` into volume batches and consumes `WatchFaulted`. It
uses `WatchFault.IsRecovering` for recoverability, subscribes to per-drive
`WatchStateChanged`, reads `WatchStateChange.Watch.State` / `Watch.StateVersion`
and seeds from `RootStatus.Watch`. The event and recovery classification
contracts are defined in the linked MFTLib documentation.

Equal and older versions are ignored. Recovery reconciliation runs off the library callback thread and publishes CaughtUp only if its version is still current. WatchFaulted remains the source of fault diagnostics and terminal-drive policy; recoverable Faulted then Recovering transitions do not exclude the drive. No recovery timer or status polling is used.

The source records the drive's `.git` marker paths when recovery starts and, once
the recovered watch has caught up and before it publishes `CaughtUp`, delivers
the difference as ordinary change entries
(`IndexVolumeChangeSource.RecoveryReconciliation.cs`): a vanished marker as
`Deleted`, a new one as `Created`, and a surviving one as `Modified` so its
repository is refreshed. `RepositoryWatchService` applies its usual membership,
scope and refresh rules to them.

Otherwise the observation stays pending: a recoverable fault is followed, and a terminal one is excluded by its own notification. A fault that lands just after the publication is published by its handler over it. `OnWatchFaulted` excludes a nonrecovering drive and raises optional `IVolumeChangeDiagnostics.DriveFailed` while healthy drives continue. The adapter reads `RootWatchStatus.IsRecoveryStopped` for journal-warning refreshes; consumers do not infer exhaustion from failure text or a retry count. A drive that reads `Recovering` when its startup catch-up wait faults is followed the same way instead of being excluded. MFTLib's linked watch/checkpoint contracts define fault classification.

When the faulted drive reports a `JournalCheckpointLossDetection.LiveWatch` or `ScanCatchUp` loss the source first raises `IVolumeChangeDiagnostics.JournalWarningDetected` with the drive's refreshed hint; an unrelated fault raises no warning and leaves the open's standing report untouched. Arm-time `JournalCheckpointLossDetection.RootOpening` losses, including a cache-declined drive's discarded checkpoint, reach the arm result as `JournalWarning`s.

When the last usable drive is excluded the source raises `SourceDied` once and closes the output channel; a recovering drive keeps the source alive. Subscriber faults reach the reader as errors. Shutdown detaches index handlers and stops the pump before disposing its owner.

## Linux adapter

`FanotifyInterop` is the P/Invoke layer plus capability probe for the `fanotify` counterpart (`Available` / `RequiresElevation` / `UnsupportedKernel` / `UnsupportedFileSystem`, kernel floor 5.9). Probed by `LiveWatchViewModel` to enable Live mode on supported kernels and filesystems while gracefully disabling it with user-facing reasons on older kernels or unsupported filesystems.

`FanotifyVolumeChangeSource` is the `IVolumeChangeSource` backend for whole-filesystem change detection via fanotify. It emits every batch under the single routing volume `/` (`LinuxWatchRouting.Volume`, also the only startup watch volume), using marked roots only for native arming, readiness and error attribution. A directory arriving through `FAN_MOVED_TO` is emitted as `MovedIn`; file creation and file moves stay `Created`. Each decoded handle carries the event's filesystem identifier (`FileHandleKey` includes it), and `MountRootTable` remembers the marked root paths grouped by filesystem. Each decoded batch opens a short-lived descriptor on the first registered root that currently reaches the event's filesystem (a root that was renamed, removed, overmounted or unmounted is skipped because its path no longer reaches the filesystem), resolves that batch's handles through it for `open_by_handle_at`, and closes it, so no descriptor is held between batches and a plain `umount` of a watched filesystem is never blocked. Resolved paths carry the prefix of the mount that produced them: when a batch reaches a filesystem through a different root than the previous batch, or no root reaches it, that filesystem's cached handles are discarded before the cache is consulted, so a detached mount's prefix is never emitted. An event whose filesystem has no reachable root is dropped, and an event from an unmarked filesystem is skipped. Every re-arm, including empty and initialization-failed attempts, forgets the registered roots and cached handles; disposal does the same.

## Membership and batching

`RepositoryWatchService` is the platform-agnostic layer on top: it groups tracked roots by volume, maps change batches through `GitWizard.RepositoryChangeFilter.ClassifyEntry`/`TryClassifyMovedInCandidate`, and dedupes/debounces per window. The filter set is live: a repository whose `.git` creation classifies under a search root (or is probed from a moved-in candidate) is adopted into that volume's running filter, and a deleted repository's root is dropped, so repositories created while Live runs are tracked for changes without restarting Live. Directory renames arrive as `Deleted` + `MovedIn` (see `IndexChangeMapping`): a `MovedIn` path under a tracked root classifies as `Changed`, while an untracked one under a search root is probed once at flush time for `<path>\.git` (a directory, or a file for a gitfile worktree) and surfaces as `Created` on a hit, skipping ignored and temp-excluded paths. Probe-emitted `Created` events flush after the window's `Deleted` events so the UI's pending-delete rename correlation sees the old root first. It subscribes to optional drive diagnostics and optional `IVolumeScanProgress` before arming and detaches on shutdown, publishing immutable `ScanErrors` snapshots for initial scan errors and later watch failures plus immutable `ScanWarnings` journal-hint snapshots, where a mid-run `JournalWarningDetected` replaces the same drive's earlier hint, and re-raising `ScanProgress`. `ScanErrorsChanged` fires outside the error lock, so a warning does not depend on a repository event. Sources without diagnostics retain their existing watch behavior.

`RepositoryChangeFilter` is I/O-free and cross-platform-testable, with a tracked root set that `AddRepositoryRoot`/`RemoveRepositoryRoot` mutate mid-run (single-threaded, on the watch pump). `ClassifyEntry` is path-based and emits Changed/Created/Deleted for one entry; `TryClassifyMovedInCandidate` separately reports `MovedInCandidates` (untracked directories renamed into a search root, or a case-only rename of a tracked root, left for the service layer to probe - the filter itself never touches the filesystem). Both respect configured ignored paths and the temp-directory exclusion (`includeTempDirectories`). Paths compare through `GitWizard.Watch.WatchPath`, which retains a path's syntax (Windows-rooted vs POSIX) instead of rewriting separators, so a POSIX root is reported back as `/repos/repo-a` rather than `\repos\repo-a`.

## Consumers

`Program.cs` dispatches the elevated `--broker` child through `ElevatedEntryPoint.RunBrokerIfRequested` before normal startup. `--watch` uses the platform source factory (`IndexVolumeChangeSource` on Windows), prints `changed:` / `created:` / `deleted: <root>`, prints each new or changed `scan error on <drive>: <message>` once while the stream is live, and prints each journal hint line when it first appears and again when its wording changes (a live-watch loss rephrases the drive's arm-time hint). CLI `--watch` runs `RepositoryDiscoveryCoordinator.PrepareWatchAsync` with the readable cached list as retained exact roots, so cached repositories on drives outside the configured search roots stay in discovery and in the journal watch's selected drive set; it replaces `repositories.txt` only with a publishable outcome. Repositories created since the previous cache write are therefore tracked without a rename. Discovery respects the configured ignore, hidden-directory and temp-directory policies. This adds startup latency at each CLI watch start; it does not launch an additional MFT broker or elevation prompt. An empty discovery clears the repository-list cache and the CLI reports that there is nothing to watch; an unusable discovery preserves the previous list and exits 1.

`LiveWatchController` forwards `ScanErrorsChanged` and `ScanProgress`; the UI shows "Scanning D: (n of N), x of y records" with a progress bar next to the amber Live button until the watch reports started (green), the start fails, or Live stops. The UI posts warning changes through its dispatcher even without repository events. A drive-specific failure keeps Live active on healthy drives. A Live run that ends in source death keeps the drives' journal hints on screen; a user-initiated stop still clears them. Terminal loss retains the existing stop policy and at most three elevated respawns.

## Readiness

`Started` and the green Live state describe pump startup, never a catch-up completion fence. Readiness is a separate contract: `IVolumeReadiness` (`GitWizard/Watch/VolumeReadiness.cs`) exposes immutable per-volume `CatchingUp`/`CaughtUp`/`Excluded` state with `Warm`/`Cold` origin; `IndexVolumeChangeSource` sets it only from each drive's `WaitForCatchUpAsync` outcome and later watch faults, and `FanotifyVolumeChangeSource` sets `CaughtUp` when a mark installs (mode `FilesystemMarks`, no backlog fence). `RepositoryWatchService.Ready` fires exactly once per run when the snapshot is settled with at least one caught-up volume, the startup batches are delivered and the run is not terminal; terminal marking and publication share one lock so death or cancellation cannot race out a late marker.

`LiveWatchController` forwards `Ready`, `VolumeReadinessChanged` and `StartupProgress`; the CLI prints `ready:` (grammar in README) and, under `-v`, timestamped `drive:`/`startup:` lines. `RepositoryDiscoveryCoordinator.PrepareWatchAsync` takes an `IProgress<WatchStartupProgress>` because the arm and catch-up complete before any consumer holds the source; `RequestingElevation`/`BrokerConnected` come from `BrokerSession.Connecting`/`Connected`. Broker process existence is never readiness evidence. The UI still correlates delete/create pairs by remote URL.

Desktop readiness uses one reserved, nonwrapping toolbar line; per-drive state, origin, and failure reasons are shown only in its tooltip and the Index Cache Status window, sharing LiveWatchViewModel.LiveVolumes. The readiness tooltip and Index Cache Status window list recent watch transitions separately from current volume readiness. Faulted and Recovering are retained as separate observations even when recovery is immediate. The list retains up to 64 observations for 30 seconds and clears on an explicit Live stop or a new run. It is in-memory only and is not part of report.json. UI summary updates on later snapshots do not re-emit RepositoryWatchService.Ready. Cache-status refresh remains independent of live readiness updates.

## Verification sources

- Windows: `GitWizard/Watch/IndexVolumeChangeSource*.cs`, `IndexChangeMapping.cs`, `VolumeReadiness.cs`, `WatchStartupProgress.cs`.
- Linux: `GitWizard/Watch/Linux/Fanotify*.cs`, `GitWizard/Watch/Linux/MountRootTable.cs`, `GitWizard/Watch/LinuxWatchRouting.cs`.
- Membership/readiness: `GitWizard/Watch/RepositoryWatchService*.cs`, `GitWizard/RepositoryChangeFilter*.cs`, `GitWizard/Discovery/RepositoryDiscoveryCoordinator*.cs`.
- CLI: `git-wizard/Program.cs`, `Program.Watch*.cs`; desktop: `GitWizardUI/Services/LiveWatchController.cs`, `GitWizardUI/ViewModels/LiveWatchViewModel*.cs`, `GitWizardUI/Views/MainWindow.axaml` and `IndexCacheStatusWindow.axaml`.
