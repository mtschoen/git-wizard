# Handoff - UAC prompts in `dotnet test` (+ residual inspection notes)

Branch: `retire-maui-rename-ui` (PR #51). All this work lands here per the user's choice.

## DONE - ReSharper/Rider inspection cleanup (items 1-4)

Two commits on this branch:
- `4184264` - naming config, thread-sync, context-menu fix, dead commands, async-void hardening.
- `38854f9` - redundancy / dead-code / XML-doc cleanup + structural closure fixes.

`jb inspectcode` went **199 → 19**. The 19 residual are all by-design:
- **10 `InconsistentNaming`** - CLI artifact (jb is hard-bound to the global Unity/C++ naming config; clears in Rider via the solution-shared `git-wizard.slnx.DotSettings`). See `~/.claude/notes/reference_jb_inspectcode.md`.
- **3 `ConditionIsAlwaysFalse` + 1 `ConditionalAccessQualifier`** - System.Text.Json deserialization-boundary null guards (`RemoteUrls` in `MainViewModel`, `SubmoduleHealth?.` in `RepositoryNodeViewModel`). Kept deliberately; don't enable `RespectNullableAnnotations`.
- **3 `RedundantArgumentDefaultValue`** - test-intent (the all-log-types enumeration in `GitWizardLogTests`; `Refresh(null)` in `…_WithNullUpdateHandler_…`; `MatchesFilter(…, null)` in `…_WhenNullEmail`). The explicit value *is* the test subject.
- **2 `UnusedMethodReturnValue`** - informational (`TryFindGitRepositoriesUsingMft`, `TempRepoFixture.RunGit`).

Tooling persists in `.claude/scripts/` (gitignored): `parse-jb-report.py <report>`, `dump-jb-details.py [ruleSubstring]`.
Run: `~/.dotnet/tools/jb.exe inspectcode git-wizard.slnx -o="$TEMP/gitwizard-jb-report.xml" --severity=WARNING --no-updates`.

⚠️ **Rider confirm still pending** (can't verify via the CLI): reopen the solution in Rider and confirm the 10 naming warnings clear (Rider honors the solution-shared `.DotSettings`; jb does not).
✅ **GUI smoke-test DONE (2026-05-27)** - and it caught two real bugs the committed "fix" had: the context-menu `{Binding $self.PlacementTarget.DataContext}` was **broken** (`PlacementTarget` is always null, Avalonia#16344) and a latent clipboard crash (`dynamic` can't see `IClipboard.SetTextAsync`). Both fixed + re-verified in a `--headed` window (Fork/Explorer/Copy all work, "Open in Fork" hides on group headers). See `docs/handoff-context-menu-clipboard-copy-icon.md`.

## UAC prompts during `dotnet test` - DESIGNED (cross-repo, MFTLib 0.3); implementation deferred to fresh sessions

**Full design (authoritative):** `docs/superpowers/specs/2026-05-26-uac-free-tests-and-elevation-seam-design.md` (committed). This section is the orientation summary.

**Symptom:** non-elevated `dotnet test` pops ~4 UAC dialogs (CI unaffected - the Windows runner is elevated). **Mechanism (verified):** `GitWizardReport.GetRepositoryPaths(..., noMft:false)` → `GitWizardApi.TryFindAllRepositoriesUsingMft` → on non-elevated Windows it spawns the elevated `--elevated-mft` child **unconditionally** (independent of search paths; the child does the scan). The 4 triggers all call the instance `GetRepositoryPaths(ICollection)` overload: `RefreshConcurrencyTest`, `GetRepositoryPaths_WithEmptySearchPaths_DoesNotThrow`, `GetRepositoryPaths_EmptyConfiguration_FindsNothing`, `GetRepositoryPaths_WithNonExistentSearchPath_DoesNotThrow`. (`GeneratedReport_DefaultsBranchScopeToActionable` is **not** a trigger - it passes a non-null empty list, so discovery never runs. The earlier "re-verify" doubt was correct.)

**Locked decisions (these supersede the earlier "User's decision"):**
- **Elevation seam lives in MFTLib 0.3, NOT git-wizard (DRY for git-wizard + file-wizard).** MFTLib 0.3 ships a public `IElevationProvider` (IsElevated/CanSelfElevate/TryRunElevated) + `ElevationUtilities.DefaultProvider` backed by the statics. git-wizard injects an optional `IElevationProvider? elevation = null` (default = DefaultProvider) into `TryFindAllRepositoriesUsingMft` + `WindowsDefenderException.AddExclusions`; tests pass a fake (covers the decision incl. already-elevated, no UAC). MFTLib NuGet **v0.2.0 lacks the seam** (only 4 plain statics); 0.3 is unshipped pending this surface. MFTLib task: `~/MFTLib` branch `feat/0.3-elevation-provider`, `docs/handoff-0.3-elevation-provider.md`.
- **UAC fix (4 tests):** `RefreshConcurrencyTest` → **drop the `GetRepositoryPaths` discovery call**, seed paths directly from `TempRepoFixture` (matches sibling `Refresh_*` tests). The other 3 → pass `noMft: true` (their search paths are already empty/nonexistent, so the recursive fallback is a no-op). NOT "temp search dir alone" - that does not stop the unconditional spawn.
- **Test isolation (pre-existing `SettingsViewModelTests` bug, folded in):** `GitWizardApi.GetLocalFilesPath()` honors a `GITWIZARD_HOME` env override; redirect tests to a temp dir (also fixes the `AsyncFileIOTests` delete-real-file hack + `GitWizardReportAdditionalTests` cache tests touching real `~/.GitWizard`).
- **Coverage:** follow MFTLib's apparatus - `[Category("RequiresAdmin")]` + `Assert.Inconclusive` guard for real-privilege paths; `scripts/run-coverage.ps1` self-elevates (one UAC), `-NonInteractive` for CI; root `TEST-REPORT.md`. Standard = **no regression of ACTUAL covered lines/branches** (not the 45% gate number); aim 100% on new code; 0 `[ExcludeFromCodeCoverage]` (interactive run covers the elevation boundary). See `~/.claude/notes/idioms_mftlib_elevation_testing.md`.

**Sequencing:** MFTLib 0.3 (seam + tests, keep 100%) → git-wizard via local ProjectReference swap (spec Parts B-E) → ship MFTLib 0.3.0 NuGet → flip git-wizard back to PackageReference → file-wizard adopts later (`~/file-wizard` branch `feat/elevation-provider-prep`).

**Investigation starting points:** `GitWizard/GitWizardApi.cs` `TryFindAllRepositoriesUsingMft` (~L132) + `RunElevatedMftScan`; `GitWizard/WindowsDefenderException.cs`; cross-repo memory `~/.claude/notes/idioms_mftlib_elevation_testing.md` + `project_mftlib_wizard_family.md`.

## RELATED - pre-existing test-isolation bug (found this session; fix alongside UAC)

`SettingsViewModelTests` persist `ForkPath` / search paths to the **real** `~/.GitWizard/config.json`. Running the class as a subset fails 2 tests (`Construction_LoadsConfiguration`, `AddSearchPathAsync_AddsPathFromPicker`) from cross-test pollution; the full CI suite's ordering masks it. **Verified pre-existing** (reproduces on the clean baseline with this session's changes stashed) - *not* caused by the inspection cleanup. Same family as the UAC issue (tests touching real user state). Fix: point SettingsViewModel tests at a temp config dir.

## Decisions log
- Inspection: idiomatic .NET naming kept; Rider config fixed, no code renames. Closure false-positives fixed **structurally** (value-capture for `AccessToDisposedClosure`, `StrongBox<int>` for `AccessToModifiedClosure`), not suppressed - see `~/.claude/notes/feedback_inspections_refactor_over_suppress.md`. Suppression reserved for true-but-intentional (`FunctionNeverReturns` daemon loop) or protective (serialized `Path` getter used by STJ).
- Keep the 4 STJ-boundary null guards (don't enable `RespectNullableAnnotations`).
- jb CLI naming results are NOT authoritative for this repo (glued to the global config).
- All work lands on `retire-maui-rename-ui` / PR #51.
