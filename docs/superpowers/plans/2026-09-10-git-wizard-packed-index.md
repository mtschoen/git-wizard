# git-wizard packed-index port: implementation plan

> **For agentic workers:** Use the executing-plans skill to implement this plan task by task. The controller assigns lanes and commits their results. Checkboxes track implementation, not completion of this planning dispatch.

**Goal:** Port Windows MFT repository discovery and USN watching to `MFTLib.Index.FileIndex`, preserving repository discovery rules, prepared discovery reuse, failure reporting, and Linux behavior.

**Architecture:** First replace the deleted record payload with the broker's actual packed block result while retaining the surviving session lifecycle, then stop at a green boundary on Linux and the Windows non-admin suite (P8/P13). Subsequently adopt `FileIndex` queries and its watch pump, with a small consumer owner for the borrowed broker connection. Repository/report caches and the repository-change classifier remain consumer responsibilities.

**Tech Stack:** C# / .NET 10; all five git-wizard projects target plain `net10.0`; NUnit; Avalonia; LibGit2Sharp; MFTLib submodule fixed at `4c59efa`, consumed through the assembly bridge in `Directory.Build.targets`.

**Tracking:** [git-wizard#176](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/176), filed by the controller 2026-09-10 with the `queue:wait` crew fence.

## Global Constraints

- **P1:** Start from git-wizard `9b25adba0953bfac350d59d0777ffe231b5e351b` and MFTLib `4c59efaa267978908eac5ae3ca880052f68668b0`. Match prefixes `9b25adb` and `4c59efa`, not equality with abbreviated SHAs. Do not move the pin to `975c6e6`: the controller reports release plumbing only between those pins, with no Index/Broker API changes. Plan 3 and this port share `4c59efa`.
- Work only in the dispatched worktree. The controller authorizes its existing cooperative lock. Do not acquire another worktree, clear the controller's lock, alter harness configuration, or modify the submodule. Prerequisite assembly builds below are controller-provisioned when a lane is forbidden even generated writes under `external/MFTLib`.
- **P4:** `FileEntry.Path` is a logical Windows path (`X:\a\b`), including on an enumeration index over a Linux temp tree. It is not generally an openable host path. `LookupEngine.TryParseDriveLetter` rejects Unix absolute paths. Keep `FindGitRepositoriesRecursively` for Linux and `--no-mft`; use FileIndex in production only for Windows MFT discovery and USN watch. This restriction makes Gap 7 non-blocking.
- Every enumeration-temp-tree test asserts index structure: `Name`, `IsDirectory`, `Parent`, and `FindByName` results. Never pass its `FileEntry.Path` to `Directory.Exists`, LibGit2Sharp, or `FileStream`, even on Windows where its synthetic root can differ from the real root. Use `entry.Open(FileAccess.Read)` if bytes are needed. Real filesystem tests use independently constructed host paths and the recursive scanner; Windows MFT integration tests use actual volume-rooted MFT entries.
- **P5:** The final Windows source consumes `FileIndex.Changed` after `StartWatchingAsync` (MFTLib#139: pump started, not catch-up drained). Delete its record-number dictionary, USN parsing, cursor bookkeeping, and explicit catch-up replay. `FanotifyVolumeChangeSource` and Linux fanotify files stay untouched. `VolumeChangeSourceFactory` keeps platform selection.
- **P6:** `BrokerScanOptions.Profile = BrokerScanProfile.DirectoryIndex` and `KeepFileNames = [".git"]` filter at the producer. Do not scan Full and filter away ordinary files in git-wizard. `FindByName(".git")` finds markers, `IsDirectory` enforces the existing drive-root rule, and `Parent.Path` supplies Windows repository roots. Scoped searches must also keep `.git` files for worktrees/submodules.
- No compatibility shims: no recreated `ScanRecord`, DTO with its old fields, old-signature forwarding overloads, reflection into MFTLib, or fabricated library APIs. Phase 1 uses native `BlockFile`/`BlockScanOutcome` inputs directly; it does not adapt blocks into record objects.
- **Reporting that an API does not exist IS the deliverable, not a blocker to work around.** If a task needs an MFTLib member you cannot find, stop and say so by name. Do not invent one, do not approximate it with reflection, and do not quietly substitute a different mechanism and carry on. Observed on plan 3 the same day: a lane called `BlockCacheCatalog.Read(...)`, a type that exists nowhere in MFTLib, when the honest finding - that MFTLib exposes no cached-drive enumeration - was itself the valuable result. A correct finding silently papered over is worse than a blocked task, because it costs a whole fix round and buries a real library gap.
- **If the real test runner will not run, report NO numbers.** Never substitute a different harness and report its output as the suite result. Observed on plan 3 the same day: a lane reported VSTest "blocked by sandbox sockets", swapped in an in-process xUnit harness, and returned 677 passed / 3 failed for a tree that did not compile - it had measured a stale assembly, and the real cause was a build error the lane had introduced itself. A blocked runner is a report of "blocked, here is the error"; numbers from a substitute are not evidence about this tree. Pair this with the `--no-build` note below: before concluding a runner is unavailable, check that the build actually succeeded and that its output assembly exists.
- No real wall-clock assertions in tests. Inject `TimeProvider` if adding clock-dependent behavior. Use state-driven signals or condition polling with a cancellation bound, never fixed sleeps as readiness. This plan adds no timing behavior.
- Use full words in new identifiers. Existing third-party parameter names in quoted signatures are reproduced exactly; inherited interface parameter names must remain analyzer-consistent.
- No em-dashes (U+2014) in generated prose, code, comments, or messages. No machine-specific absolute paths in shipped code.
- Keep code files near or below 500 lines; split by responsibility when useful. If a cohesive existing large file must remain large, retain or add the short rationale required by project guidance.
- `dotnet test --no-build` is valid immediately after a successful build of the same configuration. The reported invalid assembly argument followed the 47-error build, which never produced `GitWizardTests/bin/Release/net10.0/GitWizardTests.dll`; it was not a runner limitation. CI correctly uses `--no-build` on both platforms. A downstream tool error after a failed build says nothing about tool support; verify its prerequisite artifact first. Existing verification commands without the flag remain correct, though slower.
- Do not add or commit from a linked-worktree lane. Leave coherent changes for the controller to inspect and commit. Task 1 is committed alone by that controller.
- **P8:** Only Phase 1 may have red intermediate tasks. Tasks 3, 8, and 11 are explicit phase boundaries: Linux green plus the Windows non-admin suite for any phase that changes Windows-only behavior (P13). Do not begin chosen migration work before Task 3 passes on both platforms. Task 7 also has its own Windows non-admin gate before proceeding.
- **P10:** Production uses `NoCache = true`; persistent inventory caching is a separate feature outside plan 4 (Task 4).
  - Historical ruling: P10 described the original packed-index port. Production cache policy is superseded by [Git-wizard issue 247 (persistent discovery index foundation)](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/247). Its dedicated policy namespace, ownership, snapshot semantics and delete-all guard now apply; the original port's no-cache measurements remain historical.
- **P11:** Retain the staged forced/chosen boundary and pay the bounded reader cost (Task 2); do not combine first FileIndex adoption with forced payload replacement.
- **P12:** [MFTLib#139](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/139) tracks the missing initial catch-up fence. Live means "the pump started", never "catch-up has drained" (Tasks 6-8/10).
- **P13:** The controller runs `scripts/verify-windows.sh` at the Windows gates, including after Task 7 and at Task 11. It excludes `RequiresAdmin`; those tests remain attended, and no elevated result may ever be reported from this script.
- **P14:** The `.git` name match is ordinal today, on both sides of Task 2 (`record.Name == GitEntryName` before, `ReadRowName(...).SequenceEqual(GitEntryName)` after), while MFTLib's producer-side `KeepFileNames` filter is case-INSENSITIVE (`MFTLib.Tests/JournalBrokerHostTests.Scan.cs`, `ServeOnce_DirectoryIndexProfile_KeepFileNameMatch_IsCaseInsensitive`). A block can therefore contain a `.GIT` row that the consumer drops. Task 4 must establish from pinned source whether `FileIndex.FindByName` is case-insensitive and report it; Task 7 must then either preserve ordinal matching explicitly or adopt case-insensitivity deliberately, with a named test and a line in Task 10's documentation. Drifting into a behaviour change unnoticed is not acceptable; either choice, made out loud, is.
- **P15:** Task 3 adds one narrow production seam. `UsnVolumeChangeSource.ArmSessionAsync` evaluates `GitWizardApi.CreateGitDiscoveryScanOptions(drives)` as an argument to `JournalBrokerScanSession.StartAsync`, so the Windows-only `IndexedDrive.FromWindowsVolume` lookup runs BEFORE the injected broker launcher, making four required prepared-session/Live fallback definitions unobservable on Linux. Add an internal injectable `Func<IReadOnlyList<string>, BrokerScanOptions>` defaulting to `CreateGitDiscoveryScanOptions`. `IndexedDrive`'s primary constructor is public, so portable tests build a drive identity directly with no reflection and no MFTLib change. This is an injection seam of the same shape Task 4 already specifies, not a compatibility shim, and it is deleted with `UsnVolumeChangeSource.cs` in Task 7. Do not instead add Linux skips: that discards required coverage and hides a real Windows path from every gate.
- No changes to `.aislop/config.yml`, coverage exclusions, or analyzer severity to hide failures. Preserve the bridge until its existing retirement issue `git-wizard#134` is fulfilled separately.

## Evidence, scope, and corrections to older inputs

The authoritative input is `.superpowers/sdd/2026-09-10-git-wizard-packed-index/inputs/consumer-map-git-wizard.md`; the older `gap-git-wizard.md` is historical. The design's sections 5.5, 6, and 8 supply intent, but actual pinned source supplies API contracts.

**P2 corrected history:** Every `TargetFramework` in this git-wizard checkout and its pinned MFTLib is `net10.0`; the six `net8.0` occurrences are path literals naming MFTLib's build output directory, not target-framework declarations. git-wizard main is self-consistent: its recorded MFTLib pin `795d36a` genuinely targets `net8.0`, and MFTLib only moved to `net10.0` later in `1c2f1c4` (MFTLib#77). The pin move in `9b25adb` invalidates those paths, so the bridge-related red interval between that commit and Task 1 completes a pin move rather than fixing a pre-existing bug on main; the forced ScanRecord errors remain after Task 1.

**P9 bridge divergence:** Read-only comparison with file-wizard's packed-index `Directory.Build.targets` found one `<_MFTLibTfm>net10.0</_MFTLibTfm>` property at line 49, six interpolated forward-slash paths at lines 61-63 and 80-82, and managed-reference `<Error>` guards opening at lines 99 and 101 (target spans 97-103). Its comment at line 92 explains the trap: MSBuild only warns about a nonexistent `HintPath`, drops the reference, and later emits misleading type-not-found errors. git-wizard has six literal paths and only native guards (Windows error at line 84, Linux warning at line 98). The irony is that git-wizard's own lines 37-38 already cite "types (CS0246) and the lint step fails (file-wizard precedent)": the derived bridge names the failure mode but never carried across the managed guard or single-property shape. Task 1 adopts that shape. A literal rots silently on the next TFM move and costs a reader 47 misleading errors; a named property plus a named error costs them one actionable line.

The controller measured 47 build errors before correcting it and exactly 15 production diagnostics afterwards, all `CS0246 ScanRecord`:

| File | Count | Base line numbers |
| --- | ---: | --- |
| `GitWizard/GitWizardApi.PreparedDiscovery.cs` | 5 | 17, 42, 75, 116, 144 |
| `GitWizard/Watch/UsnVolumeChangeSource.cs` | 3 | 39, 56, 148 |
| `GitWizard/MftDiscoveryResult.cs` | 2 | 13, 19 |
| `GitWizard/GitWizardReport.DiscoverySession.cs` | 2 | 19, 30 |
| `GitWizard/GitWizardApi.Discovery.cs` | 2 | 16, 29 |
| `GitWizard/Watch/VolumeChangeSourceFactory.cs` | 1 | 63 |

The captured list is `.superpowers/sdd/2026-09-10-git-wizard-packed-index/red-baseline-after-hintpath.txt`. This is a measured initial compiler inventory, not a promise that fixing type declarations exposes no further method or test errors. `JournalBrokerScanSession`, `UsnJournalEntry`, `BrokerScanProfile`, `IElevationProvider`, `ElevationUtilities`, `BrokerLauncher`, and `ElevatedEntryPoint` still exist. **Only ScanRecord is a forced production type replacement. Adopting FileIndex ownership and Changed is a chosen migration, not a compilation requirement.** The old session callback overload is also gone: retaining the type requires calling its new options/block API.

This distinction costs a real intermediate step. Phase 1 reads native packed columns for discovery and for seeding the existing watch dictionary; Phase 2 deletes that reader and the dictionary in favor of library queries and mutation. It avoids disguising the entire chosen redesign as a compile fix. It is bounded transition work, not a new compatibility surface. P11 accepts that cost; Task 2 records why the green boundary is mandatory.

**P3 measured size:** the four large test sources contain 425 + 391 + 273 + 206 = **1,295 lines**, measured at this checkout. The survey lists each one line higher (1,299 total); the brief's phrase "over 1,300" is approximate. `ReportDiscoverySessionTests.cs`, absent from the survey table, adds 89 lines, making **1,384 lines in these five files alone**. The campaign's roughly 145-line estimate is a production-only floor, not a test rewrite estimate. The staged native-block step, connection ownership, and per-drive error delivery also expand production scope beyond seven files. Eleven tasks across three phases reflect that evidence.

### Pinned API corrections

| Older statement | Actual pinned code and consequence |
| --- | --- |
| Gap 1: watch lifecycle is missing | `FileIndex.Watch.cs` and `IIndexWatchSource.cs` implement it. No library change needed. |
| Gap 2: omitted ordinary-file changes disappear | `JournalMutator.TryHydrateRow` hydrates omitted rows. Test modification and deletion through the consumer source. |
| Gap 3: only PreviousName is available | `FileChange(Kind, Entry, Path, PreviousPath)` stores mutation-time paths. Never classify a delayed event using live `Entry.Path`. |
| Gap 4 proposes IndexDriveScanException / ScanErrorMessage | Those are not the shipped API. Use `DriveState.Failed`, `MftProducerFailureMessage`, `WatchFailureMessage`, and `WatchFault`. |
| Gap 5 lacks volume identity acquisition | Use public `IndexedDrive.FromWindowsVolume(string drive)`; do not add P/Invoke or use serial zero for a real volume. |
| Gap 6 expects managed-only temporary cleanup | `BlockFile` and named sections use OS delete-on-close. A held FileEntry keeps its snapshot mapped even after index disposal; do not assert immediate unlink while handles remain reachable. |
| Gap 7 needs native enumeration paths | Still open. P4 retains the recursive scanner. No path translation layer is added. |
| Spec 5.5 implies a public Scan escape hatch | `FileIndex.Scan`, `FileEntry.Create`, and query engines are internal. Public queries return materialized lists. Do not invoke these internals. |
| Older producer policies include Auto / MftOnly | `ProducerPolicy` has exactly `Mft` and `Enumeration`. There is no automatic enumeration fallback inside the index. |
| Old gap analysis says MFT FileEntry.Open is unfinished | It now opens by file ID and sequence number on Windows. The port still opens repositories by their real Windows paths through LibGit2Sharp. |
| Session test harness comment says caller owns LatestScan blocks | Actual `JournalBrokerScanSession.DisposeAsync` and rescan dispose them. Session ownership wins over that stale comment. |
| Enumeration is sufficient for live-pump tests | `StartWatchingAsync` only targets MFT-kind blocks. Enumeration is the structural substrate; explicit MFT-kind test blocks plus scripted `IIndexWatchSource` exercise the pump on Linux. |

**P12:** `StartWatchingAsync` returns when the pump is scheduled, not when all drives acknowledge readiness. It exposes neither a readiness acknowledgment nor a catch-up-complete fence. [MFTLib#139](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/139), "Index: no way to await the initial watch catch-up, so a consumer cannot do a bounded synchronous refresh", is open with `queue:wait` and `triage-pending`, filed from plan 3. This gap also explains why the file-wizard port retired `--refresh` and `--show-moves`. The plan does not invent a fence. Consumer `Started` and git-wizard's Live indicator mean "the pump started", never "catch-up has drained"; subsequent faults remain explicit.

## Verification baseline and test-count budget

Controller measurements are accepted without rerunning the broken build during this plan-only dispatch: **1,128 definitions; 1,114 executed; 1,110 passed; 0 failed; 18 skipped across all mechanisms**. Do not reinterpret the displayed executed count as the definition count; compare discovered definitions and named skips separately.

The intended budget preserves one successor per existing test, including semantic replacements for deleted files. Add exactly 35 independent new cases: Task 2 adds 4; Task 4 adds 12; Task 5 adds 5; Task 6 adds 8; Task 7 adds 2; Task 8 adds 4. Tasks 3 and 9 rewrite existing cases without changing their count. These are planned counts, not measurements of unwritten tests. If an implementer splits/combines cases, update this ledger with names and reasons before closing a phase.

| Boundary | Definitions | Passed on unprivileged Linux | Skipped, all mechanisms | Failed |
| --- | ---: | ---: | ---: | ---: |
| Task 3: forced replacement green | 1132 | 1121 | 11 | 0 |
| Task 8: chosen migration green | 1163 | 1153 | 10 | 0 |
| Task 11: final | 1163 | 1153 | 10 | 0 |

Seven Windows attributes in BrokerDiscoveryTests disappear in Task 3 when policy, ownership, and wire assertions become portable. Task 7 removes the factory test's runtime Windows skip because source construction itself no longer invokes Windows APIs and Task 7 explicitly drops the real-OS conjunct from the factory Windows selection so the existing override seam can select that branch; native opening remains guarded and lazy. No new Windows skips are budgeted.

The ten expected remaining Linux skips are named, not a blanket platform excuse:

1. `ElevationDecisionTests.TryFindAllRepositoriesUsingMftAsync_ElevatedWithEmptySearchPaths_ReturnsFalse`.
2. `ElevationCoverageTests.TryFindAllRepositoriesUsingMftAsync_Elevated_RealMftScanDoesNotThrow` (rewritten in Task 9).
3. `UsnVolumeChangeSourceTests.ArmAndCatchUpAsync_RealBroker_ReturnsNonNullErrors` (successor in Task 9).
4. `UsnVolumeChangeSourceTests.WatchAsync_RealBroker_DetectsCreatedFile` (successor in Task 9).
5. `UsnVolumeChangeSourceTests.WatchAsync_RealBroker_ReplaysCatchUpEntriesWithoutThrowing` (strengthened successor in Task 9).
6. `CoverageBoostDiscoveryTests.TryFindAllRepositoriesUsingMftAsync_ElevatedProvider_EmptySearchPaths_ReturnsFalse`.
7. `GitWizardApiDiscoveryTests.GetRepositoryPaths_SkipsHiddenAttributeDirectory`.
8. `GitWizardConfigurationTests.CreateDefaultConfiguration_SearchPath_ContainsUserProfile_OnWindows`.
9. `FanotifyInteropTests.Probe_RealFanotify_MarksAndUnmarksTemporaryFilesystem` (root/capability required).
10. `FanotifyVolumeChangeSourceTests.RealFanotify_DetectsFileCreationAndDeletion` (root/capability required).

All five solution projects build on Linux, including the Avalonia UI and screenshot tool. Linux verification is near-complete; Windows adds real NTFS/broker/elevation behavior and native deployment, not a second implementation of the whole port.

## Phase 1: forced packed-payload replacement, then green

### Task 1: Complete the pin move by adopting file-wizard's assembly bridge shape

**Files:** Modify only `Directory.Build.targets`: the MFTLib.dll property/reference group at lines 39-48 and the MFTLibTestExtensions group at lines 55-64 and a new managed-reference verification target before reference resolution. Read file-wizard's `Directory.Build.targets` for shape only; do not write to or run git commands in its worktree. No C# edits in this task.

**Interfaces:** One MSBuild `_MFTLibTfm` property supplies all six `_MFTLibDll` and `_MFTLibTestExtensionsDll` path assignments and their condition operands, preserving the existing Release/Debug fallback. A named managed-reference verification target runs before `ResolveAssemblyReferences`, scoped to non-submodule C# inner builds; the test-extension guard applies only to `GitWizardTests`. The referenced assemblies still export the surviving MFTLib types listed above.

**Steps:**

- [ ] **Red first:** use the controller's 47-error bridge baseline as the regression test. The broken paths prevent dependency resolution; do not create a C# test that cannot compile.
- [ ] Introduce one `<_MFTLibTfm>net10.0</_MFTLibTfm>` property before the managed path groups. Interpolate `$(_MFTLibTfm)` in all six path assignments, including both condition operands and path values, replacing every `net8.0` literal. Preserve configuration fallback and native targets; add no compatibility shim.
- [ ] Adopt file-wizard's managed path shape with forward slashes in the two base paths and six derived paths. Separator conversion is for consistency, not an observed Linux resolution fix: a scratch `dotnet msbuild -getProperty` evaluation on this Linux host returned `true` from `Exists` for both forward-slash and backslash spellings of the same existing `Directory.Build.targets` file. This establishes separator acceptance in MSBuild path checks, not a successful consumer build; the measured build gate below remains authoritative.
- [ ] Add a named target before `ResolveAssemblyReferences`, modelled on file-wizard's managed guards and git-wizard's existing native error. Exclude MFTLib submodule projects, non-C# projects, and outer builds with empty `TargetFramework`; restore must remain possible before the dependency is built. Add an `<Error>` for missing `$(_MFTLibDll)` and another for missing `$(_MFTLibTestExtensionsDll)` scoped to `GitWizardTests`. Each message names its missing assembly and resolved path, says "If MFTLib has changed target framework, update _MFTLibTfm in Directory.Build.targets", and supplies its respective command: `dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c $(Configuration) -p:Platform=x64` or `dotnet build external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj -c $(Configuration) -p:Platform=x64`. Include the submodule initialization remedy. This makes a missing managed reference fail at its cause instead of silently dropping into misleading type errors.
- [ ] Verify each managed guard using a command-line override of its resolved DLL property to a nonexistent scratch path and the reference-resolution target: core guard on a production project, test-extension guard on `GitWizardTests` with the core DLL present. Expect the named error with the correct build command and TFM remedy before compilation. Do not remove real DLLs; confirm restore is not blocked. Then remove the overrides for the unchanged measured gate below.
- [ ] Have the controller provision both pinned managed assemblies, then measure the diagnostic delta. The controller commits this one-file change alone after the gate below; the tree intentionally remains red.

**Verification:**

```bash
dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
dotnet build external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj -c Release -p:Platform=x64
dotnet build GitWizardTests/GitWizardTests.csproj -c Release
```

Expected: dependency builds exit 0; consumer build exits nonzero with **15 unique production errors**, all `CS0246 ScanRecord`, exactly the six files/base locations above, down from 47. Deduplicate repeated compiler output from build summary when comparing with the captured list. No missing `IElevationProvider`, `JournalBrokerScanSession`, `BrokerScanProfile`, or `UsnJournalEntry`. No tests execute yet.

### Task 2: Rewrite production callers for native block results, retaining session behavior

**P11 accepted staging cost:** Seed and select native blocks while the session and raw-watch lifecycle still exist, then delete this bounded reader in Task 7. Plan 3's Phase 1 ran red for its whole length: its test project did not compile until the phase's final task, so every test-side error arrived in one lump at the end. A green boundary prevents that recurrence. Pay this transition cost; do not combine the first FileIndex adoption with the forced payload replacement.

**Files:**

- Create: `GitWizard/GitWizardApi.PackedBlocks.cs`; `GitWizardTests/PackedBlockDiscoveryTests.cs`.
- Modify: `GitWizard/GitWizardApi.Discovery.cs`, `GitWizard/GitWizardApi.PreparedDiscovery.cs`, `GitWizard/MftDiscoveryResult.cs`, `GitWizard/GitWizardReport.DiscoverySession.cs`, `GitWizard/Watch/UsnVolumeChangeSource.cs`, `GitWizard/Watch/VolumeChangeSourceFactory.cs`.
- Modify: `GitWizardUI/ViewModels/PreparedDiscoverySession.cs`, `GitWizardUI/ViewModels/MainViewModel.Refresh.cs`, `GitWizardUI/ViewModels/MainViewModel.Live.cs`; `GitWizard/GitWizard.csproj` comment about the removed type.
- No deletion of the surviving session, direct elevated `MftVolume` branch, or raw journal watch in this task.

**Interfaces:** Exact library signatures from the pinned source:

```csharp
// MFTLib namespace
public static Task<JournalBrokerScanSession> StartAsync(
    Func<string, bool> launchBroker, IReadOnlyList<string> drives,
    BrokerScanOptions options, CancellationToken cancellationToken = default);
public sealed record BlockScanTarget(string Path, uint VolumeSerial, bool DeleteOnClose);
public sealed record BlockScanOutcome(string SectionName, BlockFile Block,
    long RowCount, long NamePoolUsedBytes, long SkippedRecordCount);
// BrokerScanResult
public IReadOnlyDictionary<string, BlockScanOutcome> BlockOutcomes { get; }
// MFTLib.Index namespace
public static IndexedDrive FromWindowsVolume(string drive);
public static ReadOnlySpan<char> ReadRowName(BlockFile block, uint rowIndex);
// BlockFile: public ref BlockHeader Header; public Span<FileRow> Rows.
// FileRow: ParentRow, IsInUse, IsDirectory, IsDeleted.
```

New consumer helpers, removed in Task 7 after their callers migrate:

```csharp
internal static IEnumerable<string> SelectGitEntryPaths(
    IReadOnlyDictionary<string, BlockScanOutcome> blocks, bool includeGitFiles);
internal static string ResolvePackedBlockPath(BlockFile block, char driveLetter, uint rowIndex);
```

Replace all `IReadOnlyList<ScanRecord>` parameters/properties with `IReadOnlyDictionary<string, BlockScanOutcome>` and rename `Records`/`ScanRecords` to `Blocks`/`ScanBlocks`. This rewrites callers, rather than preserving the old record shape. The session remains the block owner. The same tuple handoff carries `(Session, Blocks, Drives)`; blocks are borrowed from that session, never independently disposed by the result or UI.

**Steps:**

- [ ] **Red first:** write four `PackedBlockDiscoveryTests`: `DirectoryAndMarkerFileSelection`, `IgnoresFreeAndDeletedRows`, `RootRowNeedNotBeZero`, `MalformedParentChainIsRejected`. Assert actual row names/flags and bounded parent relationships using enumeration-produced temp blocks for the first two and a small explicit block for the latter two. They fail because the new native-block helpers do not exist; compilation of old record users is still red.
- [ ] Resolve completed cold blocks directly. Enumerate live rows below `Header.RowCount`, match `.git` and the include-files rule, and call the bounded path helper only for matches. Use `NamePool.ReadRowName`, never separate live name offset/length reads. Stop at `Header.RootRow`; reject missing/free parents, cycles, out-of-range rows, and depth beyond `BlockLayout.MaximumPathDepth` with `InvalidDataException`. Build Windows syntax explicitly; do not use host `Path.Combine` for these logical paths.
- [ ] Preserve `CollectGitRepositories`' real Windows disk validation and the existing pure host-path candidate tests. Enumeration-based tests stop before disk validation. Keep recursive scanning unchanged.
- [ ] Allocate unique per-scan block filenames in a protected consumer directory derived from `GitWizardApi.GetLocalFilesPath()`; `CacheDirectory.EnsureCreated` applies protection. Build `BlockTargets` with actual `IndexedDrive.FromWindowsVolume` serials and `DeleteOnClose = true`. Call the new `StartAsync(launch, drives, options, token)` signature with DirectoryIndex and `.git` retention. Do not keep a batch callback or manufacture records. The stable protected directory may remain; every block uses OS delete-on-close, including session launch/failure cleanup. No per-session directory lifetime needs a second owner.
- [ ] Read `session.LatestScan.BlockOutcomes` for discovery and seed the existing USN dictionary directly from live block rows and `ResolvePackedBlockPath`. Keep current catch-up and `WatchDriveAsync` behavior for this phase. Use the same block target construction for discovery and cold watch. Do not dispose blocks before the session, or access them after disposing it.
- [ ] Change the six core signatures plus UI payload handoff together. Preserve no-MFT, platform, empty roots, UAC decline, no repositories, callback-failure disposal, prepared drive matching, and unarmed-session disposal. The direct elevated `MftVolume` branch still resolves and remains unchanged until the chosen phase.

**Verification:**

```bash
dotnet build GitWizard/GitWizard.csproj -c Release
dotnet build GitWizardUI/GitWizardUI.csproj -c Release
dotnet build git-wizard/git-wizard.csproj -c Release
dotnet build GitWizardTests/GitWizardTests.csproj -c Release
```

Expected: all three production builds exit 0; **0 production references to ScanRecord**. The test build remains red only in the explicitly dispositioned old test fixtures/harness calls. Record this newly exposed test-error list rather than inventing a count. Task 3 closes it before Phase 2 starts. The native-block reader is not the final architecture and cannot survive Task 7. Do not persist its borrowed results or turn it into a generic block navigation API.

### Task 3: Rewrite affected fixtures and close the forced phase green

**Files:**

- Create: `GitWizardTests/TestSupport/PackedIndexFixture.cs`, `GitWizardTests/TestSupport/BlockBrokerFixture.cs`, `GitWizardTests/TestSupport/DuplexStream.cs`.
- Rewrite: `GitWizardTests/BrokerDiscoveryTests.cs`, `GitWizardTests/GitFileScanRecordMappingTests.cs`, `GitWizardTests/Watch/UsnVolumeChangeSourcePreparedSessionTests.cs`, `GitWizardTests/UI/MainViewModelPreparedSessionTests.cs`, `GitWizardTests/ReportDiscoverySessionTests.cs`.
- Modify: `GitWizardTests/Watch/VolumeChangeSourceFactoryTests.cs` signature call; `AGENTS.md` Phase 1 architecture/build wording. `CLAUDE.md` is an import and stays unchanged.

**Interfaces:**

```csharp
// MFTLibTestExtensions, public and actually exported
public static Task<JournalBrokerScanSession> StartScannedAsync(
    Func<CancellationToken, Task<JournalBrokerClient>> connectAsync,
    IReadOnlyList<string> drives, BrokerScanOptions options,
    CancellationToken cancellationToken = default);
// MFTLib public constructor
public JournalBrokerClient(Stream pipe,
    Func<string, BlockFileCreateOptions,
        (string SectionName, BlockFile Block, IDisposable Lifetime)> createDriveBlockSection);
// MFTLib.Index
public static BlockFile Create(BlockFileCreateOptions options);
public EnumerationProducer(EnumerationProducerOptions options);
public EnumerationResult Produce(BlockWriter writer,
    IProgress<IndexScanProgress>? progress, CancellationToken cancellationToken);
public void Complete(DateTime scanTimestampUtc); // BlockWriter
// BrokerProtocol
public static void WriteScanReady(IBufferWriter<byte> writer, string mmfName,
    long rowCount, long namePoolUsedBytes, long skippedRecordCount);
public static void WriteJournalBatch(IBufferWriter<byte> writer, string drive,
    uint armEpoch, UsnJournalCursor cursor, UsnJournalEntry[] entries);
```

The fixture owns its temp tree, block directory, and any independent enumeration index. A returned `JournalBrokerScanSession` owns its own blocks and client. Do not reach into MFTLib.Tests test helpers: those helpers are source examples, not exported TestExtensions APIs.

**P15 seam, do this first:** make the scan-options construction injectable on
`UsnVolumeChangeSource` (an internal `Func<IReadOnlyList<string>, BrokerScanOptions>` defaulting to
`GitWizardApi.CreateGitDiscoveryScanOptions`). Without it the fresh-session fallback calls
`IndexedDrive.FromWindowsVolume` before the injected launcher is reached, and four required
definitions cannot run on Linux at all. Build portable options from a directly constructed
`IndexedDrive`; its primary constructor is public.

**Steps:**

- [ ] **Red first:** retain the named assertions from the disposition table and rewrite their setup to native blocks. `BrokerScanAsync_SendsGitDiscoveryProfileAndKeepFileName_OverTheWire` must fail against old constructor/callback/protocol calls. `ReportDiscoverySessionTests.JsonSerialization_IgnoresDiscoveryProperties` must reject leaked block/session properties, not merely serialize an empty object.
- [ ] Build a portable duplex client using the new block-section factory and `BlockFile.Create`. Keep a separately owned disposable section-lifetime token; disposing it must not unmap the block. Replace `IMmfReader`, fake ScanPayloads, and old WriteScanReady calls outright. Share the duplex fixture instead of copying three hundred-line transport helpers again.
- [ ] Answer the client's volume-geometry query before ArmAndScan; populate the exact block the client created, stamp the armed cursor and a fixed completion timestamp, and send the three-count ScanReady. During live watch, echo the arm epoch from the StartWatch frame. Reply to EndWatch with EndWatchAck. No sleeps, forged zero-row successful blocks, or named Windows sections in portable tests.
- [ ] Replace the seven Windows-gated broker tests with portable producer-options, marker-structure, no-MFT, failure, and ownership cases over the new seams. Exercise collection policy separately from Windows-only filesystem validation. Preserve one successor for each definition and remove their seven platform gates. The no-MFT guard still precedes any injected scan invocation.
- [ ] Keep the existing session ownership assertions in prepared-source/UI tests for this phase, now using blocks. Preserve exact disposal, matching/mismatched drives, invalid session fallback, cold catch-up, first Live reuse, second start, and elevated respawn behavior. Rewire all five report tests, which the survey missed.
- [ ] Correct AGENTS.md's assembly/test instructions and Phase 1 payload description. Do not claim FileIndex owns live watching until Task 7. Keep the four measured new reader tests from Task 2; no test suppression or category changes beyond the seven justified gates.

**Verification:**

```bash
dotnet build GitWizardTests/GitWizardTests.csproj -c Release
dotnet build git-wizard.slnx -c Release
dotnet test GitWizardTests/GitWizardTests.csproj -c Release
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --list-tests
rg -n '\bScanRecord\b|IMmfReader|mmfReader:|createDriveMmf:' GitWizard GitWizardUI git-wizard GitWizardTests -g '*.cs'
```

Expected: builds and full suite exit 0; **1132 definitions, 1121 passed, 11 named skips, 0 failures**. Final `rg` has no matches (exit 1 is the desired search result). All production and test callers use actual block APIs. This is the mandatory green boundary before any chosen migration. Capture a fresh coverage baseline here for the final no-regression comparison:

```bash
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --collect:"XPlat Code Coverage" --results-directory TestResults/phase1
python3 ci/post-coverage-status.py --cobertura "TestResults/phase1/**/coverage.cobertura.xml" --gate-line 55 --summary --skip-post
```

Expected: the same full-suite result, coverage gate exit 0 and line coverage at least 55%. Record actual line/branch percentages in the controller's phase evidence; do not substitute the historical Windows report.

**P8/P13 phase exit:** After the Linux gate above, the controller runs `scripts/verify-windows.sh` with the published phase-exit reference, using the Task 7 invocation and evidence requirements. Require Windows non-admin success for this exact phase result; an earlier Task 7 result does not cover subsequent changes. This excludes `RequiresAdmin` and supplies no elevated evidence.

## Phase 2: chosen FileIndex query, ownership, and watch migration

### Task 4: Add the FileIndex owner and repository-marker query

**Files:** Create `GitWizard/MftIndexSession.cs`, `GitWizard/MftIndexSession.Windows.cs`, `GitWizard/GitWizardApi.IndexDiscovery.cs`, `GitWizardTests/MftIndexSessionTests.cs`, `GitWizardTests/IndexDiscoveryTests.cs`. Extend `GitWizardTests/TestSupport/PackedIndexFixture.cs` and `BlockBrokerFixture.cs` for index use.

**Interfaces:** Exact library calls, in `MFTLib.Index` unless noted:

```csharp
public static Task<FileIndex> OpenAsync(FileIndexOptions options,
    CancellationToken cancellationToken);
public IReadOnlyList<FileEntry> FindByName(string name);
public FileEntry? Find(string fullPath);
public FileEntry Root(char drive);
public FileEntry? Parent { get; } // FileEntry
public string Path { get; }      // FileEntry
public bool IsDirectory { get; } // FileEntry
public ValueTask DisposeAsync(); // FileIndex and JournalBrokerClient
// MFTLib namespace
public BrokerMftBlockProducer(
    Func<CancellationToken, Task<JournalBrokerClient>> connectAsync,
    BrokerScanOptions? scanOptions = null,
    Action<BrokerScanResult>? scanCompleted = null);
public MftBlockProducer CreateProducer();
public IIndexWatchSource CreateWatchSource();
public static Task<JournalBrokerClient> SpawnAndConnectAsync(
    Func<string, bool> launchBroker, CancellationToken cancellationToken = default);
```

Proposed consumer contract, not a claimed MFTLib type:

```csharp
public sealed partial class MftIndexSession : IAsyncDisposable
{
    public FileIndex Index { get; }
    public IReadOnlyList<string> Drives { get; }
    public IReadOnlyDictionary<string, string> ScanErrors { get; }
    public bool IsFaulted { get; }
    public bool IsDisposed { get; }
    internal MftIndexSession(FileIndex index, IAsyncDisposable connectionOwner,
        IReadOnlyList<string> drives, IReadOnlyDictionary<string, string> scanErrors);
    public static Task<MftIndexSession> OpenWindowsAsync(
        IReadOnlyList<string> drives, Func<string, bool> brokerLauncher,
        CancellationToken cancellationToken);
    internal static Task<MftIndexSession> OpenAsync(
        IReadOnlyList<IndexedDrive> drives,
        Func<CancellationToken, Task<JournalBrokerClient>> connectAsync,
        string cacheDirectory, CancellationToken cancellationToken);
    public ValueTask DisposeAsync();
}
internal static IReadOnlyList<FileEntry> SelectGitEntries(FileIndex index, bool includeGitFiles);
```

The internal constructor is also the ownership seam for enumeration-based tests: pass a real enumeration FileIndex and a counting async lifetime, then verify the real index is closed. Production passes its actual JournalBrokerClient through the same constructor. The owner exists because the producer borrows its client. It is not a replacement metadata database. It retains only one FileIndex, requested drive tokens, and connection lifetime; no record list, cursor map, or repository dictionary.

**Steps:**

- [ ] **Red first:** add eight owner tests: `ConnectsOnceAcrossDrives`, `DiscoveryAndWatchShareClient`, `DisposeClosesIndexThenClient`, `CancellationClosesClient`, `OneDriveFailureKeepsOtherDrive`, `AllDrivesFailDoesNotLeak`, `UsesDirectoryProfileAndMarkerRetention`, `NoCacheRequestsDeleteOnClose`. They fail because the owner and option wiring do not exist. Use the portable real client/producer fixture for connection/transport cases.
- [ ] Add four structural query tests: `MarkerDirectoryParent`, `ScopedMarkerFileParent`, `DriveRootExcludesMarkerFiles`, `CaseInsensitiveMarkerLookup`. Build real enumeration indexes over temp trees; assert parent names/identities and counts. These tests must not open returned logical paths.
- [ ] Implement query selection without allocating a volume-wide list:

```csharp
return index.FindByName(GitEntryName)
    .Where(entry => !entry.IsDeleted && (includeGitFiles || entry.IsDirectory))
    .ToArray();
```

- [ ] In Windows composition, normalize/deduplicate letter-rooted volumes and acquire each identity with `IndexedDrive.FromWindowsVolume`. Reject UNC tokens from this path; the existing filesystem scanner handles such roots. A failed volume-identity acquisition must be reported per drive rather than preventing valid volumes from opening. Retain these pre-index failures in the owner's ScanErrors and merge them with FileIndex.Drives production failures; never invent an IndexedDrive serial just to manufacture a library status.
- [ ] **Lazy connection safety:** the discovery composition returns false for empty roots or zero selected drives BEFORE any broker connection factory is invoked. Task 7 wires this ordering into the application and rewrites the existing empty-search test as `TryFindAllRepositoriesUsingMftAsync_EmptySearchPaths_DoesNotInvokeConnectionFactory`; it counts invocations and asserts zero, false, and no paths. Keep this as a successor, not an extra definition. Opening must remain lazy even when already elevated: eager connection can prompt or hang Windows CI.
- [ ] Connect lazily only after nonempty drive selection, once, retain the real client, and give producer and watch source the same connection delegate. Use `ProducerPolicy.Mft`, DirectoryIndex, and `.git` KeepFileNames. Attach JournalBrokerClient.BrokerDied before scanning and latch IsFaulted so a parked prepared owner cannot miss a prior fault; detach on disposal and latch IsDisposed. Unwind the client on any exceptional exit; on ordinary disposal await index disposal before client disposal in a finally block. Dispose once on races with cancellation.
- [ ] **P10 settled cache policy:** use `NoCache = true` for this port's production index. git-wizard has no persistent MFT inventory today, and a port preserves observable behaviour. The block destination still comes from FileIndex; do not add a consumer file format. Use a protected dedicated index directory under `GitWizardApi.GetLocalFilesPath()` for directory metadata, never the other consumer's full-index cache. Persistent caching would need its own namespace, fresh-discovery and rescan semantics, cleanup, configuration plus CLI and UI call sites, a warm-open freshness gate, and tests. That is a feature, not a port, and is out of scope for plan 4. This mode is documented explicitly in Task 10.
- [ ] This task adds fully tested components; Task 7 is their application composition point. It changes no working discovery/watch route yet. Do not delete the Phase 1 code prematurely.

**Verification:**

```bash
dotnet build GitWizardTests/GitWizardTests.csproj -c Release
dotnet test GitWizardTests/GitWizardTests.csproj -c Release
```

Expected: **1144 definitions, 1133 passed, 11 skips, 0 failures**. Exactly 12 new portable tests; one connection for the multi-drive fixture; failed-drive status names that drive; no new Windows attribute.

### Task 5: Map immutable FileChange locations into repository change inputs

**Files:** Create `GitWizard/Watch/IndexChangeMapping.cs`, `GitWizardTests/Watch/IndexChangeMappingTests.cs`. Read but do not modify `GitWizard/RepositoryChangeFilter.cs` and `GitWizard/Watch/WatchPath.cs`.

**Interfaces:**

```csharp
// Actual MFTLib.Index event value
public sealed record FileChange(FileChangeKind Kind, FileEntry Entry,
    string Path, string? PreviousPath = null);
// FileChangeKind: Created, Deleted, Renamed, Modified.
// FileIndex mutation seam, used by this task's tests only
public IReadOnlyList<FileChange> ApplyJournalEntries(char driveLetter,
    IReadOnlyList<UsnJournalEntry> entries, ulong journalId, long nextUsn);
// Proposed consumer helper
internal static IReadOnlyList<VolumeChangeEntry> Map(FileChange change);
// Existing consumer domain
public readonly record struct VolumeChangeEntry(string FullPath, VolumeEntryKind Kind);
public FilterResult Classify(IReadOnlyList<VolumeChangeEntry> entries);
```

**Steps:**

- [ ] **Red first:** add five tests: `CreatedMapsToCreated`, `DeletedUsesCapturedPath`, `ModifiedUsesCapturedPath`, `RenameInvalidatesBothLocations`, `SameBatchRenameDoesNotRewriteEarlierEvent`. The last two must use a real FileIndex mutation and demonstrate `change.Entry.Path` can differ from `change.Path`. This test file opens its own enumeration index directly through public FileIndex.OpenAsync/FileIndexOptions over a generated temp tree; it does not require Task 4's owner or fixture extensions.
- [ ] Map Created/Deleted/Modified one-for-one. Map Renamed to old Deleted plus new Created, preserving both strings; ordinary files then refresh both containing repositories, while a moved `.git` marker removes/adds the appropriate repository. A rename without PreviousPath yields the new Created entry only, without fabricating an old location. Do not parse UsnReason in this mapper.
- [ ] Feed results through the existing `RepositoryChangeFilter` in the rename tests. Assert changed roots for ordinary file moves and deleted/created roots for marker moves as assertions within those five tests. Do not add a second record lookup or mutate the FileEntry.

**Verification:**

```bash
dotnet test GitWizardTests/GitWizardTests.csproj -c Release
```

Expected: 0 failed, **five additional portable definitions**, same 11 skips. When integrated after Task 4: 1149 definitions and 1138 passed. This task can be developed in parallel with Task 4, but their combined count is checked after integration.

### Task 6: Implement the index-backed Windows source

**Files:** Create `GitWizard/Watch/IndexVolumeChangeSource.cs`, `GitWizard/Watch/IVolumeChangeDiagnostics.cs`, `GitWizardTests/Watch/IndexVolumeChangeSourceTests.cs`, `GitWizardTests/TestSupport/ScriptedIndexWatchSource.cs`. Extend `PackedIndexFixture.cs`. Leave the factory on the existing source until Task 7.

**Interfaces:**

```csharp
// Actual FileIndex
public event Action<FileChange>? Changed;
public event Action<WatchFault>? WatchFaulted;
public Task StartWatchingAsync(CancellationToken cancellationToken);
public Task StopWatchingAsync(CancellationToken cancellationToken);
public IReadOnlyList<DriveStatus> Drives { get; }
// Actual public watch seam
IAsyncEnumerable<WatchStreamItem> StartWatching(
    IReadOnlyList<IndexWatchTarget> targets, CancellationToken cancellationToken);
Task ArmDriveAsync(IndexWatchTarget target, CancellationToken cancellationToken);
Task DisarmDriveAsync(char driveLetter, CancellationToken cancellationToken);
public sealed record JournalBatch(char DriveLetter, IReadOnlyList<UsnJournalEntry> Entries,
    ulong JournalIdentifier, long NextUsn) : WatchStreamItem;
public sealed record DriveWatchFailure(char DriveLetter, Exception Exception) : WatchStreamItem;
public sealed record WatchFault(WatchFaultKind Kind, char? DriveLetter, Exception Exception);
```

Proposed consumer source contracts:

```csharp
public interface IVolumeChangeDiagnostics
{
    event Action<string, string>? DriveFailed;
}
public IndexVolumeChangeSource(
    Func<IReadOnlyList<string>, CancellationToken, Task<MftIndexSession>> openSession,
    MftIndexSession? preparedIndex = null);
// Implements existing IVolumeChangeSource verbatim:
public event Action<string>? SourceDied;
public Task<VolumeArmResult> ArmAndCatchUpAsync(IReadOnlyCollection<string> volumes, CancellationToken ct);
public IAsyncEnumerable<VolumeChangeBatch> WatchAsync(CancellationToken ct);
public ValueTask DisposeAsync();
```

`IVolumeChangeDiagnostics` is an optional new domain capability implemented by the index source. It does not add members or no-op implementations to fanotify or fake sources.

**Steps:**

- [ ] **Red first:** add eight tests: `ReusesMatchingPreparedIndex`, `MismatchDisposesAndOpensRequestedDrives`, `DisposeBeforeArmClosesPreparedOwner`, `WatchDeliversMutationEvents`, `OrdinaryFileModificationHydratesOmittedRow`, `OrdinaryFileDeletionHydratesOmittedRow`, `CancellationStopsPumpAndCompletesReader`, `TerminalFaultClosesChannelOnce`. They fail because the new source does not exist.
- [ ] Structural ownership tests use real enumeration indexes. Pump tests use a generated temp tree to supply names/parent relationships, then an explicit **MFT-kind** `BlockFile` built via the public `MftBlockProducer` seam and `BlockWriter`, with a nonzero fixed journal cursor. The producer writes the exact request BlockPath and stamps the same cursor it returns. Do not merely attach a WatchSource to an Enumeration-policy index: that is a no-op watch.
- [ ] Implement a scripted `IIndexWatchSource` using a channel and readiness/completion signals. Record target cursors and cancellation. Use `JournalBatch`/`DriveWatchFailure`, actual `FileIndex.StartWatchingAsync`, and real mutation; do not fake FileIndex or synthesize FileChange as a substitute for testing the pump.
- [ ] Arm/setup adopts a prepared owner only when it is neither disposed nor faulted and normalized requested drive sets match; otherwise dispose it and open a fresh owner once. Merge owner.ScanErrors and initial failed/offline drive statuses into `VolumeArmResult.Errors`. Enumeration ownership fixtures intentionally have no watchable drives and must be tested without claiming a live pump started. A wholly failed open must produce a clean stopped/error outcome, not an apparently live empty source.
- [ ] Subscribe to Changed and WatchFaulted before starting the watch. Map each FileChange immediately to string-only VolumeChangeBatch data; group by `WatchPath.Parse(change.Path).Volume`, not host Path.GetPathRoot. Create the output channel before subscription so the first events cannot be lost. Begin FileIndex watching when `WatchAsync` starts, preserving the consumer service's subscription ordering. Under P12 and MFTLib#139, `Started` and Live mean "the pump started", never "catch-up has drained"; scripted readiness signals do not establish a production catch-up fence.
- [ ] A drive-specific Source/Apply WatchFault raises DriveFailed and leaves other drives running. A merged-stream fault (null DriveLetter), or loss of every usable drive, raises SourceDied once and completes the output channel. Surface subscriber errors without pretending an individual file event was a broker death. Preserve the failure message from DriveStatus/WatchFault.
- [ ] In iterator cleanup, detach handlers and await StopWatchingAsync. Handle the already-reported stop fault explicitly, preserve unexpected faults, and release the owner in the source's idempotent DisposeAsync. Source disposal also ends a blocked channel reader. Never await shutdown while holding a lock needed by an event handler.
- [ ] No `_pathIndexByDrive`, `ResolvePath`, `MapEntries`, or explicit CatchUpEntries replay in this source. The library resumes from the armed block cursor, so replaying the scan's separate catch-up data would double-apply it.

**Verification:**

```bash
dotnet test GitWizardTests/GitWizardTests.csproj -c Release
```

Expected after Tasks 4-5 integrate: **1157 definitions, 1146 passed, 11 skips, 0 failures**. Scripted watches actually enter StartWatching and deliver events; cancellation awaits completion without sleeps. Existing production behavior remains green until composition switches in Task 7.

### Task 7: Adopt the owner in discovery, reports, UI, and platform factory; delete the old path

**Files:**

- Modify: `GitWizard/GitWizardApi.PreparedDiscovery.cs`, `GitWizard/GitWizardApi.Discovery.cs`, `GitWizard/GitWizardApi.IndexDiscovery.cs`, `GitWizard/MftDiscoveryResult.cs`, `GitWizard/GitWizardReport.DiscoverySession.cs`, `GitWizard/GitWizardReportOptions.cs`, `GitWizard/GitWizardReport.cs`, `GitWizard/Watch/VolumeChangeSourceFactory.cs`.
- Modify: `GitWizardUI/ViewModels/MainViewModel.cs`, `MainViewModel.Refresh.cs`, `MainViewModel.Live.cs`, `MainViewModel.Grouping.cs` in that same directory.
- Delete: `GitWizard/GitWizardApi.PackedBlocks.cs`, `GitWizard/Watch/UsnVolumeChangeSource.cs`, `GitWizardUI/ViewModels/PreparedDiscoverySession.cs`.
- Rewrite: `GitWizardTests/BrokerDiscoveryTests.cs`, `GitWizardTests/UI/MainViewModelPreparedSessionTests.cs`, `GitWizardTests/Watch/UsnVolumeChangeSourcePreparedSessionTests.cs`, `GitWizardTests/ReportDiscoverySessionTests.cs`, `GitWizardTests/GitFileScanRecordMappingTests.cs`.
- Modify: `GitWizardTests/UI/MainViewModelLiveTests.cs` Windows concrete-type assertion; `GitWizardTests/Watch/RepositoryWatchServiceTests.cs` stale catch-up comment at baseline line 338.
- Modify: `GitWizardTests/Watch/VolumeChangeSourceFactoryTests.cs`, `GitWizardTests/Watch/UsnVolumeChangeSourceTests.cs` constructor/type calls; `GitWizardTests/ElevationDecisionTests.cs`, `GitWizardTests/CoverageBoostDiscoveryTests.cs`, `GitWizardTests/ElevationCoverageTests.cs` discovery signatures/comments. Preserve their existing gates until Task 9.
- Delete `GitWizardTests/PackedBlockDiscoveryTests.cs` only after moving its four assertions to `IndexDiscoveryTests.cs`; delete `GitFileScanRecordMappingTests.cs` only after moving its eleven successors to `GitFileIndexMappingTests.cs`. Rename the prepared-source fixture to `Watch/IndexVolumeChangeSourcePreparedIndexTests.cs`, preserving five cases.

**Interfaces:** Final proposed consumer ownership API:

```csharp
// GitWizardApi: replace old public signatures, no compatibility overloads
public static Task<bool> TryFindAllRepositoriesUsingMftAsync(
    GitWizardConfiguration configuration, ICollection<string> paths,
    IUpdateHandler? updateHandler = null, bool noMft = false);
public static Task<MftDiscoveryResult> TryDiscoverRepositoriesUsingMftAsync(
    GitWizardConfiguration configuration, ICollection<string> paths,
    IUpdateHandler? updateHandler = null, bool noMft = false);
// MftDiscoveryResult owns the session until detached
public MftDiscoveryResult(bool success, MftIndexSession? indexSession = null);
public FileIndex? Index { get; }
public MftIndexSession? DetachIndexSession();
// GitWizardReport
[JsonIgnore] public FileIndex? DiscoveryIndex { get; }
public MftIndexSession? DetachDiscoveryIndex();
// GitWizardReportOptions
public bool RetainDiscoveryIndex { get; set; }
// VolumeChangeSourceFactory: replace old prepared overload, keep parameterless entry point
public static IVolumeChangeSource Create();
public static IVolumeChangeSource Create(MftIndexSession? preparedIndex,
    Func<string, bool>? brokerLauncher = null);
// MainViewModel, all under its existing ownership lock
internal MftIndexSession? TakePreparedDiscoveryIndex();
internal Task SetPreparedDiscoveryIndexAsync(MftIndexSession? nextIndex);
```

`FileIndex` properties are borrowed query access; `MftIndexSession` is the transferable resource owner. There is one owner at a time: discovery result -> report -> view model -> IndexVolumeChangeSource -> disposal. This is why simply replacing a Session property with a FileIndex property would leak the client.

**Steps:**

- [ ] **Red first:** rewrite the existing ownership/lifecycle tests against this contract, and add two report tests: `PopulatedIndexDetachesWithoutDisposal` and `PopulatedIndexIsNotSerialized`. Their old equivalents only tested empty state. Assert the real index remains queryable by the new owner after the old container is disposed; verify the client closes once at final disposal.
- [ ] **Lazy connection safety:** empty roots or zero selected drives return false BEFORE invoking any broker connection factory, including for an already-elevated process. Select roots/drives first; connect lazily only for a nonempty selection, as required by Task 4. Rewrite `ElevationDecisionTests.TryFindAllRepositoriesUsingMftAsync_ElevatedWithEmptySearchPaths_ReturnsFalse` as `TryFindAllRepositoriesUsingMftAsync_EmptySearchPaths_DoesNotInvokeConnectionFactory`, passing a counting/failing connection factory through the internal composition helper. Assert zero calls, false, and empty paths. Retain its existing Windows gate and test-definition count; it must never launch a real broker with empty search paths. Cover zero selected drives in an existing portable discovery-policy successor with the same zero-call assertion.
- [ ] Switch Windows discovery to `MftIndexSession.OpenWindowsAsync` regardless of the process's initial elevation. Remove `DiscoverRepositoriesInProcess` and `FindGitRepositoriesUsingMft`. `MftVolume` remains a valid library API; deleting this branch is a chosen simplification, not a missing-type repair. Keep Defender's IElevationProvider use and both elevated entry dispatchers untouched.
- [ ] Remove public record-provider/session-provider parameters outright, and remove the discovery-only elevation argument with the now-deleted in-process decision. Update its call sites in ElevationCoverageTests, ElevationDecisionTests, CoverageBoostDiscoveryTests, and BrokerDiscoveryTests in this task; the separate Defender elevation interface survives unchanged. Retain the public basic TryFind/TryDiscover entry points and put injected composition, including the lazy connection factory seam, on an internal helper used by their Windows branch and portable tests; do not keep obsolete overloads. The internal helper takes actual index ownership and independent candidate filtering dependencies, not fake OS flags that permit enumeration paths into real filesystem checks.
- [ ] Query `.git` entries once per index, preserve the full-drive directory-only versus scoped file-inclusive rule, and derive repository parents with `entry.Parent`. Filter scope, ignored roots, and hidden segments using Windows syntax-aware logic before disk validation. Separate `SelectGitEntries`/pure candidate policy from the final Windows filesystem collector. Update existing host-path resolver tests without passing index paths to their filesystem-dependent ExpandIgnoredPaths seam.
- [ ] Real Windows candidates are still checked for existence and later opened by LibGit2Sharp. A failed/no-result discovery disposes the owner and returns false so the existing recursive fallback runs. Preserve successful volumes and report partial failures instead of silently losing their errors. Linux and no-MFT exit before any index opening.
- [ ] Atomically replace report and UI ownership fields, RetainDiscoverySession call sites, detach/set/take methods, cache-clear/delete-local-files actions, and shutdown. Keep all transient ownership/query properties JsonIgnore. No FileEntry or broker state enters report.json. Disposing a replaced report/UI holder must not dispose an already-detached owner.
- [ ] Switch the factory to IndexVolumeChangeSource on Windows and leave the fanotify branch intact. Explicitly change the Windows branch condition from `IsWindows() && OperatingSystem.IsWindows()` to `IsWindows()`, so `_isWindowsOverride` can select it on Linux. This is safe because constructing the source performs no native operation or broker connection; the deferred Windows opener must retain a real `OperatingSystem.IsWindows()` guard and platform annotation at the native call site. Without an override, `IsWindows()` still detects the actual platform. Rewrite the `Create_WindowsWithPreparedSession_*` successor to exercise prepared-owner construction through that seam on Linux and remove its one runtime Windows skip; do not lower the count gate. Use `preparedIndex` consistently in this factory and Task 6's constructor. The first Live start consumes prepared state (MFTLib#139: pump started, not catch-up drained); stopping disposes it; a second start or terminal respawn opens a new owner exactly once.
- [ ] Delete the old USN adapter and temporary block traversal. Move all four block-reader test subjects to public query tests: flags/root handling become structural query assertions; invalid parent handling uses the library's documented depth guard and does not demand semantics the library does not promise. The old raw-reader implementation itself is gone. Preserve eleven mapping cases and five prepared-source cases in their renamed files.
- [ ] In `GitWizardTests/UI/MainViewModelLiveTests.cs`, update `LiveVolumeChangeSourceFactory_DefaultsToRealPlatformSourceConstructor` at baseline line 471 to assert `source.GetType().Name == "IndexVolumeChangeSource"` in the Windows branch. Keep the Linux `FanotifyVolumeChangeSource` assertion. Linux cannot execute this Windows assertion, so the controller Windows non-admin gate below is mandatory.
- [ ] Rewrite the comment at `GitWizardTests/Watch/RepositoryWatchServiceTests.cs:338` to describe the unchanged `IVolumeChangeSource` ordered-batch contract and FileIndex-owned replay (MFTLib#139: no startup catch-up fence), removing the retired `UsnVolumeChangeSource.WatchAsync` contract claim. Include test comments in the stale-symbol sweep.
- [ ] Update all dependent test source signatures in this same task, including the real Windows test file that Task 9 later strengthens. Do not leave a green Linux runtime with uncompilable Windows tests. Remove any existing coverlet file exclusion for the deleted source in Task 11; do not transfer it to the new source.

**Verification:**

```bash
dotnet build git-wizard.slnx -c Release
dotnet test GitWizardTests/GitWizardTests.csproj -c Release
rg -n 'ScanRecord|JournalBrokerScanSession|UsnJournalEntry|_pathIndexByDrive|ResolvePackedBlockPath' GitWizard GitWizardUI git-wizard -g '*.cs'
rg -n 'UsnVolumeChangeSource\.WatchAsync' GitWizardTests -g '*.cs'
```

Expected: build/full suite exit 0; **1159 definitions, 1149 passed, 10 skips, 0 failures**. The production search has no matches (comments updated too), and the test-comment sweep has no retired watch-contract matches; broker child entry points and Defender remain. No FileIndex enumeration producer in any production Linux discovery path. Remove the obsolete source exclusion from coverlet.runsettings in Task 11 without excluding IndexVolumeChangeSource.

**Controller Windows non-admin gate (P13):** The controller publishes the exact task result to the Gitea feature branch, sets `verificationReference` to that committed reference, and runs from this worktree:

```bash
bash scripts/verify-windows.sh "${verificationReference:?Set the controller-published task reference}"
```

The supplied script targets `mtsch@chonkers`, checkout `C:/Users/mtsch/git-wizard`, fetching the feature reference from `gitea`. Record the checked-out commit, successful native/managed builds, suite counts, zero failures, and `RESULT: PASS (windows non-admin)` for this exact task result. This gate uses `Category!=RequiresAdmin`; it does NOT run elevated `RequiresAdmin` tests, which remain attended in Task 9. No elevated result may ever be reported from this script. Do not advance on Linux-only evidence.

### Task 8: Surface per-drive watch failures without stopping healthy drives; close Phase 2

**Files:** Modify `GitWizard/Watch/RepositoryWatchService.cs`, `GitWizardUI/Services/LiveWatchController.cs`, `GitWizardUI/ViewModels/MainViewModel.Live.cs`, `git-wizard/Program.Watch.cs`. Add assertions/cases in `GitWizardTests/Watch/RepositoryWatchServiceTests.cs`, `GitWizardTests/UI/LiveWatchControllerTests.cs`, `GitWizardTests/UI/MainViewModelLiveTests.cs`, `GitWizardTests/ProgramWatchLoopTests.cs`. Update `AGENTS.md` architecture paragraphs for final ownership.

**Interfaces:** Consume Task 6's `IVolumeChangeDiagnostics.DriveFailed`. Add consumer `public event Action? ScanErrorsChanged` to RepositoryWatchService and LiveWatchController, preserving their `IReadOnlyDictionary<string, string> ScanErrors` accessors. Library `DriveStatus.WatchFailureMessage` is independent of `State`: a failed watch may still have a Ready, queryable block.

**Steps:**

- [ ] **Red first:** add four named cases, one in each test file: `DriveFailureKeepsHealthyEvents`, `DriveFailureDoesNotRespawnController`, `DriveFailureUpdatesLiveWarningWithoutRepositoryEvent`, `DriveFailurePrintsWhileOtherDriveContinues`. They fail because consumers currently learn errors only at initial arm or on another repository event.
- [ ] Subscribe to optional diagnostics before the source can start watching. Preserve P12/MFTLib#139 semantics: `Started` and Live mean "the pump started", never "catch-up has drained". Publish a new immutable error dictionary on each update and raise ScanErrorsChanged outside locks. Detach on every shutdown path. A source lacking this optional interface keeps its existing behavior; no edits to fanotify or its tests.
- [ ] Forward the event through LiveWatchController, marshal UI warning changes through `_ui.Post`, and print each new/changed drive message in the CLI while the stream is live. Do not reprint unchanged errors per repository event or cancel all volumes for one failure.
- [ ] Drive-specific faults keep healthy volumes live. Source-wide or all-drive loss still follows the existing terminal stop/respawn policy; never label a stopped pump healthy solely because DriveState is Ready. Preserve current cancellation and maximum respawn policy without adding sleeps or test-only clock knobs.
- [ ] Update AGENTS.md's MFT/discovery/watch paragraphs and the inline comments changed by these tasks. Confirm ordinary Refresh still uses cached repository paths and does not cause UAC; Shift+Refresh still disposes prepared state and rediscovers.

**Verification:**

```bash
dotnet build git-wizard.slnx -c Release
dotnet test GitWizardTests/GitWizardTests.csproj -c Release
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --list-tests
```

Expected: **1163 definitions, 1153 passed, 10 named skips, 0 failures**, both builds/tests exit 0. This full suite is Phase 2's green boundary; no unresolved terminal-channel hang or missing mid-watch error presentation may be deferred.

**P8/P13 phase exit:** After the Linux gate above, the controller runs `scripts/verify-windows.sh` with the published phase-exit reference, using the Task 7 invocation and evidence requirements. Require Windows non-admin success for this exact phase result; an earlier Task 7 result does not cover subsequent changes. This excludes `RequiresAdmin` and supplies no elevated evidence.

## Phase 3: narrow Windows evidence, documentation, final gates

### Task 9: Strengthen the four real Windows integration cases

**Files:** Rewrite `GitWizardTests/Watch/UsnVolumeChangeSourceTests.cs` as `GitWizardTests/Watch/IndexVolumeChangeSourceWindowsTests.cs`; modify `GitWizardTests/ElevationCoverageTests.cs`. No new Windows-only test definitions.

**Interfaces:** Consume `MftIndexSession.OpenWindowsAsync`, `IndexVolumeChangeSource`, `IndexedDrive.FromWindowsVolume`, `FileIndex.FindByName`, and `FileIndex.StartWatchingAsync` already specified. Keep `IElevationProvider`, `BrokerLauncher`, `ElevatedEntryPoint.TryHandle`, and app-host launch behavior.

**Steps:**

- [ ] **Red first:** preserve the four original definitions but strengthen them to require real results: a usable MFT drive status, actual discovery of a pre-existing repository, one deliberately generated file change, and replay of a deliberately generated scan-to-watch-gap change. The old catch-up test accepted a timeout with no event; that is not evidence of replay.
- [ ] Use the actual drive containing the fixture from Path.GetPathRoot, never hard-code C. Derive the built UI app-host location from the test output/repository location. Keep the existing real-admin check at the start of each test body and Platform("Win")/RequiresAdmin gates. Launch the app host containing broker dispatch, not `dotnet TestAssembly.dll`.
- [ ] Create fixture repositories before scanning. For the gap case, await discovery/arm, then create the file before calling WatchAsync. Require its event from the armed cursor replay with a cancellation bound. For live create, begin the observable watch and then write the file; replay semantics cover startup races (MFTLib#139: starting the pump does not acknowledge catch-up completion). Remove fixed Task.Delay readiness sleeps and the catch that turns a no-event timeout into success.
- [ ] Controller runs these on Windows with native build and actual elevation. Manually check one non-elevated cold discovery -> Live transition prompts once, and no-MFT discovery prompts zero times. Ordinary cached Refresh must not prompt. Validate `.git` directory plus worktree/submodule marker-file behavior under a scoped path and drive-root exclusion policy.

**Verification (Linux regression):**

```bash
dotnet test GitWizardTests/GitWizardTests.csproj -c Release
```

Expected: **1163 definitions, 1153 passed, 10 skips, 0 failures**. No broadened platform gates.

**Verification (controller on Windows, outside this lane):**

```powershell
MSBuild.exe external/MFTLib/MFTLibNative/MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64
dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
dotnet build external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj -c Release -p:Platform=x64
dotnet build git-wizard.slnx -c Release
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --filter "Category=RequiresAdmin"
```

Expected: native and managed builds exit 0; the four named MFT/broker cases execute and pass in the elevated run, with 0 failures. Record any other RequiresAdmin cases selected rather than assuming this filter selects only those four. The unprivileged Linux fanotify cases remain a separate unchanged capability requirement, not a reason to expand this port's Windows gate.

### Task 10: Document actual scope, cache policy, and ownership

**Files:** Modify `README.md`, `AGENTS.md`, `docs/report-schema.md`, `TEST-REPORT.md`, and the bridge header comment in `Directory.Build.targets`; update `GitWizard/GitWizard.csproj` and relevant XML comments. `CLAUDE.md` remains `@AGENTS.md`. Do not modify submodule documentation.

**Interfaces:** Prose contract only. Explain `FileIndexOptions.NoCache`, actual ownership, unchanged Git report schema, and platform routing. No new runtime API.

**Steps:**

- [ ] **Red first:** inspect the current text for claims about ScanRecord payloads, the consumer USN dictionary, session-owned live updates, and in-process elevated scans. These assertions are now false and constitute the documentation regression. No executable prose-only test is needed.
- [ ] Describe Windows discovery as filtered whole-volume metadata: directories plus `.git` marker files across the selected volume, potentially including other users' names; live hydration may add observed ordinary-file metadata. A process running as the user can read its mapped/backing metadata. Explain OS delete-on-close and that this port selects no-cache mode, so it does not add a persistent inventory cache. The existing `~/.GitWizard/repositories.txt` and report.json still persist and are not disabled by index no-cache mode.
- [ ] Describe FileIndex's persistent-cache capability only as such; P10 selects `NoCache = true` and leaves persistent caching outside plan 4. Do not advertise a nonexistent `--no-cache` flag or say no-cache blocks never touch disk. Do not call a filtered index a complete full-file inventory.
- [ ] Document recursive Linux/no-MFT discovery, unchanged Linux fanotify, the one-UAC discovery-to-Live transfer, drive-specific watch failures, and stop/restart ownership. Cite MFTLib#139 for watch startup and state that Live means "the pump started", never "catch-up has drained". Keep user-facing text free of implementation details unless they explain behavior.
- [ ] Keep report schema 2.1 and Git metadata ownership intact. State that DiscoveryIndex and its owner are never serialized. Keep the assembly bridge and issue #134 retirement condition; do not add a NuGet dependency.
- [ ] Record fresh implementation-phase measurements in TEST-REPORT.md, distinguishing local/Linux results from attended Windows evidence and skipped gates. The existing 2026-08-21 Windows coverage figures are historical, not this port's measurements.

**Verification:**

```bash
git diff --check
rg -n 'ScanRecord|_pathIndexByDrive|in-process' README.md AGENTS.md docs/report-schema.md Directory.Build.targets
dotnet test GitWizardTests/GitWizardTests.csproj -c Release
```

Expected: diff check exits 0; search has no stale claims about the migrated runtime (inspect any unrelated matches); full suite still **1163 definitions, 1153 passed, 10 skips, 0 failures**. Documentation work does not justify altering test expectations.

### Task 11: Run full Linux, quality, scope, and attended gates

**Files:** Modify `GitWizardTests/coverlet.runsettings` only to remove the obsolete `UsnVolumeChangeSource.cs` exclusion; update `TEST-REPORT.md` with verified evidence. Fix runtime files only if an actual gate fails. No changes to exclusions for surviving files, thresholds, or dependency pin.

**Interfaces:** No new API. Use existing project coverage/lint tooling and the final Task 7 ownership surface.

**Steps:**

- [ ] **Red first:** check for residual old source/type references, the stale source coverage exclusion, missing named replacement tests, changed fanotify/recursive code, and a moved submodule pin. Any such finding fails this task before declaring completion.
- [ ] Run the exact full Linux gate, not a selected test subset. Provision dependency builds through the controller if the lane cannot write generated submodule outputs:

```bash
dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
dotnet build external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj -c Release -p:Platform=x64
dotnet build GitWizardTests/GitWizardTests.csproj -c Release
dotnet build git-wizard.slnx -c Release
dotnet test GitWizardTests/GitWizardTests.csproj -c Release
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --list-tests
dotnet format git-wizard.slnx --verify-no-changes
```

Expected: all commands exit 0; **1163 definitions, 1153 passed, 10 justified skips, 0 failed**; 0 analyzer/format findings. The supplied historical executed count is not a target for new runner display formatting.

**Controller Windows non-admin gate (P13):** The controller publishes the exact task result to the Gitea feature branch, sets `verificationReference` to that committed reference, and runs from this worktree:

```bash
bash scripts/verify-windows.sh "${verificationReference:?Set the controller-published task reference}"
```

The supplied script targets `mtsch@chonkers`, checkout `C:/Users/mtsch/git-wizard`, fetching the feature reference from `gitea`. Record the checked-out commit, successful native/managed builds, suite counts, zero failures, and `RESULT: PASS (windows non-admin)` for this exact task result. This gate uses `Category!=RequiresAdmin`; it does NOT run elevated `RequiresAdmin` tests, which remain attended in Task 9. No elevated result may ever be reported from this script. Do not advance on Linux-only evidence.

- [ ] Collect coverage using the same assembly scope as Linux CI, without adding exclusions:

```bash
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --collect:"XPlat Code Coverage" --results-directory TestResults/final
python3 ci/post-coverage-status.py --cobertura "TestResults/final/**/coverage.cobertura.xml" --gate-line 55 --summary --skip-post
```

Expected: 0 test failures, coverage command and gate exit 0, line coverage at least **55%**. Use the coverage report captured at Task 3's green boundary, then compare the final percentage and changed production paths to that Phase 1 baseline; require no regression as well as the 55% minimum; a denominator reduction is not proof that replacement behavior is covered. Record line/branch counts and percentages; do not claim the stale 85.67% Windows measurement is a new Linux result.

- [ ] Controller runs the pinned C#-capable aislop workflow and existing Windows deep inspection. These are not satisfied by a public npm Python-only scan. Exact analysis commands in their provisioned environments:

```bash
node "$RUNNER_TEMP/aislop/dist/cli.js" ci . --json
```

```powershell
jb inspectcode git-wizard.slnx -o="$env:RUNNER_TEMP/jb-report.xml" --settings="git-wizard.slnx.DotSettings" --severity=WARNING --no-updates --properties:Configuration=Release
python ci/parse-jb-report.py "$env:RUNNER_TEMP/jb-report.xml"
```

Expected: aislop **100/100**, at least **10 files** analyzed, 0 findings; JetBrains 0 actionable findings and parser exit 0. Use the workflow's inline PowerShell parser if Python is absent on the runner. Tool availability is reported as pending evidence, never a green gate.

- [ ] Confirm Task 9's four elevated cases and attended UAC checks. Linux covers the entire solution and portable behavior; the Windows non-admin suite covers Windows-only branches and assertions. Attended elevated evidence separately covers real volume identity, broker/native transport, actual MFT repository paths, and deployment.
- [ ] Carry spec section 8's attended memory measurement to the controller/campaign: same 21.3M-file machine, before/after, cold settled client **under 1 GB**, warm **under 300 MB** only in a separately scoped persistent-cache campaign, name query **under 200 ms warm**, and cold duration bounded by the measured MFT scan. Record fixture/volume scale and selected DirectoryIndex profile, which cannot be compared as if it were a Full inventory. These are attended benchmarks, not wall-clock unit assertions. With production NoCache selected under P10, record warm persistent opening as not applicable to plan 4 rather than inventing that result. The final campaign measurement tool/path is controller-provided (Open question 4).
- [ ] Run the final scope audit:

```bash
git diff --check
git diff --stat 9b25adb
git diff 9b25adb -- external/MFTLib GitWizard/Watch/Linux
rg -n 'ScanRecord|JournalBrokerScanSession|UsnJournalEntry|_pathIndexByDrive|ResolvePackedBlockPath' GitWizard GitWizardUI git-wizard -g '*.cs'
rg -n 'UsnVolumeChangeSource\.WatchAsync' GitWizardTests -g '*.cs'
git -C external/MFTLib rev-parse HEAD
git status --short
```

Expected: no whitespace errors, no submodule/fanotify diff, no obsolete production symbols, submodule prefix **4c59efa**, and only controller-reviewable intended changes. Inspect the recursive scanner hunks directly: its runtime body must be unchanged. Do not require an empty worktree before the controller commits, and do not run git clean over uncommitted lane deliverables.
- [ ] Audit every changed/generated file for em-dashes and new hard-coded machine paths. Remove new violations only; do not expand into unrelated repo cleanup. Reconcile every test disposition and the count ledger with discovered tests.

**Verification / Phase 3 exit:** Full Linux suite green with the counts above and controller Windows non-admin suite green at this exact phase-exit reference (P13); analyzer/format/coverage evidence recorded; Windows/aislop/attended evidence either attached or explicitly pending controller execution. The controller must not mark the implementation fully verified or merge while a required external gate is pending. A prose-only lane writing this plan runs none of these runtime gates.

## Test disposition table (P7)

Paths below are relative to `GitWizardTests/`. No existing test definition is dropped merely to obtain a green build. A file deletion is contingent on its named replacement having landed in the same task.

| File | Current subject | Disposition | Replacement or retained coverage |
| --- | --- | --- | --- |
| `GitFileScanRecordMappingTests.cs` | Eleven record-to-root selection/filtering cases | Rewrite against enumeration producer over a temp tree; delete old filename in Task 7 | `GitFileIndexMappingTests.cs`, Tasks 3/7; structure-only markers and independent native-path filter tests. Keep drive-root/scoped, ignored/hidden, outside-root, nested worktree cases. |
| `BrokerDiscoveryTests.cs` | Nine discovery/ownership/policy and wire cases | Rewrite against enumeration temp trees plus portable block broker | Tasks 3/7; remove seven Windows attributes. Options travel over the wire; actual Windows filesystem result checks move to existing elevated case in Task 9. |
| `Watch/UsnVolumeChangeSourcePreparedSessionTests.cs` | Five prepared adoption/mismatch/fault/disposal/catch-up cases | Rewrite against enumeration temp trees for ownership, scripted MFT blocks for watch; rename/delete old filename in Task 7 | `Watch/IndexVolumeChangeSourcePreparedIndexTests.cs`, Tasks 3/7. Session.State assertions go away with that subject; owner disposal/reuse and real index replay remain. |
| `Watch/UsnVolumeChangeSourceTests.cs` | Three real Windows broker/watch cases | Rewrite; rename/delete old filename in Task 9 | `Watch/IndexVolumeChangeSourceWindowsTests.cs`, Tasks 7/9. Keep three Windows gates; require real delivery, not successful timeout. |
| `UI/MainViewModelPreparedSessionTests.cs` | Nine ownership/cache-clear/live-reuse/respawn cases | Rewrite against enumeration temp trees plus scripted MFT watch | Tasks 3/7; keep nine successors and remove copied pipe scaffolding. Test type may retain filename while its assertions refer to prepared indexes. |
| `ElevationCoverageTests.cs` | One elevated real scan | Rewrite in place | Task 9 verifies the actual index-backed MFT path; one existing Windows/admin gate. Direct MftVolume branch itself is gone by Task 7. |
| `ElevatedBrokerEntryDispatchTests.cs` | Child broker argument dispatch | Keep as is | Entry dispatch APIs survive; no adaptation required. |
| `ElevationDecisionTests.cs` | Defender injection and empty-root elevated discovery | Rewrite empty-root case as `TryFindAllRepositoriesUsingMftAsync_EmptySearchPaths_DoesNotInvokeConnectionFactory` | Task 7 pins lazy connection and asserts zero factory calls, false, and empty paths. Defender assertion and the single Windows gate stay unchanged. |
| `CoverageBoostDiscoveryTests.cs` | Discovery/fallback branches and provider decisions | Keep as is, with changed discovery signature/comment if required | Task 7. Keep the Windows-only empty-root runtime guard and all filesystem cases. |
| `Watch/VolumeChangeSourceFactoryTests.cs` | Eight platform/elevation/factory cases | Keep subject; rewrite prepared signature/type assertions | Tasks 3/7. Factory still selects fanotify on Linux; remove one Windows skip after portable construction is exposed. |
| `ReportDiscoverySessionTests.cs` | Five cached/no-MFT/detach/dispose/JSON cases, missed by map | Rewrite in place against actual index ownership | Tasks 3/7; five existing successors plus two populated-owner cases. |
| `Watch/RepositoryWatchServiceTests.cs` | Classification, cancellation, debounce, scan errors | Update retired-source catch-up comment in Task 7; extend | Task 7 sweeps test comments; Task 8 adds DriveFailureKeepsHealthyEvents, with no new wall-clock assertion. |
| `UI/LiveWatchControllerTests.cs` | Stop/start/respawn lifecycle | Keep existing cases; extend | Task 8 adds DriveFailureDoesNotRespawnController. |
| `UI/MainViewModelLiveTests.cs` | Live UI events, platform factory, and warnings | Task 7 changes the Windows concrete-type assertion to IndexVolumeChangeSource; retain Linux FanotifyVolumeChangeSource assertion | Task 7 Windows non-admin gate executes the changed assertion; Task 8 adds DriveFailureUpdatesLiveWarningWithoutRepositoryEvent. |
| `ProgramWatchLoopTests.cs` | CLI long-running stream/error output | Keep existing cases; extend | Task 8 adds DriveFailurePrintsWhileOtherDriveContinues. |
| `PackedBlockDiscoveryTests.cs` (new in Task 2) | Four native-block traversal/selection cases | Delete in Task 7 after migrating coverage | Four structural public-query successors in IndexDiscoveryTests; raw traversal implementation itself is deleted. |
| `MftIndexSessionTests.cs` (new) | Connection, options, partial failure, disposal | Create; keep | Task 4: eight portable cases. |
| `IndexDiscoveryTests.cs` (new) | FileIndex marker lookup | Create; keep | Task 4: four cases; Task 7: four migrated cases, not new count growth. |
| `Watch/IndexChangeMappingTests.cs` (new) | Captured path and rename mapping | Create; keep | Task 5: five portable cases. |
| `Watch/IndexVolumeChangeSourceTests.cs` (new) | Real index pump and source lifecycle | Create; keep | Task 6: eight portable cases. |
| `TestSupport/PackedIndexFixture.cs`, `TestSupport/BlockBrokerFixture.cs`, `TestSupport/DuplexStream.cs`, `TestSupport/ScriptedIndexWatchSource.cs` (new) | Test substrates | Create; keep | Tasks 3/4/6. No test definitions; no production wiring or dependency on unexported library helpers. |

An exhaustive unchanged-source inventory follows in Appendix B so test files missed by the survey cannot silently disappear during implementation.

## What this port does not do

- Delete or replace Linux/`--no-mft` recursive repository discovery (P4).
- Modify fanotify, its privilege probing, or platform selection (P5).
- Move the MFTLib pin or retire the assembly/submodule bridge. NuGet bridge retirement is `git-wizard#134`.
- Rewrite LibGit2Sharp Git status, repository/report caches, JSON schema, remote-based UI rename correlation, or the existing classifier.
- Add a second metadata database, public low-level query API, synthetic-to-host path translator, or MFTLib gap fixes.
- Promise a persisted warm cache, a catch-up-complete signal, or a new no-cache CLI flag without the policy decisions below.

## Appendix A: execution waves

| Wave | Tasks | Dependency and actual parallelism |
| --- | --- | --- |
| 1 | 1 | Alone: independently reviewed mechanical hint-path delta. |
| 2 | 2 | Depends on 1; production signature/block rewrite is one coherent change. |
| 3 | 3 | Depends on 2; rewires all affected tests and ends Phase 1 green. |
| 4 | 4 and 5 | **Genuinely parallel** after 3: index owner/query and pure FileChange mapper have disjoint files. Task 5 opens its own temp-tree enumeration index through the public library API and does not depend on Task 4's fixture changes. |
| 5 | 6 | Depends on both 4 and 5; owns the shared scripted-watch fixture additions. |
| 6 | 7 | Depends on 6; atomic application composition and old-source deletion. |
| 7 | 8 | Depends on 7; shared service/UI/CLI error route and full-suite phase boundary. |
| 8 | 9 | Depends on 8; controller supplies Windows evidence. |
| 9 | 10 | Depends on final behavior and measured evidence from 8/9. |
| 10 | 11 | Depends on every task, including documentation and dispositions. |

Only **one of ten waves** is genuinely parallel at the task level. Do not claim that UI and core ownership changes are independent: they share a transfer contract and must compose together. Controller-provisioned Windows work can overlap unrelated documentation reading, but that does not turn their acceptance gates into independent tasks. Lanes do not commit; the controller merges/reviews each wave and reruns the full suite when integrated.

## Appendix B: all other existing test source files

Every file listed here keeps its existing subject and is **keep as is**. This is a source inventory, including support files with no test definitions; it is not a remeasurement of the controller's suite counts.

| File | Disposition |
| --- | --- |
| `AsyncFileIOTests.cs` | Keep as is; existing subject survives. |
| `AvaloniaClipboardServiceTests.cs` | Keep as is; existing subject survives. |
| `BranchInventoryReportTests.cs` | Keep as is; existing subject survives. |
| `CoverageBoostApiTests.cs` | Keep as is; existing subject survives. |
| `CoverageBoostBranchesTests.cs` | Keep as is; existing subject survives. |
| `CoverageBoostBranchesTestsPart2.cs` | Keep as is; existing subject survives. |
| `CoverageBoostReportTests.cs` | Keep as is; existing subject survives. |
| `CoverageBoostRepositoryTests.cs` | Keep as is; existing subject survives. |
| `CoverageBoostRunConfigurationEdgeCases.cs` | Keep as is; existing subject survives. |
| `CoverageBoostRunConfigurationTests.cs` | Keep as is; existing subject survives. |
| `CoverageSafetyBoostTests.cs` | Keep as is; existing subject survives. |
| `ErrorLogViewModelTests.cs` | Keep as is; existing subject survives. |
| `GitWizardApiAdditionalTests.cs` | Keep as is; existing subject survives. |
| `GitWizardApiDiscoveryTests.cs` | Keep as is; existing subject survives. |
| `GitWizardApiTests.cs` | Keep as is; existing subject survives. |
| `GitWizardConfigurationErrorPathTests.cs` | Keep as is; existing subject survives. |
| `GitWizardConfigurationTests.cs` | Keep as is; existing subject survives. |
| `GitWizardLogTests.cs` | Keep as is; existing subject survives. |
| `GitWizardReportAdditionalTests.cs` | Keep as is; existing subject survives. |
| `GitWizardReportDiscoveryProgressTests.cs` | Keep as is; existing subject survives. |
| `GitWizardReportErrorPathTests.cs` | Keep as is; existing subject survives. |
| `GitWizardReportMergeTests.cs` | Keep as is; existing subject survives. |
| `GitWizardReportTests.cs` | Keep as is; existing subject survives. |
| `GitWizardRepositoryBranchesAdditionalTests.cs` | Keep as is; existing subject survives. |
| `GitWizardRepositoryBranchesTests.cs` | Keep as is; existing subject survives. |
| `GitWizardRepositoryMoreTests.cs` | Keep as is; existing subject survives. |
| `GitWizardRepositoryRemoteDivergenceTests.cs` | Keep as is; existing subject survives. |
| `GitWizardRepositoryTests.cs` | Keep as is; existing subject survives. |
| `GitWizardSubmoduleCallbackTests.cs` | Keep as is; existing subject survives. |
| `GitWizardSummaryTests.cs` | Keep as is; existing subject survives. |
| `GlobalTestSetup.cs` | Keep as is; existing subject survives. |
| `InventoryWalkTests.cs` | Keep as is; existing subject survives. |
| `MainViewModelCommandAdditionalTests.cs` | Keep as is; existing subject survives. |
| `MainViewModelCommandTests.cs` | Keep as is; existing subject survives. |
| `MainViewModelGroupingAdditionalTests.cs` | Keep as is; existing subject survives. |
| `MainViewModelGroupingTests.cs` | Keep as is; existing subject survives. |
| `MainViewModelRefreshTests.cs` | Keep as is; existing subject survives. |
| `MainViewModelTests.cs` | Keep as is; existing subject survives. |
| `MainWindowInteractionTests.cs` | Keep as is; existing subject survives. |
| `ProgramHelperTests.cs` | Keep as is; existing subject survives. |
| `RefreshConcurrencyGateTests.cs` | Keep as is; existing subject survives. |
| `RefreshThroughputTests.cs` | Keep as is; existing subject survives. |
| `RelayCommandAsyncTests.cs` | Keep as is; existing subject survives. |
| `RelayCommandTests.cs` | Keep as is; existing subject survives. |
| `RepositoryChangeFilterTests.PathShape.cs` | Keep as is; existing subject survives. |
| `RepositoryChangeFilterTests.cs` | Keep as is; existing subject survives. |
| `RepositoryNodeViewModelAdditionalTests.cs` | Keep as is; existing subject survives. |
| `RepositoryNodeViewModelStateTests.cs` | Keep as is; existing subject survives. |
| `RepositoryNodeViewModelTests.cs` | Keep as is; existing subject survives. |
| `RepositorySweepReportTests.cs` | Keep as is; existing subject survives. |
| `RunConfigurationAdapterTests.cs` | Keep as is; existing subject survives. |
| `SettingsViewModelTests.cs` | Keep as is; existing subject survives. |
| `SmallFilesCoverageTests.cs` | Keep as is; existing subject survives. |
| `StringToThicknessConverterTests.cs` | Keep as is; existing subject survives. |
| `StubServiceTests.cs` | Keep as is; existing subject survives. |
| `StubServices.cs` | Keep as is; existing subject survives. |
| `TagInventoryReportTests.cs` | Keep as is; existing subject survives. |
| `TagsAndBranchesCliTests.cs` | Keep as is; existing subject survives. |
| `TempRepoFixture.cs` | Keep as is; existing subject survives. |
| `TestUpdateHandlers.cs` | Keep as is; existing subject survives. |
| `TestUtilities.cs` | Keep as is; existing subject survives. |
| `UI/MainWindowLiveTests.cs` | Keep as is; existing subject survives. |
| `UpdateHandlerTests.cs` | Keep as is; existing subject survives. |
| `Watch/FakeVolumeChangeSourceTests.cs` | Keep as is; existing subject survives. |
| `Watch/Linux/FanotifyInteropTests.cs` | Keep as is; existing subject survives. |
| `Watch/Linux/FanotifyVolumeChangeSourceTests.cs` | Keep as is; existing subject survives. |
| `Watch/ProgramWatchTests.cs` | Keep as is; existing subject survives. |
| `Watch/RepositoryWatchServiceTests.PathShape.cs` | Keep as is; existing subject survives. |

## Open questions

1. **Tracking issues:** RESOLVED for git-wizard. The plan-3 seat wrapped without filing either issue, so the controller filed the git-wizard tracker itself as [git-wizard#176](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/176) (`queue:wait`; git-wizard has no `needs-design` label). The MFTLib Gap 7 issue and the cached-drive-enumeration issue (Open question 5) remain UNFILED: both belong to MFTLib, both are non-blocking for this port under P4 and P10, and the evidence for filing them is written out in this plan and in the SDD ledger. A lane must still not file either one.
4. **Attended campaign measurement:** the spec assigns final measurement/documentation to campaign plan 5 and names a measurement script generically. Controller must identify the supported harness and baseline artifact for the 21.3M-file machine. **Recommendation:** require actual cold/query evidence for this consumer before campaign sign-off; run persistent warm measurements only if Question 3 enables that mode. Do not invent a local script path or Windows measurement result.

5. **MFTLib exposes no cached-drive enumeration, and that is arguably a library gap rather than a consumer chore.** Verified read-only at pin `4c59efa`: `BlockCacheCatalog` does not exist anywhere in the tree, and `MFTLib/Index/CacheDirectory.cs:13` is a static class whose entire public surface is `ResolveDefaultPath()` (line 15), `BlockFileName(char driveLetter, uint volumeSerial)` (line 27) and `EnsureCreated(string path)` (line 39). There is no API returning the set of drives that currently have a cached block file, so a consumer that wants one must glob `*.mlix` in the cache directory and parse the `{Drive}-{Serial:X8}.mlix` name format (line 29) back into a drive letter and volume serial - reimplementing, per consumer, the inverse of a function the library already owns.

   **This does NOT block plan 4.** Ruling P10 settles this port on `NoCache = true`, so git-wizard persists no block files and never needs to enumerate them. The gap would bite only if persistent caching were adopted later (the feature P10 defers), and it bites file-wizard now, which is where the evidence above was first surfaced. Recorded here so it is filed once against MFTLib with both consumers' evidence rather than solved twice in consumer code. Another session owns filing it; do not file it or work around it from a lane.

The attended campaign question above is retained unchanged for campaign plan 5. Its reference to Question 3 is historical: P10 now settles plan 4 on NoCache, so persistent warm opening is not applicable here.

## PR body notes

The eventual PR body must describe the bridge divergence from file-wizard and the silent-HintPath failure mode, including git-wizard's existing comment naming the file-wizard precedent. Explain that `9b25adb` made the old output paths stale, main was self-consistent, and Task 1 completes the pin move with one TFM property and actionable managed-reference guards. Record this trap as well as the fix, and retain the measured 47 -> 15 diagnostic delta.
