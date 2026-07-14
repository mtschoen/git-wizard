# Watch-in-UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface git-wizard's CLI-only live change detection into the Avalonia UI as an opt-in **Live mode** that auto-updates the repository list (changed/created/deleted/renamed) as repos change on disk, cross-platform (Windows USN journal + Linux fanotify).

**Architecture:** Extract the watch pipeline out of the CLI into a platform-agnostic `RepositoryWatchService` in the `GitWizard` core library, sitting behind a single `IVolumeChangeSource` seam. Windows implements the seam over the existing MFTLib `JournalBrokerClient` (USN); Linux implements it with a new fanotify source. Both CLI and UI consume the service; the UI adds a `LiveWatchController` that marshals structured change events onto the view-model's existing per-repo add/update/remove hooks.

**Tech Stack:** C# / .NET 10, Avalonia 11.2 (MVVM, manual `OnPropertyChanged` + custom `RelayCommand`/`AsyncRelayCommand`), MFTLib 0.3 `JournalBrokerClient` (Windows), libc `fanotify` P/Invoke (Linux), NUnit (`GitWizardTests`).

## Global Constraints

- **MFTLib pin:** `external/MFTLib` at `cc44880` (0.3 VolumeBroker); this branch is stacked on that bump. Do not downgrade.
- **Coverage gate:** 100% managed line/branch is the bar (`TEST-REPORT.md`); new logic must land with tests. Platform-gated native paths (fanotify/USN broker) are covered by a fake `IVolumeChangeSource` for the mapping logic + integration tests for the real sources.
- **aislop gate:** `failBelow: 100`. No narrative comments, no swallowed exceptions, no oversized functions. Run `aislop scan .` before declaring a task done.
- **Charset:** utf-8 (no BOM), CRLF, 4-space. New `.cs` files written via tooling that emits LF must be CRLF-normalized before the format gate (`dotnet format git-wizard.slnx --verify-no-changes`).
- **Naming:** PascalCase types/members; `_camelCase` all private/internal fields; camelCase params/locals. No `m_`/`k_`/`s_`.
- **Platform gating:** Windows-only code carries `[SupportedOSPlatform("windows")]`; Linux-only carries `[SupportedOSPlatform("linux")]`. Core service and mapping stay platform-neutral.
- **Elevation model:** exactly one privileged handle per Live session (one UAC / one pkexec prompt). On unexpected source death, respawn silently ONLY if no new prompt is needed (already elevated/root); otherwise stop Live + notify.

---

## Phase 1: Platform-agnostic watch service in core (CLI stays green)

Goal of the phase: the CLI `--watch` behaves exactly as today, but its pipeline now runs through the new `RepositoryWatchService` behind `IVolumeChangeSource`. No UI, no Linux yet.

### Task 1: Change-event types and the source seam

**Files:**
- Create: `GitWizard/Watch/RepositoryChangeEvent.cs`
- Create: `GitWizard/Watch/IVolumeChangeSource.cs`
- Create: `GitWizard/Watch/VolumeChangeBatch.cs`
- Test: `GitWizardTests/Watch/FakeVolumeChangeSourceTests.cs`

**Interfaces:**
- Produces:
  - `enum RepositoryChangeKind { Changed, Created, Deleted, Renamed }`
  - `sealed record RepositoryChangeEvent(string RepoRoot, RepositoryChangeKind Kind, string? NewPath = null)`
  - `enum VolumeEntryKind { Modified, Created, Deleted }`
  - `readonly record struct VolumeChangeEntry(string FullPath, VolumeEntryKind Kind)`
  - `sealed record VolumeChangeBatch(string Volume, IReadOnlyList<VolumeChangeEntry> Entries)`
  - `interface IVolumeChangeSource : IAsyncDisposable` with:
    - `event Action<string>? SourceDied;`
    - `Task<IReadOnlyList<VolumeColdRecord>> ArmAndCatchUpAsync(IReadOnlyCollection<string> volumes, CancellationToken ct);`
    - `IAsyncEnumerable<VolumeChangeBatch> WatchAsync(CancellationToken ct);`
  - `readonly record struct VolumeColdRecord(string Path, ulong RecordId)` — the cold-scan snapshot the mapper indexes (Windows fills `RecordId` from USN record numbers; Linux uses a synthetic id).

