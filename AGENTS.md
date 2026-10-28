# GitWizard

Cross-tool instructions for the core library, CLI, Avalonia desktop, headless
screenshot tool and NUnit tests. Open the linked reference before changing its area.

## Projects and boundaries

- `GitWizard/`: cross-platform core library, discovery coordinator and watch pipeline.
- `git-wizard/`: CLI. Default reports are human-readable; only `--json` reserves stdout
  for strict JSON. Progress and debug diagnostics go to stderr.
- `GitWizardUI/`: Avalonia desktop; view models in `ViewModels/` use
  `IUiDispatcher`, `IUserDialogs` and `IFolderPicker`.
- `GitWizardUI.Screenshot/`: headless Avalonia capture, exercised by CI.
- `GitWizardTests/`: NUnit tests. All five projects target `net10.0`.

## Build

Initialize with `.\init.ps1` (cmd: `.\init.bat`) or `./init.sh`; add `-Build` /
`--build` for builds. Keep cmd's `.\` prefix. Init checks, never installs,
prerequisites; see [building and testing](docs/testing.md).

MFTLib is pinned at `external/MFTLib`. Initialize it with
`git submodule update --init`. Use `bash ci/gates.sh native` for the shared Release
native and managed recipe, then `dotnet restore git-wizard.slnx` and
`dotnet build git-wizard.slnx -c Release --no-restore`.
On Windows, native C++ requires VS MSBuild, not `dotnet build`.

`Directory.Build.targets` owns the HintPath bridge, native copying and retire-to-NuGet
instructions. Do not pull `.vcxproj` into dotnet through project references.
Build both managed dependency assemblies first; only tests reference TestExtensions.
The relative submodule URL is `../MFTLib.git`; publish pinned commits to both remotes.

For dependency changes, edit the submodule, rebuild, and commit its new pin only
after publishing the dependency commit. Read MFTLib's own
[instructions](external/MFTLib/AGENTS.md),
[architecture](external/MFTLib/docs/architecture.md),
[broker lifetime](external/MFTLib/docs/broker-lifetime.md),
[watch lifetime](external/MFTLib/docs/watch-lifetime.md),
[block ownership](external/MFTLib/docs/block-ownership.md) and
[test isolation](external/MFTLib/docs/test-isolation.md) rather than maintaining
a second description of library contracts here.

## Testing

Build first, then run NUnit with `--no-build` and the same configuration. Keep
results outside disposable checkouts, unique for every full-suite/stress run:

```powershell
$results = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ("GitWizard/test-results/" + [Guid]::NewGuid().ToString('N'))
dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release --nologo --results-directory "$results"
```

Preserve failed TRX paths; never overwrite a run or assume uploads succeeded.
Tests need a writable profile; the controller runs outside read-only sandboxes.

[Testing reference](docs/testing.md) and [test organization](docs/test-organization.md): follow their test layout and consolidation evidence rules. Select explicit `RespawnLimit_Stress` by qualified name;
a pass does not prove historical reproduction. Preserve AsyncCondition's
first-exception failure and stack/expression/poll/elapsed diagnostics.

Tests supply owned cache directories and synthetic journal windows.
`GitWizardTests` activates `JournalIsolation.ForbidLiveJournalReads()` in its module
initializer; `GlobalTestSetup` redirects `GITWIZARD_HOME` independently. Follow
MFTLib's linked test-isolation contract for the library behavior.

`AgentInstructionsTests` guards this file below 15000 characters, every
blank-line-delimited block below 3000, linked docs, documented CLI flags and
the shared version. Run it after instruction changes. Preserve still-true rules;
move narrative verbatim into linked docs, correcting false claims in place.
Do not cite source line numbers in prose.

### Shared test support (`GitWizardTests/TestSupport/`)

Central test scaffolding lives in `GitWizardTests/TestSupport/` (`TempDirectory`, `LocalFilesSandbox`,
`RecordingUpdateHandler`, `QueuedUiDispatcher`, `ThreadedUiDispatcher`, `TestViewModels`,
`ScriptedVolumeChangeSource`, `FakeFanotifyNative`, `RootStatuses`, `TestTimeouts`, `AsyncCondition`);
detailed doubles and fixtures are inventoried in [testing reference](docs/testing.md).

