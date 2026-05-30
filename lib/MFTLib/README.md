# Vendored MFTLib (TEMPORARY — CI unblock)

These are **prebuilt MFTLib `0.3.0` binaries**, checked in to let CI build the solution
with a plain `dotnet build` while MFTLib `0.3.0` is still **unpublished**.

## Why this exists

git-wizard depends on MFTLib `0.3.0` (the `IElevationProvider` elevation API). That version
is not on NuGet yet, so the repo had been carrying a local **`ProjectReference`** to a sibling
`..\..\MFTLib` checkout. The CI runners don't have that sibling checked out, and `dotnet`
can't build MFTLib's native `MFTLibNative.vcxproj` (needs the VS v145 toolset) — so CI was
dormant ("red by design"). Vendoring the prebuilt DLLs removes both blockers: `dotnet build`
just references a checked-in assembly and copies a checked-in native DLL, no MFTLib source or
native toolchain required.

## Contents

| File | What | Built from |
|---|---|---|
| `MFTLib.dll` | managed assembly (`net8.0`, AnyCPU IL), referenced via HintPath | MFTLib `c329fb9` (branch `feat/lint-rollout`), `Release` |
| `win-x64/MFTLibNative.dll` | Windows x64 native lib, copied to each project's output on Windows | same, `x64/Release` |

Built 2026-05-29 with VS2026 MSBuild (`-p:Configuration=Release -p:Platform=x64`).
No Linux `libMFTLibNative.so` is vendored: git-wizard only invokes MFT on Windows (the Linux CI
job builds against the managed assembly and skips MFT tests), so the `.so` isn't needed here.

## Wiring (see the build files, not just this note)

- **`GitWizard/GitWizard.csproj`** + **`GitWizardTests/GitWizardTests.csproj`** — `<Reference Include="MFTLib">`
  with `<HintPath>` pointing here (the two projects that compile against MFTLib types).
- **`Directory.Build.targets`** (repo root) — copies `win-x64/MFTLibNative.dll` into **every**
  project's output on Windows (a project-local `<None>` copy doesn't flow to dependents, so the
  native copy lives in the shared targets).

## How to retire this (do this when MFTLib 0.3.0 ships to NuGet)

1. Add `<PackageReference Include="MFTLib" Version="0.3.0" />` to `GitWizard/GitWizard.csproj`
   (it flows transitively to the CLI/UI/tests, restoring the original clean design).
2. Delete the repo-root `Directory.Build.targets` (the managed `<Reference>`, the native-DLL
   copy, and the `BlockVendoredMFTLibOnPublish` guard) — the NuGet package's `buildTransitive`
   targets then place the native DLL automatically.
3. Delete this `lib/MFTLib/` directory.
4. In `.gitea/workflows/release.yml`, **un-comment the `pull_request:` trigger** (it was disabled
   for the vendored era — the publish guard + the Ubuntu runner's inability to bundle the Windows
   native lib made the PR publish-smoke unrunnable).
5. Re-confirm CI is green on the package and re-enable any required status checks.

NOTE: the `.gitattributes` force-CRLF block (`*.cs … text eol=crlf`) is **NOT** part of this
retirement — it's a permanent fix for the canonical `.editorconfig` (`end_of_line = crlf`) vs
Linux LF-checkout conflict, independent of MFTLib. Leave it.

Tracked in `PLAN.md` → Infrastructure and `CLAUDE.md` → MFTLib Local Development.
