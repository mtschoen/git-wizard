# Retire MAUI, rename Avalonia → GitWizardUI, fold in the ViewModels — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Avalonia app the sole desktop GUI, renamed `GitWizardUI`, with the `GitWizardUI.ViewModels` project folded into it (no separate VM project). End state: five projects (`git-wizard`, `GitWizard`, `GitWizardUI`, `GitWizardUI.Screenshot`, `GitWizardTests`) instead of seven.

**Architecture:** A pure structural refactor — no production behavior change. Delete the MAUI `GitWizardUI` + `GitWizardUI.UITests`; rename `GitWizardAvalonia/` → `GitWizardUI/` (folder, csproj, `RootNamespace`); move `GitWizardUI.ViewModels/*.cs` into `GitWizardUI/ViewModels/` keeping their existing `namespace GitWizardUI.ViewModels` (nests cleanly under the new root, so VM source is untouched); rename `GitWizardAvalonia.Screenshot` → `GitWizardUI.Screenshot`. `GitWizardTests` references the new `GitWizardUI` project. The coverage gate is **re-baselined to the whole `GitWizardUI` assembly** (the app's untestable Avalonia glue now sits in scope and lowers the line %). Done as one PR; each phase leaves `dotnet build git-wizard.slnx` green. Supersedes Phase 2 of `2026-05-24-retire-maui.md` (Phase 1 — Avalonia Windows parity — already merged in PR #47).

**Tech Stack:** C# / .NET 10, Avalonia 11.2, NUnit 4, coverlet, Gitea Actions (`ci.yml`, `release.yml`, `screenshot.yml`), `ci/post-coverage-status.py`.

---

## Phase 5: CI, release, screenshot workflows + coverage re-baseline

### Task 5: Update `ci.yml`

**Files:**
- Modify: `.gitea/workflows/ci.yml`

- [x] **Step 1: Update the Linux job's project lists (rename + fold)**

In BOTH the `Restore (cross-platform projects)` and `Build (cross-platform projects)` steps, replace the two lines:
```
            GitWizardUI.ViewModels/GitWizardUI.ViewModels.csproj \
            GitWizardAvalonia/GitWizardAvalonia.csproj \
```
with the single line:
```
            GitWizardUI/GitWizardUI.csproj \
```
So each loop lists: `GitWizard`, `git-wizard`, `GitWizardUI`, `GitWizardTests`.

- [x] **Step 2: Remove the three MAUI-only steps from the `test-windows` job**

Delete the `Cache MAUI workload manifests` step (lines 116–124), the `Install MAUI workload (Windows-only)` step (126–128), and the `Restore MAUI runtime pack (win-x64)` step (133–134). The `test-windows` steps go straight from `Cache NuGet packages` → `Restore (full solution)` → `Build (full solution)` → `Test`:

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

(With MAUI gone from the slnx, `dotnet restore/build git-wizard.slnx` needs no workload.)

- [x] **Step 3: Re-baseline the coverage gate — measure first**

Run the Linux-style coverage locally to read the new line %:
```bash
dotnet test GitWizardTests/GitWizardTests.csproj -c Release \
    --collect:"XPlat Code Coverage" --results-directory ./TestResults
python3 ci/post-coverage-status.py \
    --cobertura "TestResults/**/coverage.cobertura.xml" --gate-line 0 --summary --skip-post
```
Read the reported line % (now including the whole `GitWizardUI` assembly with its uncovered Views/App/Program). Pick the new threshold = floor(reported %) − 2 (a small buffer against run-to-run jitter). Record the measured % and chosen threshold in the eventual PR description.

- [x] **Step 4: Set the new `--gate-line` in `ci.yml`**

In the `Coverage gate` step, change `--gate-line 33` to the threshold from Step 3, and update the explanatory comment above it to read:

```yaml
      # Gate the build on line coverage and write a line/branch table to the job
      # summary. Re-baselined to the whole GitWizardUI assembly after the VM fold
      # (2026-05-26) — the app's untestable Avalonia glue (Views/App/Program) now
      # sits in coverage scope. Ratchet up as ViewModel coverage grows (issue #36).
```

- [x] **Step 5: Lint and confirm no MAUI/old-name refs remain**

```bash
python3 -c "import yaml; yaml.safe_load(open('.gitea/workflows/ci.yml')); print('ok')"
grep -n -i 'maui\|GitWizardAvalonia\|GitWizardUI.ViewModels' .gitea/workflows/ci.yml || echo "clean"
```
Expected: `ok` then `clean`.

- [x] **Step 6: Commit**

```bash
git add .gitea/workflows/ci.yml
git commit -m "ci: drop MAUI steps, rename project list, re-baseline coverage gate

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 6: Update `release.yml`

**Files:**
- Modify: `.gitea/workflows/release.yml`

- [x] **Step 1: Fix the `pull_request` paths trigger**

Replace the three csproj path lines (10–12):
```yaml
      - 'GitWizardUI/GitWizardUI.csproj'
      - 'GitWizardUI.ViewModels/GitWizardUI.ViewModels.csproj'
      - 'GitWizardAvalonia/GitWizardAvalonia.csproj'
```
with the single line:
```yaml
      - 'GitWizardUI/GitWizardUI.csproj'
```

- [x] **Step 2: Rename the publish step + zip and add the tag-version assertion in `publish-cross`**

Rename the `Publish Avalonia` step to `Publish GUI (GitWizardUI)` and point it at `GitWizardUI/GitWizardUI.csproj` with output `publish/gui/${{ matrix.rid }}`:
```yaml
      - name: Publish GUI (GitWizardUI)
        run: |
          dotnet publish GitWizardUI/GitWizardUI.csproj \
            -c Release \
            -r ${{ matrix.rid }} \
            --self-contained \
            -p:PublishSingleFile=true \
            -p:IncludeNativeLibrariesForSelfExtract=true \
            -o publish/gui/${{ matrix.rid }}
```
In the `Zip artifacts` step, change the Avalonia zip line:
```bash
          (cd publish/avalonia/${{ matrix.rid }} && zip -r "../../../artifacts/GitWizardAvalonia-${VERSION}-${{ matrix.rid }}.zip" .)
```
to:
```bash
          (cd publish/gui/${{ matrix.rid }} && zip -r "../../../artifacts/GitWizardUI-${VERSION}-${{ matrix.rid }}.zip" .)
```
Immediately AFTER the `Resolve version` step (`id: ver`) and BEFORE `Publish CLI`, insert the tag assertion (bash, since this job is ubuntu):
```yaml
      - name: Validate tag matches GitWizardUI <Version> (push only)
        if: github.event_name == 'push'
        run: |
          appVer=$(grep -oP '(?<=<Version>)[^<]+' GitWizardUI/GitWizardUI.csproj)
          tagVer="${{ steps.ver.outputs.version }}"
          if [ "$appVer" != "$tagVer" ]; then
            echo "Tag version '$tagVer' does not match GitWizardUI.csproj <Version> '$appVer'. Bump the csproj or move the tag." >&2
            exit 1
          fi
          echo "Version match: $tagVer"
```

- [x] **Step 3: Delete the entire `publish-maui` job**

Remove the whole `publish-maui:` job (lines 95–169), from `  publish-maui:` through its `Upload MAUI zip` step's `if-no-files-found: error`. (Its old `Validate tag matches ApplicationDisplayVersion` step is replaced by the `publish-cross` assertion added in Step 2.)

- [x] **Step 4: Drop `publish-maui` from the release job's `needs`**

Change `    needs: [publish-cross, publish-maui]` to `    needs: [publish-cross]`.

- [x] **Step 5: Lint and confirm no MAUI/old-name refs remain**

```bash
python3 -c "import yaml; yaml.safe_load(open('.gitea/workflows/release.yml')); print('ok')"
grep -n -i 'maui\|GitWizardAvalonia\|GitWizardUI.ViewModels\|ApplicationDisplayVersion\|publish/avalonia' .gitea/workflows/release.yml || echo "clean"
```
Expected: `ok` then `clean`.

- [x] **Step 6: Commit**

```bash
git add .gitea/workflows/release.yml
git commit -m "ci(release): drop MAUI publish job, ship GitWizardUI zips, anchor tag-assert on <Version>

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 7: Update `screenshot.yml`

**Files:**
- Modify: `.gitea/workflows/screenshot.yml`

- [x] **Step 1: Rewrite the build/run/artifact references**

Apply these edits:
- `Build GitWizardAvalonia` step → name `Build GitWizardUI`; command `dotnet build GitWizardUI/GitWizardUI.csproj -c Debug --no-restore`.
- `Capture screenshot` step: `dotnet restore GitWizardUI.Screenshot/GitWizardUI.Screenshot.csproj` and `dotnet run --project GitWizardUI.Screenshot/GitWizardUI.Screenshot.csproj -c Debug --no-restore`.
- `Upload screenshot artifact` step: `name: GitWizardUI-screenshot`; `path: Screenshots/GitWizardUI.png`.

- [x] **Step 2: Lint and confirm clean**

```bash
python3 -c "import yaml; yaml.safe_load(open('.gitea/workflows/screenshot.yml')); print('ok')"
grep -n -i 'GitWizardAvalonia' .gitea/workflows/screenshot.yml || echo "clean"
```
Expected: `ok` then `clean`.

- [x] **Step 3: Commit**

```bash
git add .gitea/workflows/screenshot.yml
git commit -m "ci(screenshot): point at renamed GitWizardUI + GitWizardUI.Screenshot

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase 6: Documentation

### Task 8: Update CLAUDE.md and other docs

**Files:**
- Modify: `CLAUDE.md`, `README.md`, `HANDOFF.md`, `PLAN.md`
- Delete: `docs/superpowers/plans/2026-05-24-retire-maui.md` (superseded)

- [ ] **Step 1: Rewrite `CLAUDE.md` to the single-GUI reality**

Make these edits:
- **Projects list:** drop the MAUI `GitWizardUI/` and `GitWizardUI.UITests/` bullets and the separate `GitWizardUI.ViewModels/` bullet; describe `GitWizardUI/` as "Avalonia cross-platform desktop app (Windows/macOS/Linux); contains the view models under `ViewModels/`"; rename the `GitWizardAvalonia.Screenshot/` bullet to `GitWizardUI.Screenshot/`; drop `GitWizardUI.UITests` from the test-projects line.
- **Build section:** delete the MAUI build + "Publish MAUI" blocks; reframe the intro so plain `dotnet build` is the norm and the VS2026-MSBuild guidance applies ONLY to MFTLib **local ProjectReference** dev; change the Avalonia build command to `dotnet build GitWizardUI/GitWizardUI.csproj`.
- **Key Architecture:** change `Self-elevation`/`IUpdateHandler` "CLI and MAUI" → "CLI and GitWizardUI"; delete the MAUI `CollectionView` bullet.
- **Release checklist:** step 1 becomes "Update `<Version>` in `GitWizardUI/GitWizardUI.csproj`"; drop `GitWizardUI-{ver}.zip` (MAUI) from the artifacts list (the cross-platform `GitWizardUI-*` zips now cover it).
- **CI infrastructure:** drop the MAUI workload mention and the `publish-maui` reference; update the coverage-gate bullet to note the re-baselined threshold + whole-assembly scope; update the "Deliberate non-goals" MAUI bullet to "no MAUI (retired 2026-05-26; GitWizardUI is the Avalonia app)".
- **Tips:** delete the `taskkill /IM GitWizardUI.exe` Git-Bash tip and the MAUI `CollectionView`/`IsEnabled` tips; keep the Avalonia-specific tips (custom `ICommand`, `StringToThicknessConverter`, scroll-anchor, etc.) — they now describe `GitWizardUI`.

- [ ] **Step 2: Sweep README/HANDOFF/PLAN for stale names**

```bash
grep -n -i 'GitWizardAvalonia\|GitWizardUI\.ViewModels\|maui' README.md HANDOFF.md PLAN.md
```
For each hit, update prose to the new names (Avalonia app = `GitWizardUI`; VMs live in `GitWizardUI/ViewModels/`; MAUI retired). Make the edits.

- [ ] **Step 3: Delete the superseded plan**

```bash
git diff docs/superpowers/plans/2026-05-24-retire-maui.md   # review the pending working-tree edit
```
Phase 1 of that plan is merged (PR #47) and its outcome lives in code + CLAUDE.md; Phase 2 is replaced by this plan. Fold the still-open carry-forward (the `--elevated-mft` routing check) into this plan's Phase 7 verification (Step it references is already present), then remove it:
```bash
git rm -f docs/superpowers/plans/2026-05-24-retire-maui.md
```

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "docs: update CLAUDE.md + README/HANDOFF/PLAN for GitWizardUI rename; drop superseded plan

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase 7: Verify, PR, smoke release, merge

### Task 9: Final verification and landing

**Files:** none (verification + PR + memory).

- [ ] **Step 1: Dangling-reference sweep**

```bash
grep -rn -i 'GitWizardAvalonia\|GitWizardUI\.ViewModels\|\bmaui\b\|ApplicationDisplayVersion' . \
  --include='*.cs' --include='*.axaml' --include='*.csproj' --include='*.slnx' \
  --include='*.yml' --include='*.md' --include='*.manifest' \
  | grep -v 'docs/superpowers/plans/2026-05-26-retire-maui-rename-gitwizardui.md'
```
Expected: no output. Investigate and fix anything that prints.

- [ ] **Step 2: Clean full build + tests**

```bash
dotnet build git-wizard.slnx -c Release
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build
```
Expected: build succeeds; all tests pass.

- [ ] **Step 3: Push and open the PR**

```bash
git push -u gitea retire-maui-rename-ui
```
Title: `refactor: retire MAUI, rename Avalonia → GitWizardUI, fold in ViewModels`
Body: summarize the restructure (5 projects from 7); state the measured post-fold coverage % and the new `--gate-line` (from Task 5 Step 3); note this supersedes the 2026-05-24 plan's Phase 2.

- [ ] **Step 4: Wait for CI green on both runners**

Watch `https://gitea.llamabox.internal/schoen/git-wizard/actions`. Expected: `CI / Build + Test (Linux…)` and `(Windows…)` both pass; the re-baselined coverage gate passes. (Required check context names are unchanged, so branch protection needs no reconfig.)

- [ ] **Step 5: Smoke-test the release workflow**

The PR triggers `release.yml` in draft/dry-run mode (self-cleaning). Confirm on the Actions page: builds CLI + `GitWizardUI` zips for all 3 RIDs, NO `publish-maui` job, the tag-assert step present in `publish-cross`, the draft release + tag deleted by `Smoke cleanup`. Expected assets: `git-wizard-*-{win,linux,osx}-x64.zip` and `GitWizardUI-*-{win,linux,osx}-x64.zip` only.

- [ ] **Step 6: Windows manual check (carry-forward from the old plan)**

On the Windows host, run the published `GitWizardUI` non-elevated and force a fresh discovery (delete `%USERPROFILE%\.GitWizard\repositories.txt`, then Refresh). Expected: a UAC prompt, MFT scan completes in seconds (not a ~120 s fallback hang), no second window — confirming the `--elevated-mft` routing in `Program.cs` is live, not dead code. Then **Check Windows Defender** → UAC → "Defender Exclusions Added" (verify via `Get-MpPreference | Select -Expand ExclusionProcess`). Record results in the PR.

- [ ] **Step 7: Merge**

Merge once CI is green, the smoke release is correct, and Windows verification passes (branch protection requires 1 approval — request it).

- [ ] **Step 8: Update project memory**

Update `project_maui_retirement.md` (and its `MEMORY.md` pointer): MAUI retired 2026-05-26; Avalonia app renamed `GitWizardUI` with VMs folded in; coverage gate re-baselined to `<measured %>`/whole assembly. Note issue #36's ratchet continues against the new baseline.