- [ ] **Step 1: Write a fake source + its test**

```csharp
// GitWizardTests/Watch/FakeVolumeChangeSourceTests.cs
using GitWizard.Watch;

namespace GitWizardTests.Watch;

// A scripted IVolumeChangeSource used across the phase-1/phase-2 tests.
internal sealed class FakeVolumeChangeSource : IVolumeChangeSource
{
    readonly IReadOnlyList<VolumeColdRecord> _cold;
    readonly IReadOnlyList<VolumeChangeBatch> _batches;
    public event Action<string>? SourceDied;

    public FakeVolumeChangeSource(
        IReadOnlyList<VolumeColdRecord> cold, IReadOnlyList<VolumeChangeBatch> batches)
    {
        _cold = cold;
        _batches = batches;
    }

    public Task<IReadOnlyList<VolumeColdRecord>> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct) => Task.FromResult(_cold);

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var batch in _batches)
        {
            ct.ThrowIfCancellationRequested();
            yield return batch;
            await Task.Yield();
        }
    }

    public void KillSource(string reason) => SourceDied?.Invoke(reason);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[TestFixture]
public class FakeVolumeChangeSourceTests
{
    [Test]
    public async Task WatchAsync_YieldsScriptedBatches()
    {
        var source = new FakeVolumeChangeSource(
            cold: Array.Empty<VolumeColdRecord>(),
            batches: new[]
            {
                new VolumeChangeBatch("C", new[]
                {
                    new VolumeChangeEntry(@"C:\repo\a\file.txt", VolumeEntryKind.Modified)
                })
            });

        var seen = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(CancellationToken.None))
            seen.Add(batch);

        Assert.That(seen, Has.Count.EqualTo(1));
        Assert.That(seen[0].Entries[0].Kind, Is.EqualTo(VolumeEntryKind.Modified));
    }
}
```

- [ ] **Step 2: Run it — verify it fails to compile (types missing)**

Run: `dotnet build GitWizardTests/GitWizardTests.csproj -c Release`
Expected: FAIL — `RepositoryChangeEvent`/`IVolumeChangeSource`/etc. not found.

- [ ] **Step 3: Create the three type files**

```csharp
// GitWizard/Watch/RepositoryChangeEvent.cs
namespace GitWizard.Watch;

public enum RepositoryChangeKind { Changed, Created, Deleted, Renamed }

public sealed record RepositoryChangeEvent(
    string RepoRoot, RepositoryChangeKind Kind, string? NewPath = null);
```

```csharp
// GitWizard/Watch/VolumeChangeBatch.cs
namespace GitWizard.Watch;

public enum VolumeEntryKind { Modified, Created, Deleted }

public readonly record struct VolumeChangeEntry(string FullPath, VolumeEntryKind Kind);

public sealed record VolumeChangeBatch(string Volume, IReadOnlyList<VolumeChangeEntry> Entries);

public readonly record struct VolumeColdRecord(string Path, ulong RecordId);
```

```csharp
// GitWizard/Watch/IVolumeChangeSource.cs
namespace GitWizard.Watch;

/// One privileged handle streaming filesystem change batches for a set of volumes.
public interface IVolumeChangeSource : IAsyncDisposable
{
    event Action<string>? SourceDied;

    Task<IReadOnlyList<VolumeColdRecord>> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct);

    IAsyncEnumerable<VolumeChangeBatch> WatchAsync(CancellationToken ct);
}
```

- [ ] **Step 4: Run the test — verify it passes**

Run: `dotnet build git-wizard.slnx -c Release && dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release --filter FakeVolumeChangeSourceTests`
Expected: PASS.

