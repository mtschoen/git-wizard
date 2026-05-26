# Design: Retire MAUI, rename Avalonia → GitWizardUI, fold in the ViewModels

**Date:** 2026-05-26
**Status:** Approved (design); pending implementation plan.

## Goal

Make the Avalonia app the sole desktop GUI, **named `GitWizardUI`** (taking
over the retired MAUI project's name), with the shared view-model code folded
**into** it so there is no separate `GitWizardUI.ViewModels` project. End state:
five projects instead of seven.

## Relationship to the prior plan

This **supersedes Phase 2** of `docs/superpowers/plans/2026-05-24-retire-maui.md`.
Phase 1 of that plan (Avalonia Windows parity — MFT self-elevation + Defender
exclusions) already merged in PR #47. Phase 2 (a plain MAUI deletion that *kept*
`GitWizardAvalonia`'s name and the separate `GitWizardUI.ViewModels` project) was
never started, so it is replaced wholesale by this design.

## Approaches considered

- **Literal fold (chosen):** the VMs become a `ViewModels/` folder inside the
  renamed GUI executable project. One GUI project, exactly as requested.
- **Library + launcher split (rejected):** split `GitWizardUI` into a class
  library (VMs + Views + App) plus a thin launcher exe so the test project
  references a library rather than an exe. Rejected — it *adds* a project (the
  opposite of the goal) and buys nothing: the Avalonia app already builds
  cross-platform, and a test project can reference an exe project fine.

## Current reference graph (verified)

```
git-wizard (CLI, Exe) ──► GitWizard (core)
GitWizard (core lib)  ──► MFTLib (NuGet)            [leaf]
GitWizardUI.ViewModels (lib) ──► GitWizard
GitWizardAvalonia (WinExe) ──► GitWizard, GitWizardUI.ViewModels
GitWizardAvalonia.Screenshot (Exe) ──► GitWizardAvalonia
GitWizardTests ──► GitWizard, GitWizardUI.ViewModels
GitWizardUI (MAUI)  ──► GitWizardUI.ViewModels      [to be deleted]
GitWizardUI.UITests ──► …                           [to be deleted]
```

Two facts shape the design: the **CLI does not reference the VMs** (no
conflict), and **`GitWizardTests` references the VMs** (so after the fold it
must reference the renamed GUI project).

## Target reference graph

```
git-wizard (CLI, Exe) ──► GitWizard (core)
GitWizard (core lib)  ──► MFTLib (NuGet)            [leaf]
GitWizardUI (WinExe; contains ViewModels/) ──► GitWizard
GitWizardUI.Screenshot (Exe) ──► GitWizardUI
GitWizardTests ──► GitWizard, GitWizardUI
```

## Restructure

| Action | Project |
|---|---|
| **Delete** | `GitWizardUI/` (MAUI) and `GitWizardUI.UITests/` |
| **Rename** | `GitWizardAvalonia/` → `GitWizardUI/` (folder, `.csproj`, `AssemblyName`, `RootNamespace`) |
| **Fold in** | move `GitWizardUI.ViewModels/*.cs` → `GitWizardUI/ViewModels/`; **delete** the `GitWizardUI.ViewModels` project |
| **Rename** | `GitWizardAvalonia.Screenshot/` → `GitWizardUI.Screenshot/` |
| Unchanged | `GitWizard/` (core), `git-wizard/` (CLI) |

## Namespaces (no collisions)

- App code `namespace GitWizardAvalonia.*` → `GitWizardUI.*` (Program, App,
  Views, Services, Converters).
- VM code **keeps** `namespace GitWizardUI.ViewModels` — it nests naturally
  under the new `GitWizardUI` root namespace, so the VM source files' namespace
  declarations do **not** change; only their project home does.
- The deleted MAUI app also used `namespace GitWizardUI`; no conflict because it
  is removed first.
- Mechanical edits: XAML `x:Class` and `xmlns:clr-namespace` from
  `GitWizardAvalonia` → `GitWizardUI`; `using GitWizardAvalonia…` → `GitWizardUI`
  across `.cs`; the Screenshot project's references and namespace.

## Tests + coverage

- `GitWizardTests` drops the `GitWizardUI.ViewModels` project reference and
  references the new `GitWizardUI` project. It transitively pulls in Avalonia,
  which is harmless: the VM tests use stub services (`StubUiDispatcher`,
  `StubFolderPicker`, …) and never start the Avalonia app lifetime.
- **Coverage gate re-baselined to the whole `GitWizardUI` assembly** (user
  decision). The untestable Avalonia glue (`Views/*.axaml.cs`, `App.axaml.cs`,
  `Program.cs`) now sits in coverage scope and will drag the line % below the
  current ~52%. Implementation measures the post-fold line coverage and sets
  `ci/post-coverage-status.py --gate-line` just beneath the new baseline; the
  exact number is reported in the PR. (Issue #36's ratchet intent continues
  against this new baseline.)

## CI / release / docs / version

- **`ci.yml`:** remove the MAUI workload + runtime-pack steps; update the
  coverage `--gate-line` to the re-baselined value. Required check **context
  names are unchanged** (`CI / Build + Test (Linux, cross-platform projects)`
  and `… (Windows, full solution)`), so branch protection needs no reconfig.
- **`release.yml`:** delete the `publish-maui` job and drop it from the release
  job's `needs`; the cross-platform publish job's zip artifacts rename
  `GitWizardAvalonia-*` → `GitWizardUI-*`; move the push-time tag-version
  assertion onto `GitWizardUI.csproj/<Version>`.
- **`CLAUDE.md`:** rewrite the projects list, build commands, Key Architecture
  notes, release checklist, CI-infrastructure section, and the Avalonia-specific
  Tips to describe the single-GUI (`GitWizardUI` = Avalonia) reality.
- **Version anchor:** `<Version>` lives in `GitWizardUI.csproj` (carry 0.4.1
  forward from the deleted MAUI `ApplicationDisplayVersion`).

## Sequencing

**One PR.** The rename touches the same CI/release/docs files the MAUI deletion
touches, so splitting would create churn and merge-order coupling for no benefit.
Phase 1 already de-risked the Windows feature gap, so this PR is mechanical.

## Verification

- `dotnet build git-wizard.slnx -c Release` succeeds with 0 errors.
- `GitWizardTests` fully green on **both** runners (Linux + Windows).
- Coverage job posts the re-baselined gate and passes.
- Dangling-reference sweep: no `GitWizardAvalonia`, `GitWizardUI.ViewModels`,
  `maui`, or `ApplicationDisplayVersion` tokens remain (outside this spec / the
  superseded plan).
- Release-workflow smoke run (PR-triggered, self-cleaning) produces
  `git-wizard-*` and `GitWizardUI-*` zips for win/linux/osx-x64, no
  `publish-maui` job, tag-assert present, draft release + tag cleaned up.

## Out of scope

Binary signing; macOS/Android GUI builds; any production behavior change. This
is a structural rename + fold + deletion only — no feature work.
