# Retire MAUI from git-wizard — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Avalonia app the sole desktop GUI for git-wizard (Windows/macOS/Linux) and remove the redundant MAUI app (`GitWizardUI`), after first closing Avalonia's Windows feature gap.

**Architecture:** Two PRs gated by a Windows verification checkpoint. PR1 ports MAUI's two Windows-only features into Avalonia — self-elevated MFT scanning (the core relaunches the current exe with `--elevated-mft`) and Windows Defender exclusions — and is verified on a real Windows host. Only then does PR2 delete the MAUI (`GitWizardUI`) and `GitWizardUI.UITests` projects and strip them from the solution, CI, release workflow, and docs. The shared `GitWizardUI.ViewModels` project is untouched (Avalonia keeps consuming it). Version stays an informal lockstep keyed to the UI's version, which moves from `GitWizardUI.csproj/ApplicationDisplayVersion` to `GitWizardAvalonia.csproj/<Version>`.

**Tech Stack:** C# / .NET 10, Avalonia 11.2, NUnit, Gitea Actions (`ci.yml`, `release.yml`).

**Coverage note:** The CI coverage gate measures only `GitWizard` + `GitWizardUI.ViewModels` (via `GitWizardTests`). Neither PR touches those, so the `--gate-line 33` gate is unaffected by this work. The Avalonia entry point and the CI/release YAML are not unit-testable; their "tests" are a successful build, manual Windows verification, green CI on both runners, and a smoke release.

---

## Phase 1: PR1 — Avalonia Windows parity

> ## 🤝 HANDOFF 2026-05-24 (llamabox → chonkers)
>
> **State:** Branch `retire-maui` pushed; **PR #47** open & mergeable
> (`https://gitea.llamabox.internal/schoen/git-wizard/pulls/47`). Commits:
> `0dead0c` (PR1 code: Tasks 1 & 2) + `17d9d3e` (plan status). Avalonia builds
> clean on Linux (0 warnings/errors). Tasks 1 & 2 are **done**.
>
> **To resume:** `git fetch && git checkout retire-maui`, then continue **Task 3
> (Windows verification)** below — needs a Windows host (the app + UAC can't be
> exercised on Linux). After it passes, merge PR #47; Phase 2 stays gated on that.
>
> **⚠️ In-progress finding — "refresh didn't prompt for admin":** Almost certainly
> NOT a bug in the PR1 code, but a test-setup artifact: the GUI's **Refresh**
> uses the cached repo list (`%USERPROFILE%\.GitWizard\repositories.txt`), so it
> re-refreshes *known* repos and never runs an MFT **discovery** scan — and the
> elevation/UAC path (`GitWizardApi.TryFindAllRepositoriesUsingMft` →
> `ElevationUtilities.TryRunElevated("--elevated-mft …")`) only fires during
> **discovery**. The app.manifest is `asInvoker` (no forced elevation), so the
> app runs non-elevated and *should* prompt during a real discovery scan.
>
> **How to actually exercise it on Windows:**
> 1. Force a fresh discovery scan — in the GUI: **Clear Cache** menu → **Refresh**;
>    or delete `%USERPROFILE%\.GitWizard\repositories.txt` then Refresh; or run
>    the CLI `git-wizard -rebuild-repo-list` (or `-rebuild-all`).
> 2. On a **non-elevated** launch, discovery should pop a UAC prompt (the
>    `--elevated-mft` child). Approve → scan completes in seconds, **no second GUI
>    window** appears, repos populate. (If you launched *already elevated*, no
>    prompt is expected — `IsElevated()` scans directly; relaunch non-elevated to
>    see the UAC path.)
> 3. **Logs:** `%USERPROFILE%\.GitWizard\Logs\` — a line `MFT search failed,
>    falling back to directory scan: …` means MFT was attempted but failed
>    (investigate the error). **No** MFT log lines at all ⇒ MFT was never
>    attempted (still a cached refresh, or `-no-mft`, or discovery didn't run).
> 4. **Defender:** click **Check Windows Defender** → expect UAC, then "Defender
>    Exclusions Added"; confirm with `Get-MpPreference | Select -Expand ExclusionProcess`
>    (should list dotnet / git / git-lfs / git-wizard).
>
> **⚠️ Open question chonkers must close:** confirm the GUI's discovery scan
> actually *routes through* the elevated path. There are two MFT entry points —
> `TryFindAllRepositoriesUsingMft` (all-paths, has the `TryRunElevated` branch at
> `GitWizardApi.cs:162`) and the per-path `GetRepositoryPaths` →
> `TryFindGitRepositoriesUsingMft` (no self-elevation). Trace which one
> `GitWizardReport.GenerateReport(...)` calls for a full scan from the GUI. If the
> GUI discovery never reaches `TryRunElevated`, that's a **real gap to fix in
> PR1**, not just a test artifact — the Program.cs handler would then be dead code
> on the GUI path. (The CLI's `-rebuild-repo-list` exercising MFT is a good
> cross-check that the core path works at all.)
>
> **Also paused:** issue #36 ViewModel test backfill (we pivoted to the MAUI
> retirement before starting it).

Branch: `retire-maui` (already created off `main`). This phase's only automated check is that Avalonia still **builds**; the elevation and Defender code paths require Windows + UAC and are verified manually (Task 3). Do NOT proceed to Phase 2 until Task 3's Windows verification passes and PR1 is merged.

### Task 1: Add the elevated-mode startup handler to Avalonia

The core's `GitWizardApi.GetRepositoryPaths` and `WindowsDefenderException.AddExclusions` self-elevate by relaunching the **current** executable with `--elevated-mft` / `--elevated-defender`. Avalonia currently ignores those flags, so a relaunched child opens a second GUI window and the parent times out (120 s) into the recursive-scan fallback. Handle the flags before any Avalonia initialization so the child does its one job and exits.

**Files:**
- Modify: `GitWizardAvalonia/Program.cs`

- [ ] **Step 1: Rewrite `Program.cs` to dispatch elevated modes before launching the UI**

Replace the entire contents of `GitWizardAvalonia/Program.cs` with:

```csharp
using Avalonia;
using GitWizard;
using System;

