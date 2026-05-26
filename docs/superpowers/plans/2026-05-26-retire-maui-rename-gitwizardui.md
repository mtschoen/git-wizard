# Retire MAUI, rename Avalonia → GitWizardUI, fold in the ViewModels — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Avalonia app the sole desktop GUI, renamed `GitWizardUI`, with the `GitWizardUI.ViewModels` project folded into it (no separate VM project). End state: five projects (`git-wizard`, `GitWizard`, `GitWizardUI`, `GitWizardUI.Screenshot`, `GitWizardTests`) instead of seven.

**Architecture:** A pure structural refactor — no production behavior change. Delete the MAUI `GitWizardUI` + `GitWizardUI.UITests`; rename `GitWizardAvalonia/` → `GitWizardUI/` (folder, csproj, `RootNamespace`); move `GitWizardUI.ViewModels/*.cs` into `GitWizardUI/ViewModels/` keeping their existing `namespace GitWizardUI.ViewModels` (nests cleanly under the new root, so VM source is untouched); rename `GitWizardAvalonia.Screenshot` → `GitWizardUI.Screenshot`. `GitWizardTests` references the new `GitWizardUI` project. The coverage gate is **re-baselined to the whole `GitWizardUI` assembly** (the app's untestable Avalonia glue now sits in scope and lowers the line %). Done as one PR; each phase leaves `dotnet build git-wizard.slnx` green. Supersedes Phase 2 of `2026-05-24-retire-maui.md` (Phase 1 — Avalonia Windows parity — already merged in PR #47).

**Tech Stack:** C# / .NET 10, Avalonia 11.2, NUnit 4, coverlet, Gitea Actions (`ci.yml`, `release.yml`, `screenshot.yml`), `ci/post-coverage-status.py`.

---

## Phase 6: Documentation

### Task 8: Update CLAUDE.md and other docs

**Files:**
- Modify: `CLAUDE.md`, `README.md`, `HANDOFF.md`, `PLAN.md`
- Delete: `docs/superpowers/plans/2026-05-24-retire-maui.md` (superseded)

- [x] **Step 1: Rewrite `CLAUDE.md` to the single-GUI reality**

Make these edits:
- **Projects list:** drop the MAUI `GitWizardUI/` and `GitWizardUI.UITests/` bullets and the separate `GitWizardUI.ViewModels/` bullet; describe `GitWizardUI/` as "Avalonia cross-platform desktop app (Windows/macOS/Linux); contains the view models under `ViewModels/`"; rename the `GitWizardAvalonia.Screenshot/` bullet to `GitWizardUI.Screenshot/`; drop `GitWizardUI.UITests` from the test-projects line.
- **Build section:** delete the MAUI build + "Publish MAUI" blocks; reframe the intro so plain `dotnet build` is the norm and the VS2026-MSBuild guidance applies ONLY to MFTLib **local ProjectReference** dev; change the Avalonia build command to `dotnet build GitWizardUI/GitWizardUI.csproj`.
- **Key Architecture:** change `Self-elevation`/`IUpdateHandler` "CLI and MAUI" → "CLI and GitWizardUI"; delete the MAUI `CollectionView` bullet.
- **Release checklist:** step 1 becomes "Update `<Version>` in `GitWizardUI/GitWizardUI.csproj`"; drop `GitWizardUI-{ver}.zip` (MAUI) from the artifacts list (the cross-platform `GitWizardUI-*` zips now cover it).
- **CI infrastructure:** drop the MAUI workload mention and the `publish-maui` reference; update the coverage-gate bullet to note the re-baselined threshold + whole-assembly scope; update the "Deliberate non-goals" MAUI bullet to "no MAUI (retired 2026-05-26; GitWizardUI is the Avalonia app)".
- **Tips:** delete the `taskkill /IM GitWizardUI.exe` Git-Bash tip and the MAUI `CollectionView`/`IsEnabled` tips; keep the Avalonia-specific tips (custom `ICommand`, `StringToThicknessConverter`, scroll-anchor, etc.) — they now describe `GitWizardUI`.

- [x] **Step 2: Sweep README/HANDOFF/PLAN for stale names**

```bash
grep -n -i 'GitWizardAvalonia\|GitWizardUI\.ViewModels\|maui' README.md HANDOFF.md PLAN.md
```
For each hit, update prose to the new names (Avalonia app = `GitWizardUI`; VMs live in `GitWizardUI/ViewModels/`; MAUI retired). Make the edits.

- [x] **Step 3: Delete the superseded plan**

```bash
git diff docs/superpowers/plans/2026-05-24-retire-maui.md   # review the pending working-tree edit
```
Phase 1 of that plan is merged (PR #47) and its outcome lives in code + CLAUDE.md; Phase 2 is replaced by this plan. Fold the still-open carry-forward (the `--elevated-mft` routing check) into this plan's Phase 7 verification (Step it references is already present), then remove it:
```bash
git rm -f docs/superpowers/plans/2026-05-24-retire-maui.md
```

- [x] **Step 4: Commit**

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
