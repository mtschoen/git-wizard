# MFTLib Submodule Swap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace git-wizard's vendored prebuilt MFTLib binaries (`lib/MFTLib/`) with the
`external/MFTLib` git submodule built from source on both platforms, unblocking `dotnet publish`
(required by the upcoming `/preview` provider) and dogfooding unpublished MFTLib 0.3.0 the same way
file-wizard does.

**Architecture:** Mirror file-wizard's proven arrangement (`Directory.Build.targets` there): the
native `MFTLibNative` is built by the platform toolchain (MSVC v143 vcxproj on Windows, CMake+Ninja
on Linux — MFTLib's `CMakeLists.txt` has a real POSIX path), the managed `MFTLib.dll` is built by
`dotnet` (a targets rule drops the vcxproj `ProjectReference` that `dotnet` cannot load, MSB4278),
and every git-wizard project consumes MFTLib as a built **assembly** (HintPath `<Reference>`) with
the native library copied beside each output. The old `BlockVendoredMFTLibOnPublish` guard is
deleted — publishing against the source-built submodule is legitimate — which also lets us
re-enable release.yml's PR publish smoke (disabled during the vendored era).

**Tech Stack:** git submodule (pin `f76eee68f7c8ff074698b6cd035d6f3ce49f6980`, tip of
`feat/volume-broker`, verified present on BOTH MFTLib remotes); MSBuild targets; CMake + Ninja
(Linux native); VS MSBuild v143 (Windows native); gitea Actions (`ubuntu-latest` = llamabox
runner, `windows-latest` = the Windows VM runner).

## Global Constraints

- Parent spec: `~/schoen-lab/docs/superpowers/specs/2026-07-10-preview-platform-executors-design.md`
  (PR ① of 5). The spec stays in-tree until PR ⑤ consumes it — do NOT delete it with this plan.
- Submodule pin `f76eee68f7c8ff074698b6cd035d6f3ce49f6980` must exist on both MFTLib remotes
  (verified 2026-07-10 on `gitea/feat/volume-broker` and `origin/feat/volume-broker`).
- Submodule URL must be **relative** (`../MFTLib.git`) so it resolves to
  `github.com/mtschoen/MFTLib` for local clones and `gitea:schoen/MFTLib` on the CI runners
  (file-wizard precedent).
- Both CI legs (`test-linux`, `test-windows`) and `screenshot.yml` must stay green.
- Local Windows builds use VS MSBuild at
  `C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe`.
- Commit style: `<type>: <imperative summary>` + body explaining why.
- Run `aislop ci .` before declaring done (repo gates on it).
- Working branch: **`feat/journal-watch`** (existing WIP — the journal-watch CLI feature is the
  consumer that needs MFTLib 0.3's journal broker, so the swap lands on that branch and ships in
  its PR; user decision 2026-07-10). Its head already re-vendored the dlls from `fd0b750`; the swap
  deletes those and pins the submodule at `f76eee6` (a descendant — includes the broker). git-wizard
  is dual-remote: push the branch to `gitea` (canonical, PRs live there); mirror `main` to github
  only at merge time.

---

## Task 1: Add the MFTLib submodule and remove the vendored binaries

**Files:**
- Create: `.gitmodules`, `external/MFTLib` (submodule at `f76eee68f7c8ff074698b6cd035d6f3ce49f6980`)
- Delete: `lib/MFTLib/MFTLib.dll`, `lib/MFTLib/win-x64/MFTLibNative.dll`, `lib/MFTLib/.gitattributes`
- Keep for now: `lib/MFTLib/README.md` is deleted in Task 2 together with the targets rewrite
  (the targets file references it; delete them atomically).

**Interfaces:**
- Produces: `external/MFTLib/` source tree that Task 2's targets file and Tasks 4–6's CI steps
  build from. Key paths inside the submodule: `MFTLib/MFTLib.csproj` (managed, net8.0, builds to
  `MFTLib/bin/x64/<Config>/net8.0/MFTLib.dll` with `-p:Platform=x64`),
  `MFTLibNative/MFTLibNative.vcxproj` (Windows native), `MFTLibNative/CMakeLists.txt` (POSIX native).

- [ ] **Step 1: Add the submodule (on `feat/journal-watch`)**

```bash
cd ~/git-wizard
git checkout feat/journal-watch
git submodule add ../MFTLib.git external/MFTLib
cd external/MFTLib
git fetch origin feat/volume-broker
git checkout f76eee68f7c8ff074698b6cd035d6f3ce49f6980
cd ../..
```

Expected: `.gitmodules` contains `url = ../MFTLib.git`; `git submodule status` shows
`f76eee68f7c8ff074698b6cd035d6f3ce49f6980 external/MFTLib`.

- [ ] **Step 2: Delete the vendored binaries (not the README yet)**

```bash
git rm lib/MFTLib/MFTLib.dll lib/MFTLib/win-x64/MFTLibNative.dll lib/MFTLib/.gitattributes
```

(Run via the Bash tool — the PowerShell tool false-positives on `git rm` chains.)

- [ ] **Step 3: Verify the build is now BROKEN (red step)**

```bash
dotnet build GitWizard/GitWizard.csproj -c Release 2>&1 | tail -5
```

Expected: FAIL — the HintPath in the (still-old) `Directory.Build.targets` points at the deleted
`lib\MFTLib\MFTLib.dll`, so compilation errors (CS0246 `MFTLib` types not found) confirm nothing
silently falls back to a stale cached dll.

- [ ] **Step 4: Commit**

```bash
git add .gitmodules external/MFTLib
git commit -F - <<'EOF'
build: add MFTLib submodule, drop vendored binaries

external/MFTLib pinned to f76eee6 (feat/volume-broker tip, present on
both MFTLib remotes). Relative URL resolves to github for local clones
and gitea for the CI runners (file-wizard precedent). The vendored
lib/MFTLib dlls are removed; the tree does not build until the
Directory.Build.targets rewrite in the next commit.
EOF
```

(Mid-task red commit is deliberate: Task 2's diff then shows exactly what makes the build green.)

## Task 2: Rewrite `Directory.Build.targets` for source-built MFTLib on both platforms

**Files:**
- Modify: `Directory.Build.targets` (full replacement, content below)
- Delete: `lib/MFTLib/README.md` (directory `lib/` disappears)

**Interfaces:**
- Consumes: submodule paths from Task 1.
- Produces: build contract used by Tasks 3–6 —
  managed dll expected at `external/MFTLib/MFTLib/bin/x64/{Debug|Release}/net8.0/MFTLib.dll`;
  Windows native at `external/MFTLib/**/{Config}/MFTLibNative.dll` (vcxproj output, globbed);
  Linux native at **`external/MFTLib/build-linux/libMFTLibNative.so`** (fixed CMake binary dir —
  every CI step and script must use exactly `-B external/MFTLib/build-linux`).

- [ ] **Step 1: Replace `Directory.Build.targets` with the submodule bridge**

```xml
<Project>

  <!-- ============================================================================
       TEMPORARY - MFTLib 0.3.0 bridge (git submodule at external/MFTLib).

       git-wizard needs MFTLib 0.3.0 (IElevationProvider), which is not yet on NuGet. MFTLib is a
       git submodule pinned at external/MFTLib and built FROM SOURCE on each platform:
         - native: MSVC vcxproj on Windows / CMake into external/MFTLib/build-linux on Linux
         - managed: `dotnet build external/MFTLib/MFTLib/MFTLib.csproj -p:Platform=x64`
       Every git-wizard project consumes MFTLib as a built ASSEMBLY (HintPath <Reference>); a
       ProjectReference would drag the native vcxproj into every consumer's build graph, which
       `dotnet` cannot load (MSB4278). Publishing against the source-built submodule is fine (the
       old vendored-dll publish guard is gone with the vendored dlls).

       RETIRE when MFTLib 0.3.0 ships to NuGet: delete this file and the external/MFTLib
       submodule, then add `<PackageReference Include="MFTLib" Version="0.3.0" />` to
       GitWizard/GitWizard.csproj (it flows transitively and its buildTransitive targets place the
       native library automatically).
       ============================================================================ -->

  <!-- external/MFTLib is inside the repo, so MSBuild imports THIS file when MFTLib itself is
       built. Flag those projects so nothing below applies to MFTLib's own build. -->
  <PropertyGroup>
    <IsMFTLibSubmodule Condition="$(MSBuildProjectFullPath.Contains('external\MFTLib')) Or $(MSBuildProjectFullPath.Contains('external/MFTLib'))">true</IsMFTLibSubmodule>
  </PropertyGroup>

  <!-- Let `dotnet` build the managed MFTLib.csproj: drop its ProjectReference to the native
       MFTLibNative.vcxproj, which the .NET CLI cannot load (MSB4278). The managed dll only
       P/Invokes the native at runtime, where the separately built library is supplied by the
       copy targets below. -->
  <ItemGroup Condition="'$(IsMFTLibSubmodule)' == 'true' And '$(MSBuildProjectExtension)' == '.csproj'">
    <ProjectReference Remove="..\MFTLibNative\MFTLibNative.vcxproj" />
  </ItemGroup>

  <!-- (1) Managed reference - the MFTLib.dll built from the submodule. Prefer the active
       configuration but fall back to whichever is actually built: `dotnet format` loads projects
       in Debug even when CI built only Release; without the fallback it cannot resolve MFTLib's
       types (CS0246) and the lint step fails (file-wizard precedent). -->
  <PropertyGroup Condition="'$(IsMFTLibSubmodule)' != 'true' And '$(MSBuildProjectExtension)' == '.csproj'">
    <_MFTLibBin>$(MSBuildThisFileDirectory)external\MFTLib\MFTLib\bin\x64</_MFTLibBin>
    <_MFTLibDll>$(_MFTLibBin)\$(Configuration)\net8.0\MFTLib.dll</_MFTLibDll>
    <_MFTLibDll Condition="!Exists('$(_MFTLibDll)') And Exists('$(_MFTLibBin)\Release\net8.0\MFTLib.dll')">$(_MFTLibBin)\Release\net8.0\MFTLib.dll</_MFTLibDll>
    <_MFTLibDll Condition="!Exists('$(_MFTLibDll)') And Exists('$(_MFTLibBin)\Debug\net8.0\MFTLib.dll')">$(_MFTLibBin)\Debug\net8.0\MFTLib.dll</_MFTLibDll>
  </PropertyGroup>
  <ItemGroup Condition="'$(IsMFTLibSubmodule)' != 'true' And '$(MSBuildProjectExtension)' == '.csproj'">
    <Reference Include="MFTLib">
      <HintPath>$(_MFTLibDll)</HintPath>
      <Private>true</Private>
    </Reference>
  </ItemGroup>

  <!-- (2a) Windows native lib - copied into each project's output so MFTLib's P/Invoke resolves
       it at runtime. Glob for the vcxproj-built DLL under the submodule, scoped to the active
       Configuration, excluding obj copies. -->
  <Target Name="CopyMFTLibNativeWindows" AfterTargets="Build"
          Condition="'$(IsMFTLibSubmodule)' != 'true' And '$(MSBuildProjectExtension)' == '.csproj' And '$(TargetFramework)' != '' And $([MSBuild]::IsOSPlatform('Windows'))">
    <ItemGroup>
      <_MFTLibNativeDll Include="$(MSBuildThisFileDirectory)external\MFTLib\**\$(Configuration)\MFTLibNative.dll"
                        Exclude="$(MSBuildThisFileDirectory)external\MFTLib\**\obj\**" />
    </ItemGroup>
    <Error Condition="'@(_MFTLibNativeDll)' == ''"
           Text="MFTLibNative.dll ($(Configuration)) not found under external/MFTLib. Build it first with VS MSBuild: msbuild external\MFTLib\MFTLibNative\MFTLibNative.vcxproj -t:Build -p:Configuration=$(Configuration) -p:Platform=x64. Also ensure the submodule is checked out (git submodule update --init)." />
    <Copy SourceFiles="@(_MFTLibNativeDll)" DestinationFolder="$(OutDir)" SkipUnchangedFiles="true" />
  </Target>

  <!-- (2b) Linux native lib - same idea, from the fixed CMake binary dir. Missing .so is a
       WARNING, not an error: the managed-only build (and the whole test suite) runs fine without
       native on Linux, exactly as it did in the vendored era (which shipped no Linux native at
       all). Steps that need the runtime native (publish artifacts, preview) build it first. -->
  <Target Name="CopyMFTLibNativeLinux" AfterTargets="Build"
          Condition="'$(IsMFTLibSubmodule)' != 'true' And '$(MSBuildProjectExtension)' == '.csproj' And '$(TargetFramework)' != '' And $([MSBuild]::IsOSPlatform('Linux'))">
    <ItemGroup>
      <_MFTLibNativeSo Include="$(MSBuildThisFileDirectory)external/MFTLib/build-linux/libMFTLibNative.so" />
    </ItemGroup>
    <Warning Condition="!Exists('$(MSBuildThisFileDirectory)external/MFTLib/build-linux/libMFTLibNative.so')"
             Text="libMFTLibNative.so not found (external/MFTLib/build-linux). Managed-only build proceeds; to build the native: cmake -S external/MFTLib/MFTLibNative -B external/MFTLib/build-linux -G Ninja -DCMAKE_BUILD_TYPE=Release &amp;&amp; cmake --build external/MFTLib/build-linux" />
    <Copy SourceFiles="@(_MFTLibNativeSo)" DestinationFolder="$(OutDir)" SkipUnchangedFiles="true"
          Condition="Exists('$(MSBuildThisFileDirectory)external/MFTLib/build-linux/libMFTLibNative.so')" />
  </Target>

</Project>
```

- [ ] **Step 2: Delete the vendored-era README**

```bash
git rm lib/MFTLib/README.md
```

- [ ] **Step 3: Build MFTLib from source locally (Windows), then git-wizard**

```powershell
& "C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" external/MFTLib/MFTLibNative/MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
dotnet restore git-wizard.slnx
dotnet build git-wizard.slnx -c Release --no-restore
```

Expected: all four commands succeed; `GitWizardUI/bin/Release/net10.0/` contains both
`MFTLib.dll` and `MFTLibNative.dll`.

- [ ] **Step 4: Run the test suite**

```powershell
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build
```

Expected: PASS, same counts as main (no behavior change — same 0.3.0 API the vendored dll had).

- [ ] **Step 5: Verify publish is now unblocked (the point of the exercise)**

```powershell
dotnet publish GitWizardUI/GitWizardUI.csproj -c Release -r win-x64 --self-contained -o publish-smoke
Get-ChildItem publish-smoke/MFTLib*.dll
Remove-Item -Recurse -Force publish-smoke
```

Expected: publish succeeds (previously blocked by `BlockVendoredMFTLibOnPublish`); both
`MFTLib.dll` and `MFTLibNative.dll` are in the output.

- [ ] **Step 6: Commit**

```bash
git add Directory.Build.targets
git commit -F - <<'EOF'
build: source-build MFTLib from the submodule on both platforms

Mirrors file-wizard's bridge: dotnet builds the managed MFTLib.dll
(vcxproj ProjectReference dropped from the dotnet graph), the platform
toolchain builds the native (MSVC vcxproj / CMake into
external/MFTLib/build-linux), and every project consumes MFTLib via
HintPath Reference with the native copied beside the output. The
vendored-dll publish guard is gone: publishing against source-built
MFTLib is legitimate, which the /preview provider work requires.
EOF
```

## Task 3: CI — linux leg builds MFTLib (managed + CMake native)

**Files:**
- Modify: `.gitea/workflows/ci.yml` (job `test-linux`, after "Set up .NET 10 SDK", before "Restore")

**Interfaces:**
- Consumes: Task 2's path contract (`build-linux`, `bin/x64`).
- Produces: green `test-linux` with the submodule; pattern reused by Task 5 (release.yml).

- [ ] **Step 1: Enable submodule checkout**

In `test-linux`, replace the checkout step:

```yaml
      - name: Checkout
        uses: actions/checkout@v4
        with:
          submodules: recursive
```

- [ ] **Step 2: Add MFTLib build steps (before "Restore (cross-platform projects)")**

```yaml
      # MFTLib is a source submodule (external/MFTLib) until 0.3.0 ships to NuGet.
      # Native via CMake (POSIX path is real: platform_posix.cpp), managed via dotnet;
      # Directory.Build.targets consumes both from fixed paths. apt install mirrors
      # MFTLib's own linux CI (pip is cert-blocked on this runner).
      - name: Build MFTLib (native + managed)
        run: |
          apt-get update && apt-get install -y cmake ninja-build g++
          cmake -S external/MFTLib/MFTLibNative -B external/MFTLib/build-linux -G Ninja -DCMAKE_BUILD_TYPE=Release
          cmake --build external/MFTLib/build-linux
          dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
```

Note: the "Format check" step runs after this, so the HintPath fallback finds the Release
`MFTLib.dll` (Task 2's Debug→Release fallback covers `dotnet format`'s Debug-mode load).

- [ ] **Step 3: Push the branch and watch the linux leg**

```bash
git add .gitea/workflows/ci.yml
git commit -m "ci: build MFTLib submodule on the linux leg"
git push -u gitea feat/journal-watch
```

Then open a draft PR on gitea (`schoen/git-wizard`) for `feat/journal-watch` — or reuse its
existing PR if one is already open — and confirm `Build + Test (Linux,
cross-platform projects)` is green. Expected first-run risk: apt not available/permitted in the
job container → switch the install line to match MFTLib's `test.yml` linux job verbatim (read
`MFTLib/.gitea/workflows/test.yml:70-95` for the working incantation).

## Task 4: CI — windows leg + screenshot workflow build MFTLib (MSVC native + managed)

**Files:**
- Modify: `.gitea/workflows/ci.yml` (job `test-windows`), `.gitea/workflows/screenshot.yml`

**Interfaces:**
- Consumes: Task 2's path contract.
- Produces: green `test-windows` + `screenshot` with the submodule.

- [ ] **Step 1: `test-windows` — submodule checkout + MSBuild setup + MFTLib build**

Replace the checkout step and add, after "Set up .NET 10 SDK":

```yaml
      - name: Checkout
        uses: actions/checkout@v4
        with:
          submodules: recursive

      # VS MSBuild is the only toolchain that can compile MFTLib's native C++ vcxproj
      # (file-wizard CI precedent on this runner).
      - name: Setup MSBuild
        uses: microsoft/setup-msbuild@v2
        with:
          msbuild-architecture: x64

      - name: Build MFTLib (native + managed)
        shell: pwsh
        run: |
          msbuild external\MFTLib\MFTLibNative\MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
          dotnet build external\MFTLib\MFTLib\MFTLib.csproj -c Release -p:Platform=x64
```

- [ ] **Step 2: `screenshot.yml` — same three steps**

The screenshot job builds `GitWizardUI` in **Debug**, so build MFTLib in Debug there
(the HintPath prefers the active configuration):

```yaml
      - name: Checkout
        uses: actions/checkout@v4
        with:
          submodules: recursive

      - name: Setup MSBuild
        uses: microsoft/setup-msbuild@v2
        with:
          msbuild-architecture: x64

      - name: Build MFTLib (native + managed)
        shell: pwsh
        run: |
          msbuild external\MFTLib\MFTLibNative\MFTLibNative.vcxproj -t:Build -p:Configuration=Debug -p:Platform=x64 -nologo -v:minimal
          dotnet build external\MFTLib\MFTLib\MFTLib.csproj -c Debug -p:Platform=x64
```

(Adjust placement to match the file's existing step order: checkout replaces the current checkout;
the two build steps go before the existing `dotnet build GitWizardUI/GitWizardUI.csproj` step.)

- [ ] **Step 3: `aislop.yml` — submodule checkout + managed MFTLib only**

The aislop job runs on **ubuntu-latest** and only loads the solution for jb/roslynator analysis,
so the managed `MFTLib.dll` must exist for the HintPath to resolve — but no native build is
needed (the Linux copy target merely warns when the `.so` is absent). Replace its checkout step
and add one build step immediately before the existing "Restore git-wizard" step:

```yaml
      - uses: actions/checkout@v4
        with:
          submodules: recursive
```

```yaml
      # MFTLib types must resolve for jb/roslynator to load the solution; managed-only
      # is enough for analysis (no native build here — the copy target only warns).
      - name: Build MFTLib managed (HintPath for analysis)
        run: dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
```

- [ ] **Step 4: Commit, push, verify all workflows green**

```bash
git add .gitea/workflows/
git commit -m "ci: build MFTLib submodule on windows, screenshot, and aislop legs"
git push gitea feat/journal-watch
```

Expected: all CI contexts on the draft PR green. The windows leg proves MSVC native + managed
build on the VM runner — the exact recipe PR ④'s `preview.yml` will reuse.

## Task 5: Re-enable release publish smoke (per-platform native)

**Files:**
- Modify: `.gitea/workflows/release.yml`

**Interfaces:**
- Consumes: Task 2's targets, Task 3/4's CI build snippets.
- Produces: `publish-cross` job that works with the submodule; PR publish-smoke re-enabled.

- [ ] **Step 1: Re-enable the PR publish smoke trigger**

In the `on:` block, delete the "TEMPORARILY DISABLED during the vendored-MFTLib bridge era"
comment (lines 6–10) and uncomment the `pull_request:` trigger, so `on:` reads:

```yaml
on:
  push:
    tags: ['v*']
  # PR publish-smoke: publishes all RIDs and creates+deletes a draft smoke release,
  # scoped to files that affect publishing.
  pull_request:
    paths:
      - '.gitea/workflows/release.yml'
      - 'GitWizard/GitWizard.csproj'
      - 'GitWizardUI/GitWizardUI.csproj'
      - 'git-wizard/git-wizard.csproj'
      - 'Directory.Build.targets'
      - '.gitmodules'
  workflow_dispatch:
    inputs:
      version:
        description: 'Version (without leading v) - used for filenames when run manually'
        required: true
        default: '0.0.0-test'
```

(`Directory.Build.targets` and `.gitmodules` are added to the paths — they now determine what a
publish contains — which also makes the smoke fire on THIS PR.)

- [ ] **Step 2: Split `publish-cross` by native availability**

Rename the job to `publish-posix`, drop `win-x64` from its matrix, and add the MFTLib build. The
job keeps: checkout (now with submodules), .NET setup, NuGet cache, "Resolve version",
"Validate tag matches GitWizardUI <Version> (push only)", both publish steps, "Zip artifacts",
and "Upload artifacts" — all unchanged except as noted:

```yaml
  publish-posix:
    name: Publish CLI + Avalonia (linux + osx)
    runs-on: ubuntu-latest
    timeout-minutes: 30

    strategy:
      fail-fast: false
      matrix:
        rid: [linux-x64, osx-x64]
```

Checkout step gains `with: submodules: recursive`. After "Cache NuGet packages", insert:

```yaml
      # osx-x64 ships managed-only (no mac runner to build the native; the vendored era
      # shipped no osx native either). linux-x64 gets the CMake-built .so via the
      # CopyMFTLibNativeLinux target. The stray .so a linux host copies into the osx
      # publish is inert junk, accepted (the vendored era did the same with the win dll).
      - name: Build MFTLib (native + managed)
        run: |
          apt-get update && apt-get install -y cmake ninja-build g++ zip
          cmake -S external/MFTLib/MFTLibNative -B external/MFTLib/build-linux -G Ninja -DCMAKE_BUILD_TYPE=Release
          cmake --build external/MFTLib/build-linux
          dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
```

The "Upload artifacts" step's name becomes `posix-${{ matrix.rid }}` (was `cross-${{ matrix.rid }}`).

- [ ] **Step 3: Add the `publish-windows` job**

New job after `publish-posix`. The version-resolve is rewritten in **pwsh** (the VM runner's
default shell; don't assume bash exists there). Publish flags and zip names must stay
byte-identical to the posix job (`git-wizard-${VERSION}-win-x64.zip`,
`GitWizardUI-${VERSION}-win-x64.zip`):

```yaml
  publish-windows:
    name: Publish CLI + Avalonia (win-x64, native MFTLib)
    runs-on: windows-latest
    timeout-minutes: 30

    steps:
      - name: Checkout
        uses: actions/checkout@v4
        with:
          submodules: recursive

      - name: Set up .NET 10 SDK
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x

      - name: Setup MSBuild
        uses: microsoft/setup-msbuild@v2
        with:
          msbuild-architecture: x64

      - name: Build MFTLib (native + managed)
        shell: pwsh
        run: |
          msbuild external\MFTLib\MFTLibNative\MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
          dotnet build external\MFTLib\MFTLib\MFTLib.csproj -c Release -p:Platform=x64

      - name: Resolve version
        id: ver
        shell: pwsh
        run: |
          switch ('${{ github.event_name }}') {
            'push'         { $version = $env:GITHUB_REF_NAME -replace '^v', '' }
            'pull_request' { $version = "0.0.0-pr-$($env:GITHUB_SHA.Substring(0,7))" }
            default        { $version = '${{ inputs.version }}' }
          }
          "version=$version" | Out-File -Append $env:GITHUB_OUTPUT
          Write-Host "Resolved version: $version"

      - name: Publish CLI
        shell: pwsh
        run: |
          dotnet publish git-wizard/git-wizard.csproj `
            -c Release `
            -r win-x64 `
            --self-contained `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -o publish/cli/win-x64

      - name: Publish GUI (GitWizardUI)
        shell: pwsh
        run: |
          dotnet publish GitWizardUI/GitWizardUI.csproj `
            -c Release `
            -r win-x64 `
            --self-contained `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -o publish/gui/win-x64

      - name: Zip artifacts
        shell: pwsh
        run: |
          $version = '${{ steps.ver.outputs.version }}'
          New-Item -ItemType Directory -Force artifacts | Out-Null
          Compress-Archive -Path publish/cli/win-x64/* -DestinationPath "artifacts/git-wizard-$version-win-x64.zip"
          Compress-Archive -Path publish/gui/win-x64/* -DestinationPath "artifacts/GitWizardUI-$version-win-x64.zip"
          Get-ChildItem artifacts

      - name: Upload artifacts
        uses: actions/upload-artifact@v3
        with:
          name: windows-win-x64
          path: artifacts/*.zip
          if-no-files-found: error
```

- [ ] **Step 4: Point the release job at both publish jobs**

In the `release` job, change only:

```yaml
    needs: [publish-posix, publish-windows]
```

("Download all artifacts" has no `name:` filter, so it already collects both jobs' uploads;
"Stage assets" copies every `*.zip` — no other change needed.)

- [ ] **Step 5: Commit, push, verify the publish smoke runs green on the PR**

```bash
git add .gitea/workflows/release.yml
git commit -F - <<'EOF'
ci: re-enable release publish smoke, split publish by platform

The vendored-dll publish block is gone (MFTLib now source-built), so
the PR publish smoke comes back. win-x64 publishes move to the windows
runner where MSVC builds the native; linux-x64 stays on ubuntu with
the CMake native.
EOF
git push gitea feat/journal-watch
```

Expected: the publish smoke context appears on the PR and is green for both platforms.

## Task 6: Documentation + final gates

**Files:**
- Modify: `AGENTS.md` (git-wizard's — build instructions), `README.md` (if it mentions building),
  `CLAUDE.md`/`GEMINI.md` only if they duplicate build steps.

- [ ] **Step 1: Update build docs**

Rewrite the build section of git-wizard's `AGENTS.md` to describe the submodule arrangement.
Content requirements (adapt to the file's existing voice; file-wizard's AGENTS.md "Build" section
is the model):

- `git submodule update --init` is now required after clone.
- Windows: VS MSBuild builds `MFTLibNative.vcxproj`, `dotnet` builds everything managed (exact
  commands from Task 2 Step 3).
- Linux: CMake into `external/MFTLib/build-linux` (exact commands from Task 2's warning text);
  managed-only builds work without it.
- The retire-to-NuGet note (moved from the deleted `lib/MFTLib/README.md` into the targets header
  — reference it, don't duplicate).
- Remove every mention of the vendored `lib/MFTLib/` bridge and `BlockVendoredMFTLibOnPublish`.

- [ ] **Step 2: docs-update sweep**

Search for stragglers and fix any hit that describes the vendored era:

```bash
grep -rn -i "vendored\|lib/MFTLib\|BlockVendored" --include='*.md' --include='*.csproj' . | grep -v external/MFTLib | grep -v docs/superpowers/plans
```

Expected after fixes: no hits outside the submodule and this plan.

- [ ] **Step 3: aislop gate**

```bash
aislop ci .
```

Expected: score >= the repo's gate, zero error-severity findings.

- [ ] **Step 4: Commit docs, push, finish the branch**

```bash
git add AGENTS.md README.md
git commit -m "docs: describe the MFTLib submodule build"
git push gitea feat/journal-watch
```

Then mark the PR ready on gitea. Merge = user's call (or pr-crew). At branch-finish, delete this
plan file per plan lifecycle; durable build knowledge lives in AGENTS.md + the targets header.