Run the [test scaffolding regression checks](docs/testing.md#scaffolding-regression-checks).

## Cleaning the working tree

`git clean -ffxd` must be safe before new work: nothing unrecoverable belongs in
the checkout. Files are tracked and pushed or reproducible. Machine-only valuable
files belong outside it. No exclude list can protect files from `-x`. Do not clean
away uncommitted work. See [checkout maintenance](docs/checkout-maintenance.md)
for the restore inventory and init verification.

```powershell
git clean -ffxd && .\init.ps1
git clean -ffxd && .\init.ps1 -Build
```

```bash
git clean -ffxd && ./init.sh
git clean -ffxd && ./init.sh --build
```

Regenerate agent files after clean/upgrades: `aislop hook install claude --project`.
Init also restores these and optional provisioner-owned settings. Keep it idempotent.
Verify post-clean locally for init/submodule/build-input changes; CI `init-verify`
runs only after non-pin-only main pushes, not PRs.

## Local CI preflight

Run `bash tools/ci-preflight.sh --all` before pushing, or
`bash tools/ci-preflight.sh --changed-from main`. Scopes: `native`, `restore`,
`format`, `build`, `inspect`, `test`, `coverage`, `contracts`, `aislop`.
Every selected gate finishes even after failure.

Preserve preflight protocol 1: clean, unchanged-HEAD runs mint
`pr-crew/preflight-stamp.json`; targeted runs invalidate it. Windows runs InspectCode;
Linux enforces 55% coverage floor. Share recipes in `ci/gates.sh`, `ci/windows-gates.ps1`,
`ci/aislop-gate.sh`. Prepare pinned scanner: `bash ci/aislop-gate.sh prepare`.
Keep tools outside the checkout (`AISLOP_TOOL_DIRECTORY` overrides state cache).
Contract tests redirect stamp/state/results to owned temporary paths.
Workflow detail and verification live in [CI and release reference](docs/ci-and-release.md).

## Lint gate

The bar is **0 findings**, not suppressions. Preserve `Directory.Build.props`
analyzers/code style/warnings-as-errors and `.editorconfig`: UTF-8 no BOM, CRLF,
four spaces, PascalCase members/types/const/static-readonly, `_camelCase` private
and internal fields including mutable static, camelCase locals/parameters. Full
words; no `m_`/`k_`/`s_`. Keep only RCS1001/RCS1003/RCS1161/RCS1168/RCS1238/RCS1249 enabled by default,
plus AsyncFixer and CA2000/CA1849/CA2016. InspectCode enforces UseCollectionExpression and
EnforceIfStatementBraces; tests gate NUnit2045/NUnit2056.

Run `dotnet format git-wizard.slnx --verify-no-changes`; use
`dotnet format git-wizard.slnx` to apply fixes. Naming is a separate rename,
not a whitespace fix. `scripts/format-on-save.py` serves Claude and AGY hooks:
keep skipped/applied success distinct from FAILED (stderr, exit 2).

Deep inspection uses exactly:

```text
jb inspectcode git-wizard.slnx -o=jb-report.json --settings=git-wizard.slnx.DotSettings --severity=WARNING --no-updates --properties:Configuration=Release
python ci/parse-jb-report.py jb-report.json
```

Inspect rejects any finding; match settings/severity and global ReSharper naming.
Keep manually run tools and their contract tests, including parse-jb-report and
verify-windows; lack of a production caller is not grounds for deletion.

Use installed `aislop scan .` before completion and `aislop scan --staged` before
commit. Preserve `failBelow: 100` and C# file-count validation. Fix findings, never
mute rules/config; split oversized files, extract methods, track TODOs as issues,
retain rationale. Pin `.aislop/fork-commit` deliberately, never a moving branch.

Keep run evidence outside the repository. Coverage verdicts belong in
`pr-crew/coverage` and the CI summary; attended evidence belongs on its issue.

`TEST-REPORT.md` is generated on main by CI; pull requests never touch it; details in [CI and release reference](docs/ci-and-release.md).

## Architecture rules

[Architecture](docs/architecture.md) and [watch/recovery](docs/watch-and-recovery.md)
hold the full consumer contracts. Preserve the following boundaries.

Single ownership: discovery result -> report -> view model -> watch source ->
disposal. Detach clears, replacement disposes; queries borrow. Never serialize
the index/owner; schema stays 2.1. Dispose index before broker session. Replace
unusable or drive-mismatched prepared owners.

`MftIndexCachePolicy` owns the directory and `GWMI` version-3 tag. Every index
stores every name. Scope/ignore/hidden/temp settings filter MFT candidates;
walks also apply directory pruning. No ordinal `.git` post-filter; retain
case-insensitive matching and Windows-boundary filesystem validation.

MFTLib selects the producer from `RootKey` and `ProducerSelection`; `--no-mft`
selects `WalkOnly`. Automatic walks on eligibility failures, never mid-scan errors.
Reuse `CandidateAdmissionPolicy`;
ignored roots reject descendants. Temp paths require opt-in or an explicit search
root; hidden paths default included. Temp fixtures opt in explicitly.

Only complete discovery publishes globally; complete-empty clears. Unusable
preserves list/report, without automatic crawl/second elevation. Partial retains
failed/unobserved scope, never treats zero as globally empty. Preserve report
exit 3 (explicit paths unaffected). Ordinary Refresh uses cached membership;
Rebuild/Shift+Refresh and non-explicit sweeps scan without clearing caches first.

Never unlink active index locks. Use `MftIndexCacheMaintenance`/library clearing.
Clear Cache deletes list/report only. Delete All Local Files refuses while
`mft-index` exists, even empty. Status is point-in-time, not deletion permission
or proof another holder still exists.

Dispatch `ElevatedEntryPoint.RunBrokerIfRequested`/Defender child modes first.
Desktop admission stays in `GitWizardUI.Program.Run`/`DesktopInstance`, before
Avalonia, not MFTLib/CLI/App/screenshot. Hold the per-user mutex through lifetime,
release on its thread; duplicates exit 1. Tests use distinct threads/unique names.

Defender excludes process names only (dotnet/git/git-lfs/git-wizard/GitWizardUI),
never cache/temp/volumes. Preserve the protection warning and successful setup
diagnostics in alert/Errors; exclusions affect matching names at any location.

Build collections off-screen, swap once; dispatch visual-tree hooks. Coalesce
status posts; one 250 ms TimeProvider window per command burst, not per-message
posts. Buttons call view-model methods; observe async faults via `RunAndLogFaults`.
Use `StringToThicknessConverter` for string padding.

Scroll: materialize once with `ScrollIntoView`, then bound corrections from row Y,
not estimated extent. Verify headed; headless misses it. During-refresh clear/stream
jump is accepted, separate from final snap-back.

Keep `Repository.IsValid` inside Refresh's try. `ResolveTrackedBranch` decides
upstream; catch only `InvalidSpecificationException`. Config reads name diagnostics.
Preserve git conditional-include fallback, common-repository dedupe and per-refresh
branch/value warnings; no `Branch.RemoteName`/`Repository.Config`/suffix substitutes.

## Watch rules

Arm first, reconcile against the exact watched session, then deliver buffered
startup events through the normal ordered path. Preserve retained roots outside
search drives. A null source means complete-empty or unusable, not a respawn cue.
Directory keys remain exact owner identities. Read final paths from opened indexes
and volume identity from watch/checkpoint observations; never infer it from an alias.
Sibling views must all catch up before their volume becomes ready; route each
change through one owning view. See [directory-root integration](docs/directory-root-integration.md).
Keep optional diagnostics and scan-progress subscriptions attached before arming
and detached on shutdown. Deliver warning changes even without repository events.

Recoverable faults retain drives; terminal failures exclude only affected drives.
Use structured recovery/checkpoint data, not text/retry guesses. Version-check
publication; reconcile markers before CaughtUp. No recovery polling. SourceDied
fires once when no usable/recovering drive remains. Detach/stop before disposal.

Started/green is pump startup, not caught-up. Ready fires once after startup batches
and settled readiness with a caught-up volume; terminal/cancel wins races. Broker
existence is not readiness. Keep CLI ready grammar/verbose timestamps and separate
desktop readiness/history: reserved line, tooltip/cache detail, 64 entries/30 seconds,
cleared on stop/new run. Cache refresh remains independent.

Keep live membership, flush-time moved-in probes and Deleted before probed Created
for rename correlation. Filter stays I/O-free; WatchPath preserves syntax. Linux
routes under `/`, selects roots by event filesystem; no cross-batch descriptors
or stale mount prefixes.

## Debugging, attended testing and screenshots

Diagnose hangs/crashes with dotnet-stack/dotnet-dump, not guesses; launch GUI
diagnostics in background. [Manual tests](docs/manual-test-plan.md) require
`scripts/cli-live-smoke.ps1` before attended desktop retests.

Use `scripts/update-screenshots.ps1 [-OutputPath <file>]` for isolated synthetic
capture, never real repositories. Preserve CI capture/upload verification.

## Maintained developer tools

[Maintained developer tools](docs/maintained-developer-tools.md) inventories every
script under `scripts/`.
Only redundant code is deleted. Add CI coverage for a hand-run tool instead of
deleting it, and add a row when adding a tool. Keep the `UpdateHandler`
ordering checks.

## Release and CLI reference

[CLI options](docs/cli-options.md) is the complete flag list, checked against
parser and help by tests. Keep the list current when changing options.

[CI and release](docs/ci-and-release.md): change the single `<Version>` in
`Directory.Build.props`, update screenshots, commit/push intended changes, then
tag `v<Version>`. CLI help/version and desktop metadata inherit that property;
release rejects a mismatched tag. Preserve Gitea/GitHub asset mirroring, secret
validation, TLS verification and failure reporting. Repair partial releases
before retrying. Never treat a tolerated coverage-artifact upload as gate success.

Windows preview uses `PREVIEW_REGISTRY_TOKEN`. Enrollment is an operator step.
Keep no-display artifact fallback and DISPLAY for XWayland; run-preview scripts
launch artifacts. Do not edit live deployment configuration from this checkout.

[Prior-art notes](docs/notes/prior-art-multi-repo-dashboards.md) are dated research.