- [ ] **Step 5: CRLF-normalize new files, verify format + aislop, commit**

```bash
# normalize any LF-only new files, then:
dotnet format git-wizard.slnx --verify-no-changes
aislop scan .
git add GitWizard/Watch GitWizardTests/Watch
git commit -m "feat(watch): change-event types and IVolumeChangeSource seam"
```

### Task 2: RepositoryWatchService — mapping, rename correlation, debounce

**Files:**
- Create: `GitWizard/Watch/RepositoryWatchService.cs`
- Test: `GitWizardTests/Watch/RepositoryWatchServiceTests.cs`

**Interfaces:**
- Consumes: `IVolumeChangeSource`, `RepositoryChangeFilter` (Task 3 extends it), `RepositoryChangeEvent`.
- Produces:
  - `sealed class RepositoryWatchService`
    - ctor `(IVolumeChangeSource source, IReadOnlyCollection<string> trackedRoots, IReadOnlyCollection<string> searchRoots, TimeSpan? debounce = null)`
    - `IAsyncEnumerable<RepositoryChangeEvent> RunAsync(CancellationToken ct)`
    - `event Action<string>? Stopped;` (raised on source death, carrying the reason)
  - Debounce default `TimeSpan.FromMilliseconds(500)`.

Mapping rules the service applies per volume batch (after arming the cold scan and building a `RepositoryChangeFilter` per volume):
- An entry under a **tracked root** whose path is not a root create/delete → coalesce into one `Changed(root)` per debounce window.
- A **created** entry whose path ends in a `.git` directory under a **search root** → `Created(repoRoot)` where `repoRoot` is the `.git` parent.
- A **deleted** entry equal to a tracked root's `.git` (or the root itself) → `Deleted(root)`.
- Within one debounce flush, a `Deleted(oldRoot)` + `Created(newRoot)` are surfaced as-is; the **consumer** correlates them into a rename (the UI reuses `FindRenamedRepo`). The service does NOT itself decide renames — it emits the raw Created/Deleted, keeping core free of git-remote comparison. (Documented so consumers know they own correlation.)

- [ ] **Step 1: Write failing tests for the mapping + debounce**

```csharp
// GitWizardTests/Watch/RepositoryWatchServiceTests.cs
using GitWizard.Watch;

namespace GitWizardTests.Watch;

[TestFixture]
public class RepositoryWatchServiceTests
{
    static async Task<List<RepositoryChangeEvent>> DrainAsync(RepositoryWatchService svc)
    {
        var events = new List<RepositoryChangeEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var ev in svc.RunAsync(cts.Token))
            events.Add(ev);
        return events;
    }

    [Test]
    public async Task ModifiedUnderTrackedRoot_CoalescesToSingleChanged()
    {
        var cold = new[] { new VolumeColdRecord(@"C:\repo\a", 1) };
        var batch = new VolumeChangeBatch("C", new[]
        {
            new VolumeChangeEntry(@"C:\repo\a\one.txt", VolumeEntryKind.Modified),
            new VolumeChangeEntry(@"C:\repo\a\two.txt", VolumeEntryKind.Modified),
        });
        var svc = new RepositoryWatchService(
            new FakeVolumeChangeSource(cold, new[] { batch }),
            trackedRoots: new[] { @"C:\repo\a" },
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.EqualTo(new RepositoryChangeEvent(@"C:\repo\a", RepositoryChangeKind.Changed)));
    }

    [Test]
    public async Task DotGitCreatedUnderSearchRoot_EmitsCreated()
    {
        var svc = new RepositoryWatchService(
            new FakeVolumeChangeSource(
                Array.Empty<VolumeColdRecord>(),
                new[] { new VolumeChangeBatch("C", new[]
                {
                    new VolumeChangeEntry(@"C:\repo\newrepo\.git", VolumeEntryKind.Created)
                }) }),
            trackedRoots: Array.Empty<string>(),
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Does.Contain(
            new RepositoryChangeEvent(@"C:\repo\newrepo", RepositoryChangeKind.Created)));
    }

    [Test]
    public async Task TrackedRootDotGitDeleted_EmitsDeleted()
    {
        var svc = new RepositoryWatchService(
            new FakeVolumeChangeSource(
                new[] { new VolumeColdRecord(@"C:\repo\gone", 2) },
                new[] { new VolumeChangeBatch("C", new[]
                {
                    new VolumeChangeEntry(@"C:\repo\gone\.git", VolumeEntryKind.Deleted)
                }) }),
            trackedRoots: new[] { @"C:\repo\gone" },
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Does.Contain(
            new RepositoryChangeEvent(@"C:\repo\gone", RepositoryChangeKind.Deleted)));
    }
}
```