namespace GitWizardAvalonia;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Self-elevation child modes: the core (GitWizardApi.GetRepositoryPaths /
        // WindowsDefenderException) relaunches THIS exe with these flags. Handle
        // them before any Avalonia init so the elevated child performs its single
        // task and exits instead of opening a second GUI window.
        if (TryHandleElevatedMode(args))
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    static bool TryHandleElevatedMode(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--elevated-mft":
                {
                    string? configPath = null;
                    string? outputPath = null;
                    for (var j = i + 1; j < args.Length; j++)
                    {
                        switch (args[j])
                        {
                            case "--config-path":
                                if (j + 1 < args.Length) configPath = args[++j];
                                break;
                            case "--output":
                                if (j + 1 < args.Length) outputPath = args[++j];
                                break;
                        }
                    }

                    if (configPath != null && outputPath != null)
                    {
                        GitWizardApi.RunElevatedMftScan(configPath, outputPath);
                        Environment.Exit(0);
                    }

                    Environment.Exit(1);
                    return true;
                }
                case "--elevated-defender":
                    Environment.Exit(WindowsDefenderException.RunDefenderCommands() ? 0 : 1);
                    return true;
            }
        }

        return false;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
```

- [ ] **Step 2: Build Avalonia to confirm it compiles (Linux is fine)**

Run: `dotnet build GitWizardAvalonia/GitWizardAvalonia.csproj -c Debug`
Expected: `Build succeeded` with 0 errors. (`GitWizardApi.RunElevatedMftScan` and `WindowsDefenderException.RunDefenderCommands` live in the cross-platform `GitWizard` core lib, so they compile on Linux even though they only do work on Windows.)

- [ ] **Step 3: Commit**

```bash
git add GitWizardAvalonia/Program.cs
git commit -m "feat(avalonia): handle --elevated-mft/--elevated-defender at startup

