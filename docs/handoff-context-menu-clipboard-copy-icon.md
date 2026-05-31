# Handoff - context-menu + clipboard fixes, copy-icon UX change, and clipboard coverage (all done)

**Date:** 2026-05-27. **Branch:** `main` (local, held - see "Push gate" below).

## DONE this session (3 fixes, committed; see git log)

All three were found by finally smoke-testing the GUI in a real `--headed` window (the carry-forward item from `inspection-cleanup-handoff.md`).

1. **Context menu was completely broken.** `MainWindow.axaml` had `<ContextMenu DataContext="{Binding $self.PlacementTarget.DataContext}">`. `ContextMenu.PlacementTarget` is **always null** in Avalonia 11 ([#16344](https://github.com/AvaloniaUI/Avalonia/issues/16344)), so the binding failed (`'Value is null'` spam) AND blocked the menu's natural DataContext inheritance → every item's `Tag`/`IsVisible` was unbound, so all menu items no-opped.
   **Fix:** dropped the binding; the owning `Grid` now has `ContextRequested="OnRepositoryContextRequested"`, and the handler sets `menu.DataContext = control.DataContext` (the row VM). Files: `GitWizardUI/Views/MainWindow.axaml`, `MainWindow.axaml.cs`. Verified: Fork/Explorer/Copy work; "Open in Fork" hides on group headers.

2. **Clipboard crashed the whole app.** `AvaloniaClipboardService._clipboard` was typed `dynamic`. `dynamic` dispatch can't see explicitly-implemented interface members, so `_clipboard.SetTextAsync(...)` threw `RuntimeBinderException: 'object' does not contain a definition for 'SetTextAsync'` → unhandled → process exit. Latent forever (the broken menu made "Copy" unreachable).
   **Fix:** typed `_clipboard` as `IClipboard?` (`Avalonia.Input.Platform`). File: `GitWizardUI/Services/AvaloniaClipboardService.cs`.

3. **Leaky test destroyed the real `~/.GitWizard/config.json`.** `GitWizardConfigurationTests` was the one config-touching test class Part D's `GITWIZARD_HOME` redirect missed; `SaveGlobalConfiguration_SavesConfiguration` wrote `/global/test` to the user's real config on every `dotnet test`. **Fix:** SetUp now calls `TestUtilities.RedirectLocalFilesToTemp()` / TearDown `ClearLocalFilesRedirect` (mirrors the 4 sibling classes). File: `GitWizardTests/GitWizardConfigurationTests.cs`. Verified: 25/25 pass, real config byte-identical before/after a run. (A backup of the real config sits at `%TEMP%\gitwizard-config-backup-20260527-031422.json` in case it's wanted.)

## DONE (2026-05-27) - replaced the "Copied" dialog with a row icon (the user's ask)

**Implemented as sketched below.** `RepositoryNodeViewModel.JustCopied` (transient, notifying) drives a right-aligned green "✓ Copied" in the row's text column (`MainWindow.axaml`, `IsVisible="{Binding JustCopied}"`, no layout shift). `MainViewModel.CopyToClipboardAsync` sets it on the UI thread (`_ui.Post`) after the clipboard write and clears it after `CopiedIndicatorMilliseconds` (1500). The command is now an `AsyncRelayCommand`, so a clipboard failure is logged by that wrapper instead of going unobserved - and the indicator is never lit on failure (the awaited write throws first). Tests: four `MainViewModelTests` (lights / clears-after-delay / null+group-header guard / fails-without-lighting) all TDD'd red→green. The original behavior, for reference:

**Old behavior:** `MainViewModel.CopyToClipboard(node)` (≈ line 321) did:
```csharp
_ = _clipboard.SetPlainTextAsync(node.WorkingDirectory)
    .ContinueWith(_ => ShowAlert("Copied", "Working directory path copied to clipboard"), TaskScheduler.Default);
```
**Wanted:** no modal/toast dialog. Instead a check/copy icon on the repo's row that **lights up briefly** when the path is copied, then fades/clears.

**Implementation sketch:**
- `RepositoryNodeViewModel`: add a transient `JustCopied` (bool, `INotifyPropertyChanged`) or a small visibility property. Set it `true` when this node's path is copied; reset to `false` after ~1.5 s via a `DispatcherTimer` (or `_ui.Post` + delay). Keep it framework-agnostic per the app's convention (string/bool in the VM, visuals in the view).
- `MainWindow.axaml`: in the `ListBox.ItemTemplate` `Grid` (columns `20,*,Auto,Auto,Auto,Auto`), add a small `TextBlock`/icon (e.g. a "✓" glyph) in/near the action-button columns, `IsVisible="{Binding JustCopied}"`. The Fork button column is the model to copy.
- `CopyToClipboard`: drop `ShowAlert(...)`. After the clipboard `SetTextAsync` completes, set `node.JustCopied = true` **on the UI thread** - the current `ContinueWith` uses `TaskScheduler.Default` (off-thread), so marshal via `_ui.Post(...)` (the app already does this in `ShowAlert`). Don't touch the VM off-thread.
- Tests use `StubClipboardService`, so the copied-flag set/reset is unit-testable without a real clipboard - add a test that `CopyToClipboard` flips `JustCopied` true (and that it resets). Closes part of the coverage gap below.

## Push gate (why this is held, not pushed)

`GitWizard/GitWizard.csproj` currently has the **MFTLib local `ProjectReference`** (line 13), the publish-gated state from the elevation work. `dotnet build` (what CI uses) **cannot build MFTLib's native `.vcxproj`**, so **pushing `main` breaks CI**. MFTLib 0.3.0 is not yet published to NuGet (user-gated). Until the publish + flip-back-to-`PackageReference`, `main` stays local.

Local `main` is now **gitea/main (`2b26b92`) + 4 elevation/docs commits + this session's commits** (held).

**Two ways to ship these fixes:**
- **(a) Wait for the MFTLib publish** - publish 0.3.0, flip csproj to `<PackageReference Include="MFTLib" Version="0.3.0" />`, push `main`; CI builds green and everything ships together. (Matches `docs/handoff-mftlib-0.3-elevation-integration.md`.)
- **(b) Ship the GUI fixes now, separately** - the **context-menu (#1) and clipboard (#2) fixes are independent of the elevation work** and build against gitea/main's `PackageReference 0.2.0`. They can be cherry-picked onto a branch off `gitea/main` and PR'd to ship today (CI green). **The leaky-test fix (#3) depends on Part D infra (`GITWIZARD_HOME`/`TestUtilities`) which only exists on held `main`**, so it can't go to gitea/main without Part D - leave it on held `main`. (The session's commit keeps #1+#2 together where possible to ease this extraction - check `git log`.)

## Coverage gap - CLOSED (2026-05-27)

`AvaloniaClipboardService` (the real `IClipboard` path) is now covered by `GitWizardTests/AvaloniaClipboardServiceTests.cs`: a headless `[AvaloniaTest]` (new `Avalonia.Headless.NUnit` package + `TestAppBuilder` / `[assembly: AvaloniaTestApplication]`) builds a real `TopLevel`, writes through `AvaloniaClipboardService.SetPlainTextAsync`, and reads the value back from `TopLevel.Clipboard` - i.e. it exercises the exact `SetTextAsync` path the `dynamic` crash hit, which `StubClipboardService` couldn't. Non-admin line coverage 41.45% → 42.35%. Remaining: the `?? Task.CompletedTask` null-clipboard branch is a defensive guard not reachable via a headless `TopLevel` (whose `Clipboard` is non-null) - left as-is, single line is covered.

## Whole-row right-click (DONE 2026-05-27, with a trade-off)

Right-click only worked on the row label/buttons - empty row space missed the menu, because `ContextRequested` was on the inner `Grid`, which didn't fill the row (its `Margin` left a gap and the `ListBoxItem` didn't stretch its content). `ContextRequested` bubbles **up** to the `ListBox` from the clicked element; it never reaches a `Grid` the cursor isn't over.

**Fix:** wrap each row in a `Border` (`Background="Transparent"`, `Padding="5,5,20,5"` - `Grid` has **no** `Padding` property in Avalonia, `AVLN2000`) carrying the `ContextMenu` + `ContextRequested`, plus a `ListBox.Styles` `Style Selector="ListBoxItem"` setting `HorizontalContentAlignment="Stretch"` + `Padding="0"` so the Border fills the whole row. Right-click now works anywhere on the row.

**Trade-off (open follow-up):** zeroing `ListBoxItem.Padding` made rows visibly tighter (user noticed). To restore row padding **and** keep whole-row hit-testing, attach the context menu at the **`ListBoxItem` container level** (an `ItemContainerTheme`/`Style` `ContextMenu` setter, or a `ListBox`-level `ContextRequested` handler that walks `e.Source` up to the `ListBoxItem`) instead of zeroing padding. Accepted as-is this session. See `~/.claude/notes/idioms_avalonia.md`.

## Data-loss incident (FIXED 2026-05-27)

Running the test suite this session **deleted the user's real `~/.GitWizard/config.json`** (lost the `G:\Documents` search path). Root cause: `GitWizardApiAdditionalTests` calls `GitWizardApi.DeleteAllLocalFiles()` without redirecting `GITWIZARD_HOME`, so `GetLocalFilesPath()` resolved to the real `~/.GitWizard` and `Directory.Delete` wiped it; the next GUI launch wrote a default config (search paths gone). The per-class opt-in redirect had now leaked twice (Part D's `GitWizardConfigurationTests`, then this).

**Fix:** `GitWizardTests/GlobalTestSetup.cs` - an assembly-wide `[SetUpFixture]` points `GITWIZARD_HOME` at a temp dir (under the user profile) for the whole run, so isolation is the default; `TestUtilities.ClearLocalFilesRedirect` restores that default instead of nulling; regression guard `GitWizardApiAdditionalTests.GetLocalFilesPath_NeverResolvesToTheRealGitWizardDirectoryDuringTests`. Verified: a sentinel written to the real config survives a full run; config intact. The user's config was restored from `%TEMP%\gitwizard-config-backup-…json`. Full detail in project memory `test-isolation-gitwizard-home`. (Like the earlier leaky-test fix, this depends on Part D's `GITWIZARD_HOME` infra, so it lives only on held `main`.)

## Push state (2026-05-27)

This session added **5 commits** to held local `main` (`45711eb` test-isolation → `f77a395` Settings height); local `main` is now **12 ahead of `gitea/main`**, still held - the push gate above is unchanged (MFTLib local `ProjectReference`, publish-gated on MFTLib 0.3.0). User chose to **hold the push**. After the config restore, the cached `repositories.txt` is stale (it holds the default-config scan) → **Shift+click Refresh** (hard refresh) to rediscover with the real search paths.