- [ ] **Step 2: Run — verify FAIL** (`RepositoryWatchService` undefined).
Run: `dotnet build git-wizard.slnx -c Release`

- [ ] **Step 3: Implement `RepositoryWatchService`**

Implement per the mapping rules above. Structure: `RunAsync` calls `ArmAndCatchUpAsync`, builds per-volume `RepositoryChangeFilter` from the cold records + tracked roots, then `await foreach` over `source.WatchAsync`. Accumulate affected roots / created / deleted into a per-window dictionary keyed by root; flush after `debounce` idle using a `PeriodicTimer` or a `Task.Delay`-driven coalescer. Subscribe to `source.SourceDied` → raise `Stopped` and complete the enumeration. Keep each method small; split the coalescer into a private helper type if `RunAsync` grows past ~40 lines (aislop function-size).

*(Full coalescer code is the implementer's to write against these tests; the three tests above pin the observable contract. Match the existing `RepositoryChangeFilter` usage from `git-wizard/Program.Watch.cs:184-208`.)*

- [ ] **Step 4: Run — verify PASS.**
Run: `dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release --filter RepositoryWatchServiceTests`

- [ ] **Step 5: Format, aislop, commit**

```bash
dotnet format git-wizard.slnx --verify-no-changes && aislop scan .
git add GitWizard/Watch GitWizardTests/Watch
git commit -m "feat(watch): RepositoryWatchService mapping + debounce + rename surfacing"
```

### Task 3: Extend RepositoryChangeFilter for .git create/delete classification

**Files:**
- Modify: `GitWizard/RepositoryChangeFilter.cs`
- Test: `GitWizardTests/` (add to the existing filter test file if present; else `GitWizardTests/RepositoryChangeFilterTests.cs`)

**Interfaces:**
- Produces: alongside the existing `IReadOnlyCollection<string> Filter(UsnJournalEntry[] batch)` (which returns modified tracked roots), add
  `FilterResult Classify(IReadOnlyList<VolumeChangeEntry> entries)` returning
  `readonly record struct FilterResult(IReadOnlyCollection<string> Changed, IReadOnlyCollection<string> Created, IReadOnlyCollection<string> Deleted)`.
  Created = `.git`-parent of a `Created` entry under a search root; Deleted = tracked root whose `.git`/root was `Deleted`; Changed = existing behavior over `Modified` entries.
- Consumes: search roots (new ctor param `IReadOnlyCollection<string> searchRoots`).

- [ ] **Step 1:** Write failing tests covering Created/Deleted/Changed classification (mirror the three `RepositoryWatchServiceTests` cases at the filter level, plus: a `.git` create OUTSIDE any search root is ignored).
- [ ] **Step 2:** Run — verify FAIL (`Classify` undefined). `dotnet build git-wizard.slnx -c Release`
- [ ] **Step 3:** Implement `Classify` + the `searchRoots` ctor param. Keep `Filter` intact so the CLI's current call site still compiles until Task 5 migrates it. Reuse the existing record-number index for Changed; add path-prefix matching against `searchRoots` for Created and against tracked roots for Deleted.
- [ ] **Step 4:** Run — verify PASS. `dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release --filter RepositoryChangeFilter`
- [ ] **Step 5:** Format, aislop, commit: `git commit -m "feat(watch): classify .git create/delete in RepositoryChangeFilter"`

### Task 4: UsnVolumeChangeSource (Windows adapter)

**Files:**
- Create: `GitWizard/Watch/UsnVolumeChangeSource.cs`
- Test: covered by integration (admin) — add `[Category("RequiresAdmin")]` smoke in `GitWizardTests/Watch/UsnVolumeChangeSourceTests.cs` gated like existing admin tests; the mapping is already unit-covered via the fake.

**Interfaces:**
- Produces: `sealed class UsnVolumeChangeSource : IVolumeChangeSource` with `[SupportedOSPlatform("windows")]`, ctor `(Func<...> brokerLaunch)` matching `BrokerLauncher.Launch`.
- Consumes: MFTLib `JournalBrokerClient.SpawnAndConnectAsync`, `ArmScanAndCatchUpAsync` → `BrokerScanResult` (`.Records` of `ScanRecord`, `.AdvancedCursors`), `SendStartWatchAsync`, `CreateBatchSource`, `BrokerDied` event.

- [ ] **Step 1:** Write the admin-gated smoke test (arm + catch up on `C`, assert a non-null cold record list). Mark `[Category("RequiresAdmin")]`.
- [ ] **Step 2:** Run non-interactive — verify it's SKIPPED (admin category), build compiles-fails (type missing). `dotnet build git-wizard.slnx -c Release`
- [ ] **Step 3:** Implement the adapter: wrap `JournalBrokerClient`, translate `BrokerScanResult.Records` → `VolumeColdRecord(record.Path, record.RecordNumber)`, translate live `UsnJournalEntry` batches → `VolumeChangeBatch` (map USN reason flags: `FileCreate`→Created, `FileDelete`→Deleted, else Modified), forward `BrokerDied`→`SourceDied`. Reuse the per-drive grouping/cursor logic currently in `Program.Watch.cs` (move the reusable parts here). `[SupportedOSPlatform("windows")]` throughout.
- [ ] **Step 4:** Verify build clean; run non-admin suite green. `dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release` (admin skipped).
- [ ] **Step 5:** Format, aislop, commit: `git commit -m "feat(watch): UsnVolumeChangeSource Windows adapter over MFTLib broker"`

### Task 5: Refactor CLI --watch onto RepositoryWatchService

**Files:**
- Modify: `git-wizard/Program.Watch.cs`
- Test: existing CLI/`GitWizardTests` watch tests must stay green; add a fake-source test asserting the CLI's printed lines for Created/Deleted/Renamed if a testable seam exists.

**Interfaces:**
- Consumes: `RepositoryWatchService`, `UsnVolumeChangeSource`, `RepositoryChangeEvent`.

- [ ] **Step 1:** Write/adjust a test that drives `RunWatchLoop`-equivalent logic through a fake source and asserts one printed line per event kind (`changed:`/`created:`/`deleted:`/`renamed:`). If the current print path isn't seam-testable, extract a small `IWatchOutput` sink first (its own micro-commit) so it is.
- [ ] **Step 2:** Run — verify FAIL.
- [ ] **Step 3:** Replace the hand-rolled broker/scan/filter/loop in `RunWatchAsync`/`RunWatchLoopAsync` with: build `UsnVolumeChangeSource`, construct `RepositoryWatchService`, `await foreach` its `RepositoryChangeEvent`s, print per kind. Delete the now-dead helpers (`BuildFiltersByDrive`, `WatchDriveAsync`, etc.) that moved into the source/service. Keep `CtrlCCancellation` and `LoadTrackedRepositoryPathsAsync`/`GroupPathsByDrive` if still used, else move them.
- [ ] **Step 4:** Run — verify PASS; manual `git-wizard --watch` smoke on Windows shows `changed:` lines as before plus new kinds.
- [ ] **Step 5:** Format, aislop, commit: `git commit -m "refactor(watch): drive CLI --watch through RepositoryWatchService"`

### Task 6: Phase 1 documentation

- [ ] **Update docs affected by Phase 1** — run the docs-update check across `README.md`, `AGENTS.md`/`CLAUDE.md`, and inline docs. Reflect that `--watch` now also reports created/deleted/renamed repos and that the watch pipeline lives in `GitWizard/Watch`. Commit with the code: `git commit -m "docs(watch): CLI --watch reports full lifecycle; note core watch pipeline"`

---

## Phase 2: UI Live mode (Windows)

### Task 7: LiveWatchController service

**Files:**
- Create: `GitWizardUI/Services/LiveWatchController.cs`
- Test: `GitWizardTests/UI/LiveWatchControllerTests.cs` (fake source, no elevation)

**Interfaces:**
- Produces: `sealed class LiveWatchController` — ctor `(Func<IVolumeChangeSource> sourceFactory, IReadOnlyCollection<string> trackedRoots, IReadOnlyCollection<string> searchRoots, Action<RepositoryChangeEvent> onEvent, Action<string> onStopped, Func<bool> isElevated)`; `Task StartAsync(CancellationToken ct)`, `Task StopAsync()`.
- Behavior: owns the `RepositoryWatchService`; forwards each event to `onEvent`; on `Stopped`, respawns silently iff `isElevated()` returns true, else calls `onStopped(reason)` once.

- [ ] **Step 1:** Write failing tests: (a) events forwarded in order; (b) `SourceDied` with `isElevated()==false` → `onStopped` called once, no respawn; (c) `SourceDied` with `isElevated()==true` → respawn (sourceFactory invoked again), `onStopped` not called.
- [ ] **Step 2:** Run — verify FAIL. `dotnet build git-wizard.slnx -c Release`
- [ ] **Step 3:** Implement the controller (marshaling is the caller's concern; controller stays UI-framework-free so it's testable).
- [ ] **Step 4:** Run — verify PASS. `dotnet test ... --filter LiveWatchController`
- [ ] **Step 5:** Format, aislop, commit: `git commit -m "feat(ui): LiveWatchController with elevation-aware respawn"`

### Task 8: MainViewModel Live state + event application

**Files:**
- Modify: `GitWizardUI/ViewModels/MainViewModel.cs` (add `IsLive`, `CanToggleLive`, `ToggleLiveCommand`)
- Create: `GitWizardUI/ViewModels/MainViewModel.Live.cs` (partial — the live event handlers)
- Test: `GitWizardTests/UI/MainViewModelLiveTests.cs`

**Interfaces:**
- Consumes: `LiveWatchController`, `RepositoryChangeEvent`, existing `AddRepository`, `UpdateCompletedRepository`, `FindRenamedRepo`, `RemoveRenamedReposFromUi` (generalize to a `RemoveRepositoryByPath`).
- Produces: `bool IsLive { get; }` (manual `OnPropertyChanged`, mirrors `IsRefreshing` pattern at `MainViewModel.cs:121`), `AsyncRelayCommand ToggleLiveCommand`, `internal void ApplyLiveEvent(RepositoryChangeEvent ev)`.

- [ ] **Step 1:** Write failing tests (drive `ApplyLiveEvent` directly via the `InternalsVisibleTo` seam): `Changed` → target node `Update()`d; `Created` → node added to `_repositoryMap`/`_allRepositories`; `Deleted` → node removed; `Renamed` (Deleted old + Created new correlated by remote) → old removed, new present.
- [ ] **Step 2:** Run — verify FAIL. `dotnet build git-wizard.slnx -c Release`
- [ ] **Step 3:** Implement `MainViewModel.Live.cs`: `ApplyLiveEvent` switches on kind, reusing existing helpers; correlate Renamed via `FindRenamedRepo` over a short pending-deleted buffer. Add `IsLive` property + `ToggleLiveCommand` that starts/stops the `LiveWatchController`, marshaling `onEvent` to the UI thread via the same `_ui.Post`/dispatcher used by `RefreshAsync` (see `MainViewModel.Refresh.cs` marshaling notes). `onStopped` sets `IsLive=false` + a `HeaderText`/status message.
- [ ] **Step 4:** Run — verify PASS. `dotnet test ... --filter MainViewModelLive`
- [ ] **Step 5:** Format, aislop, commit: `git commit -m "feat(ui): Live mode state + per-repo event application in MainViewModel"`

### Task 9: Live toggle in the view

**Files:**
- Modify: `GitWizardUI/Views/MainWindow.axaml` (add a Live toggle button near Refresh, bound to `ToggleLiveCommand`, with an on/off visual bound to `IsLive`)
- Test: manual (Avalonia view smoke) + assert command wiring in a headless test if the harness supports it.

- [ ] **Step 1:** Add the toggle control (match the existing Refresh button styling/placement). Bind `Command="{Binding ToggleLiveCommand}"`, `Classes.live="{Binding IsLive}"`, tooltip explaining it starts an elevated watch.
- [ ] **Step 2:** Build the UI project. `dotnet build GitWizardUI/GitWizardUI.csproj -c Release`
- [ ] **Step 3:** Launch the app (`scripts/run-preview.ps1` or the built exe), toggle Live on → confirm one UAC prompt, edit a tracked repo, watch its row update live; toggle off → watch stops.
- [ ] **Step 4:** Commit: `git commit -m "feat(ui): Live toggle button in MainWindow"`

### Task 10: Failure-handling polish + Phase 2 docs

**Files:**
- Modify: `GitWizardUI/ViewModels/MainViewModel.Live.cs`, `GitWizardUI/Services/LiveWatchController.cs`
- Modify: `README.md`, `AGENTS.md`

- [ ] **Step 1:** Test: declining elevation (source factory throws the UAC-declined exception) → `IsLive` ends false + status message, no crash. Broker death while elevated → silent respawn (no status). Add to `MainViewModelLiveTests`/`LiveWatchControllerTests`.
- [ ] **Step 2:** Run — verify FAIL, implement, verify PASS.
- [ ] **Step 3:** `git commit -m "feat(ui): elevation-decline + mid-session death handling for Live mode"`
- [ ] **Step 4:** **Docs** — document Live mode (README feature + AGENTS.md behavior note: opt-in, one elevation prompt, auto-updates lifecycle). `git commit -m "docs(watch): Live mode in the UI"`

---

## Phase 3: Linux fanotify backend

### Task 11: fanotify P/Invoke + kernel-floor probe

**Files:**
- Create: `GitWizard/Watch/Linux/FanotifyInterop.cs`
- Test: `GitWizardTests/Watch/Linux/FanotifyInteropTests.cs` (`[Category("RequiresRoot")]`, `[SupportedOSPlatform("linux")]`)

**Interfaces:**
- Produces: `[SupportedOSPlatform("linux")]` P/Invoke wrappers `fanotify_init`, `fanotify_mark`, plus `static bool IsSupported()` (probes kernel ≥5.1 / a successful `FAN_MARK_FILESYSTEM` init, returning false on `ENOSYS`/`EINVAL`/`EPERM`).

- [ ] **Step 1:** Test: on Linux non-root, `IsSupported()` returns false gracefully (no throw). On llamabox root, a marked-and-unmarked temp mount round-trips.
- [ ] **Step 2:** Run on llamabox — verify FAIL (type missing).
- [ ] **Step 3:** Implement the P/Invoke + `IsSupported` (use `FAN_REPORT_DFID_NAME | FAN_CLASS_NOTIF`, `FAN_MARK_FILESYSTEM`).
- [ ] **Step 4:** Run on llamabox — verify PASS.
- [ ] **Step 5:** Format, aislop, commit: `git commit -m "feat(watch): fanotify P/Invoke + kernel-floor probe"`

### Task 12: FanotifyVolumeChangeSource

**Files:**
- Create: `GitWizard/Watch/Linux/FanotifyVolumeChangeSource.cs`
- Test: `GitWizardTests/Watch/Linux/FanotifyVolumeChangeSourceTests.cs` (`[Category("RequiresRoot")]`)

**Interfaces:**
- Produces: `[SupportedOSPlatform("linux")] sealed class FanotifyVolumeChangeSource : IVolumeChangeSource` — arms marks per mount, decodes the fanotify event stream (`FAN_CREATE`/`FAN_DELETE`/`FAN_MOVED_FROM`/`FAN_MOVED_TO`/`FAN_MODIFY`) into `VolumeChangeBatch` using `FAN_REPORT_DFID_NAME` (dir fd + name → full path via `open_by_handle_at`/`/proc/self/fd`). Cold scan = a directory walk of the search roots (synthetic `RecordId`).

- [ ] **Step 1:** Test on llamabox: create/delete/rename files under a temp mount and assert the decoded `VolumeChangeEntry` kinds + paths.
- [ ] **Step 2:** Run — verify FAIL.
- [ ] **Step 3:** Implement the read loop + decode. Keep the fd read loop small; extract path-resolution into a helper. Map moves: `FAN_MOVED_FROM`→Deleted, `FAN_MOVED_TO`→Created (the service correlates into rename downstream).
- [ ] **Step 4:** Run on llamabox — verify PASS. Then run `RepositoryWatchServiceTests` on Linux too (they use the fake — must stay green cross-platform).
- [ ] **Step 5:** Format, aislop, commit: `git commit -m "feat(watch): FanotifyVolumeChangeSource (Linux whole-fs watch)"`

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
- Modify: `GitWizardUI/ViewModels/MainViewModel.cs` (`CanToggleLive` false + tooltip when `FanotifyInterop.IsSupported()` is false on Linux)
- Modify: `README.md`, `AGENTS.md`

- [ ] **Step 1:** Test: on an unsupported kernel (probe returns false), `CanToggleLive` is false and a reason string is set. (Simulate by injecting the probe result.)
- [ ] **Step 2:** Implement, verify PASS.
- [ ] **Step 3:** **Docs** — document Linux Live mode: fanotify, kernel ≥5.1 floor, pkexec prompt, llamabox as the test host. `git commit -m "docs(watch): Linux Live mode (fanotify, pkexec, kernel floor)"`

---

## Phase 4: Unrelated bundled fix — GitWizardUI app icon

*(Not part of the watch feature; bundled on this branch at the user's request. The Avalonia port dropped the window/exe icon.)*

### Task 15: Restore the app + window icon

**Files:**
- Modify: `GitWizardUI/Views/MainWindow.axaml` (add `Icon="/Assets/appicon.png"` to the root `<Window>`)
- Create: `GitWizardUI/Assets/appicon.ico` (converted from `appicon.png`)
- Modify: `GitWizardUI/GitWizardUI.csproj` (add `<ApplicationIcon>Assets\appicon.ico</ApplicationIcon>`)

- [ ] **Step 1:** Generate `appicon.ico` from `Assets/appicon.png` (multi-size 16/32/48/256). Add `Icon="/Assets/appicon.png"` to the `<Window>`; add `<ApplicationIcon>` to the csproj.
- [ ] **Step 2:** Build the UI project. `dotnet build GitWizardUI/GitWizardUI.csproj -c Release`
- [ ] **Step 3:** Launch the app — confirm the window/taskbar icon shows; check the built `.exe` in Explorer shows the icon.
- [ ] **Step 4:** Format check (axaml/csproj), commit: `git commit -m "fix(ui): restore window + exe icon lost in the Avalonia port"`

---

## Branch finish

- [ ] Full suite green Windows (`scripts/run-coverage.ps1`) + Linux on llamabox (`scripts/coverage-linux.sh` or equivalent); 100% managed gate held.
- [ ] `aislop ci .` passes (score 100).
- [ ] Fold durable insight into real docs (ARCHITECTURE / inline docs on `IVolumeChangeSource` and `RepositoryWatchService`), then delete this plan file.