Mirrors MAUI's TryHandleElevatedMode so the core's self-elevation
(which relaunches the current exe) works for the Avalonia GUI on
Windows instead of timing out into the recursive-scan fallback.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 2: Wire the Defender menu item to actually add exclusions

`MainWindow.CheckWindowsDefenderMenuItem_Click` is currently a no-op stub (`await Task.CompletedTask;`). Replace it with the real call, mirroring MAUI's `MainPage` handler.

**Files:**
- Modify: `GitWizardAvalonia/Views/MainWindow.axaml.cs`

- [ ] **Step 1: Add the `GitWizard` using directive**

In `GitWizardAvalonia/Views/MainWindow.axaml.cs`, the current usings are:

```csharp
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GitWizardAvalonia.Services;
using GitWizardUI.ViewModels;
```

Add `using GitWizard;` so `WindowsDefenderException` resolves:

```csharp
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GitWizard;
using GitWizardAvalonia.Services;
using GitWizardUI.ViewModels;
```

- [ ] **Step 2: Replace the stub handler with the real implementation**

Replace this exact block:

```csharp
    async void CheckWindowsDefenderMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows()) return;
        await Task.CompletedTask;
    }
```

with:

```csharp
    async void CheckWindowsDefenderMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows()) return;

        var success = await Task.Run(WindowsDefenderException.AddExclusions);
        await new AvaloniaUserDialogs().DisplayAlertAsync(
            success ? "Defender Exclusions Added" : "Defender Setup Failed",
            success
                ? "Process exclusions for dotnet, git, git-lfs, and git-wizard have been added."
                : "Failed to add Windows Defender exclusions. You may need to run as administrator.");
    }
```

(`AvaloniaUserDialogs` has a parameterless constructor and resolves the owner window itself — `MainWindow`'s constructor already creates one the same way.)

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build GitWizardAvalonia/GitWizardAvalonia.csproj -c Debug`
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add GitWizardAvalonia/Views/MainWindow.axaml.cs
git commit -m "feat(avalonia): wire Check Windows Defender menu to AddExclusions

Replaces the no-op stub with the real WindowsDefenderException call
+ a result dialog, matching MAUI's MainPage handler.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 3: Push, open PR1, and verify on Windows (GATE)

**Files:** none (verification + PR).

- [ ] **Step 1: Push the branch**

Run: `git push -u origin retire-maui`
Expected: branch published; a PR-create URL is printed.

- [ ] **Step 2: Open PR1 against `main`**

Title: `feat(avalonia): Windows MFT self-elevation + Defender exclusions`
Body: summarize that this closes Avalonia's Windows parity gap (MFT self-elevation + Defender) ahead of retiring MAUI; link the design (`docs/superpowers/specs/2026-05-24-retire-maui-design.md` history) and note PR2 (MAUI deletion) follows after Windows verification.

- [ ] **Step 3: Wait for CI green on both runners**

Watch `https://gitea.llamabox.internal/schoen/git-wizard/actions`. Expected: `build-linux` and `test-windows` both pass; the coverage gate stays at/above 33% (unchanged — Avalonia is outside the coverage scope).

- [ ] **Step 4: Manual Windows verification (run on the Windows host)**

Build/run the Avalonia app on Windows from this branch, **non-elevated**:
- Trigger a refresh. Expected: a UAC prompt appears; after consent, the MFT scan completes promptly (seconds), NOT a ~120 s hang followed by a recursive-scan fallback, and no second GUI window appears.
- Click the **Check Windows Defender** menu item. Expected: a UAC prompt, then a "Defender Exclusions Added" dialog; confirm the exclusions exist (`Get-MpPreference | Select -Expand ExclusionProcess` lists dotnet/git/git-lfs/git-wizard).

Record the result in the PR. If either check fails, fix on this branch and re-verify before merging.

- [ ] **Step 5: Merge PR1**

Merge once CI is green and Windows verification passes. This unblocks Phase 2.

---

## Phase 2: PR2 — Delete MAUI + cleanup

**Precondition:** PR1 merged to `main` and Windows-verified. Start from an up-to-date `main`.

### Task 4: Delete the MAUI projects, fix the solution, set the Avalonia version

**Files:**
- Delete: `GitWizardUI/` (entire directory)
- Delete: `GitWizardUI.UITests/` (entire directory)
- Modify: `git-wizard.slnx`
- Modify: `GitWizardAvalonia/GitWizardAvalonia.csproj`

- [ ] **Step 1: Create the PR2 branch off fresh main**

```bash
git switch main && git pull
git switch -c retire-maui-delete
```

- [ ] **Step 2: Remove the two MAUI projects from `git-wizard.slnx`**

Delete these two lines from `git-wizard.slnx` (lines 4–5):

```xml
  <Project Path="GitWizardUI.UITests/GitWizardUI.UITests.csproj" />
  <Project Path="GitWizardUI/GitWizardUI.csproj" />
```

The file should then contain exactly:

```xml
<Solution>
  <Project Path="git-wizard/git-wizard.csproj" />
  <Project Path="GitWizard/GitWizard.csproj" />
  <Project Path="GitWizardTests/GitWizardTests.csproj" />
  <Project Path="GitWizardUI.ViewModels/GitWizardUI.ViewModels.csproj" />
  <Project Path="GitWizardAvalonia/GitWizardAvalonia.csproj" />
</Solution>
```

- [ ] **Step 3: Add the version anchor to `GitWizardAvalonia.csproj`**

In the first `<PropertyGroup>` of `GitWizardAvalonia/GitWizardAvalonia.csproj`, add a `<Version>` line after `<RootNamespace>GitWizardAvalonia</RootNamespace>`:

```xml
    <RootNamespace>GitWizardAvalonia</RootNamespace>
    <Version>0.4.1</Version>
```

(0.4.1 is the current value carried over from the deleted `GitWizardUI.csproj/ApplicationDisplayVersion`.)

- [ ] **Step 4: Delete the MAUI project directories**

```bash
git rm -r GitWizardUI GitWizardUI.UITests
```

- [ ] **Step 5: Build the solution to confirm nothing else referenced MAUI**

Run: `dotnet build git-wizard.slnx -c Debug`
Expected: `Build succeeded`, 0 errors. (If a reference dangles, Task 8's grep catches it; nothing in the repo should reference `GitWizardUI` except the now-removed slnx entries.)

- [ ] **Step 6: Commit**

```bash
git add git-wizard.slnx GitWizardAvalonia/GitWizardAvalonia.csproj
git commit -m "chore: delete MAUI (GitWizardUI) + UITests, anchor version on Avalonia

Avalonia is now the sole desktop GUI. Version moves to
GitWizardAvalonia.csproj <Version> (0.4.1).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 5: Strip MAUI from the release workflow and move the tag-assertion

**Files:**
- Modify: `.gitea/workflows/release.yml`

- [ ] **Step 1: Remove the MAUI csproj from the PR `paths:` trigger**

Delete this line (line 10) from the `on.pull_request.paths` list:

```yaml
      - 'GitWizardUI/GitWizardUI.csproj'
```

- [ ] **Step 2: Add the tag-version assertion into `publish-cross`**

In the `publish-cross` job, insert a new step immediately AFTER the `Resolve version` step (the one with `id: ver`) and BEFORE `Publish CLI`:

```yaml
      - name: Validate tag matches Avalonia <Version> (push only)
        if: github.event_name == 'push'
        run: |
          appVer=$(grep -oP '(?<=<Version>)[^<]+' GitWizardAvalonia/GitWizardAvalonia.csproj)
          tagVer="${{ steps.ver.outputs.version }}"
          if [ "$appVer" != "$tagVer" ]; then
            echo "Tag version '$tagVer' does not match GitWizardAvalonia.csproj <Version> '$appVer'. Bump the csproj or move the tag." >&2
            exit 1
          fi
          echo "Version match: $tagVer"
```

- [ ] **Step 3: Delete the entire `publish-maui` job**

Remove the whole `publish-maui:` job (originally lines 95–169), from `  publish-maui:` through its final `if-no-files-found: error` (the `Upload MAUI zip` step). The deleted job contained the old `Validate tag matches ApplicationDisplayVersion` step — its replacement now lives in `publish-cross` (Step 2).

- [ ] **Step 4: Drop `publish-maui` from the release job's `needs`**

Change:

```yaml
    needs: [publish-cross, publish-maui]
```

to:

```yaml
    needs: [publish-cross]
```

- [ ] **Step 5: Lint the YAML**

Run: `python3 -c "import yaml,sys; yaml.safe_load(open('.gitea/workflows/release.yml')); print('ok')"`
Expected: `ok` (valid YAML; no leftover `publish-maui` references).

- [ ] **Step 6: Confirm no MAUI references remain in the file**

Run: `grep -n -i 'maui\|GitWizardUI/\|GitWizardUI\.csproj\|ApplicationDisplayVersion' .gitea/workflows/release.yml`
Expected: no output.

- [ ] **Step 7: Commit**

```bash
git add .gitea/workflows/release.yml
git commit -m "ci(release): drop MAUI publish job, anchor tag-assert on Avalonia <Version>

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 6: Strip MAUI from the CI workflow

**Files:**
- Modify: `.gitea/workflows/ci.yml`

- [ ] **Step 1: Remove the three MAUI-only steps from the `test-windows` job**

Delete the `Cache MAUI workload manifests` step (originally lines 116–124), the `Install MAUI workload (Windows-only)` step (126–128), and the `Restore MAUI runtime pack (win-x64)` step (133–134). The `test-windows` steps should go straight from `Cache NuGet packages` → `Restore (full solution)` → `Build (full solution)` → `Test`:

```yaml
      - name: Cache NuGet packages
        uses: actions/cache@v4
        with:
          path: ~\.nuget\packages
          key: windows-nuget-${{ hashFiles('**/*.csproj') }}
          restore-keys: |
            windows-nuget-

      - name: Restore (full solution)
        run: dotnet restore git-wizard.slnx

      - name: Build (full solution)
        run: dotnet build git-wizard.slnx -c Release --no-restore

      - name: Test
        shell: pwsh
        run: |
          dotnet test GitWizardTests/GitWizardTests.csproj `
              -c Release --no-build `
              --logger "trx;LogFileName=TestResults.trx" `
              --results-directory ./TestResults
```

(With MAUI removed from the slnx, `dotnet restore/build git-wizard.slnx` no longer needs the workload. The Windows job keeps running the full NUnit suite for cross-platform coverage.)

- [ ] **Step 2: Lint the YAML and confirm no MAUI references remain**

Run: `python3 -c "import yaml; yaml.safe_load(open('.gitea/workflows/ci.yml')); print('ok')" && grep -n -i 'maui\|GitWizardUI/\|GitWizardUI\.csproj' .gitea/workflows/ci.yml`
Expected: `ok` then no further output.

- [ ] **Step 3: Commit**

```bash
git add .gitea/workflows/ci.yml
git commit -m "ci: drop MAUI workload + runtime-pack steps from Windows job

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 7: Update CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

Remove every MAUI-specific section so the docs describe the Avalonia-only reality. Make these edits:

- [ ] **Step 1: Projects list** — delete the `GitWizardUI/` (MAUI) and `GitWizardUI.UITests/` bullets; keep `GitWizardUI.ViewModels/`, `GitWizardAvalonia/`, `GitWizardAvalonia.Screenshot/`, and the `GitWizardTests/` line (drop the `GitWizardUI.UITests` mention from it).

- [ ] **Step 2: Build section** — delete the MAUI build command block, the "Publish MAUI" command, and reframe the intro: normal builds use plain `dotnet build`; the "use VS2026 MSBuild" guidance now applies ONLY to MFTLib **local ProjectReference** development (the CLI's NuGet PackageReference path and Avalonia build with plain `dotnet`). Keep the Avalonia build command and the MFTLib Local Development section.

- [ ] **Step 3: Key Architecture section** — delete the MAUI-specific bullets: the `CollectionView` (MAUI) note, the "UI command queue" note if it references MAUI specifically (keep it if it describes the shared ViewModel behavior — reword to "the Avalonia app / ViewModels"), and update `IUpdateHandler` / `Self-elevation` bullets to say "CLI and Avalonia" instead of "CLI and MAUI".

- [ ] **Step 4: Release checklist** — replace step 1 ("Update `ApplicationDisplayVersion` and `PackageVersion` in `GitWizardUI/GitWizardUI.csproj`") with "Update `<Version>` in `GitWizardAvalonia/GitWizardAvalonia.csproj`"; keep the CLI-help-text bump step; drop the `GitWizardUI-{ver}.zip` from the release-artifacts list.

- [ ] **Step 5: CI infrastructure section** — drop the MAUI workload mention from the Windows-runner description and the `publish-maui` reference; update the "Deliberate non-goals" bullet (the MAUI macOS/Android note and the `GitWizardUI.UITests` note become moot — replace with "no MAUI app (retired 2026-05-24; Avalonia is the sole GUI)").

- [ ] **Step 6: Tips section** — delete the `taskkill /IM GitWizardUI.exe` tip and the MAUI `CollectionView` scroll-fix / `IsEnabled` binding tips (all MAUI-specific).

- [ ] **Step 7: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: drop MAUI from CLAUDE.md (Avalonia is the sole GUI)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 8: Dangling-ref sweep, verification, PR2, smoke release, memory

**Files:** none (verification + PR + memory).

- [ ] **Step 1: Sweep for any remaining MAUI references**

Run: `grep -rn -i 'GitWizardUI\b\|GitWizardUI/\|GitWizardUI\.csproj\|maui\|ApplicationDisplayVersion' . --include='*.cs' --include='*.csproj' --include='*.slnx' --include='*.yml' --include='*.md' | grep -v -E 'GitWizardUI\.ViewModels|docs/superpowers/(plans|specs)/'`
Expected: no output (the only legitimate `GitWizardUI*` token left is `GitWizardUI.ViewModels`). Investigate and clean anything that prints (e.g. `HANDOFF.md`, `README`, other docs).

- [ ] **Step 2: Full clean build + tests (Linux)**

Run: `dotnet build git-wizard.slnx -c Release && dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build`
Expected: build succeeds; all tests pass.

- [ ] **Step 3: Push and open PR2**

```bash
git push -u origin retire-maui-delete
```
Title: `chore: retire MAUI — Avalonia is the sole desktop GUI`
Body: link PR1; summarize deletion + CI/release/version/docs changes; note the smoke release was exercised (Step 5).

- [ ] **Step 4: Wait for CI green on both runners**

Watch the Actions page. Expected: `build-linux` + `test-windows` pass (Windows job now builds the MAUI-free solution without the workload); coverage gate ≥ 33%.

- [ ] **Step 5: Smoke-test the release workflow (before merge)**

PR-triggered `release.yml` runs in dry-run/draft mode and self-cleans. Confirm on the Actions page that the `release` workflow for PR2: builds the Avalonia + CLI zips for all 3 RIDs, has NO `publish-maui` job, the tag-assert step is present in `publish-cross`, and the draft smoke release + tag are deleted by `Smoke cleanup`. Expected assets: `git-wizard-*-{win,linux,osx}-x64.zip` and `GitWizardAvalonia-*-{win,linux,osx}-x64.zip` only — no `GitWizardUI-*.zip`.

- [ ] **Step 6: Merge PR2**

Merge once CI is green and the smoke release looks correct.

- [ ] **Step 7: Update the project memory note**

Edit `/home/schoen/.claude/notes/` (or the per-project memory) entry behind `project_avalonia_migration`: change "MAUI retained" → "MAUI retired 2026-05-24; Avalonia is the sole GUI". Update the `MEMORY.md` one-line pointer accordingly.

- [ ] **Step 8: Close the tracking issue**

If a Gitea issue was filed for the MAUI retirement, comment with the two merged PRs and close it. (Resume issue #36's ViewModel test backfill next — it's out of scope here.)
