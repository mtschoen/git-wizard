# git-wizard `/preview` provider Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make git-wizard `/preview`-able on both platforms in one PR — a Windows Actions executor (`preview.yml`) and a Linux local executor (`.preview/up`/`down`) that build the Avalonia GUI, publish it to the gitea generic package registry, and (on Linux, when a display is live) launch it on llamabox's hyprland display, plus `run-preview` scripts that fetch and launch a published artifact.

**Architecture:** This is spec ④ of the preview-platform-executors design. The pr_crew orchestrator machinery already merged (PR #310); this PR ships only the *target-repo* artifacts it drives. Two executors write the same artifact convention to one registry package (version = head SHA, one file per platform): the Windows executor is a `workflow_dispatch` Gitea Actions workflow; the Linux executor is a `.preview/up` script pr_crew runs directly on llamabox in a detached PR-head worktree. The Linux script's output is `kind: "app"` (launched on the display, PID-file liveness) when a Wayland session exists, else `kind: "artifact"`. No slot port and no URL probe on either — this is an app/artifact provider, not the site-kind schoen-lab reference.

**Tech Stack:** Gitea Actions (act_runner, `windows-latest`), .NET 10 SDK, MSVC vcxproj + CMake/Ninja native builds (MFTLib submodule), Avalonia self-contained publish, POSIX `sh`, PowerShell 7 (`pwsh`), gitea generic package registry REST.

## Global Constraints

- **.NET:** `10.0.x` SDK (`actions/setup-dotnet@v4`); GitWizardUI targets `net10.0`.
- **Native build is prerequisite to publish:** `Directory.Build.targets` supplies `MFTLibNative.dll` / `libMFTLibNative.so` to the publish output only *after* the native lib is built — always build native first (MSVC vcxproj on Windows, CMake/Ninja on Linux), same two-step as `ci.yml`.
- **Publish shape:** plain self-contained *folder* publish — **NO** `-p:PublishSingleFile=true` / `-p:IncludeNativeLibrariesForSelfExtract=true`. `run-preview` launches the exe from the unzipped folder.
- **Artifact filename convention (fixed):** `GitWizardUI-<platform>-x64.zip`, `platform ∈ {windows, linux}`. Version lives in the registry path, never in the filename.
- **Registry path (exact):** `https://gitea.llamabox.sticktoitive.net/api/packages/schoen/generic/<preview_id>/<head_sha>/<filename>` via `PUT`. `preview_id = git-wizard-pr-<N>`; version = **full** head SHA. The registry refuses a duplicate name+version+file — guard every upload with an existence probe so re-dispatch stays green.
- **Auth:** Windows workflow uses repo secret `CI_GITEA_TOKEN` (same secret `release.yml` uses); Linux `.preview/up` uses env `PREVIEW_GITEA_TOKEN` (supplied by pr_crew).
- **Registry TLS — plain calls, no bypass:** use plain `curl -fsSL` (bash) and plain `Invoke-WebRequest` / `Invoke-RestMethod` (pwsh) for every registry call. The gitea host's cert is trusted by the system CA store on the fleet hosts and CI runners — `release.yml` curls this exact host with no `-k` (lines 263-301) and is green. Do **NOT** add `-k` / `--insecure` / `-SkipCertificateCheck`; a validation bypass would (rightly) be flagged in review. (The one known cert gap is Node-only — `actions/upload-artifact`'s bundled CA store, per the ci.yml note — and does not affect curl or PowerShell, which use the system trust store.)
- **Provider stdout contract:** `.preview/up` prints **exactly one** JSON line `{"kind", "url"?, "note"?}` as the **LAST** stdout line; all diagnostics go to stderr; nonzero exit = failure (stderr tail lands in the PR comment). Daemonized children MUST detach stdio (`</dev/null >log 2>&1 &`) or the orchestrator's `run_captured` hangs.
- **`workflow_dispatch` inputs (exact names, all strings):** `preview_id`, `head_sha`, `pr_number` — pr_crew's dispatch body sends these three.
- **Total workflow runtime must stay well under 30 min** (the orchestrator's `ACTIONS_POLL_TIMEOUT_SECONDS`).
- **CI gates that must stay green:** `ci.yml` both legs, `aislop.yml` (failBelow 100, covers any Python/C#), `jb inspectcode` (windows leg), `dotnet format --verify-no-changes` (linux leg). None of the new files are C#, but the branch's final task confirms every leg green.

---

## Task 1: Windows Actions executor — `.gitea/workflows/preview.yml`

The Windows preview executor. pr_crew dispatches it via `workflow_dispatch`; it checks out the dispatched ref (the PR head, checked out by default), builds the native lib with MSVC, publishes the self-contained GUI, zips it, and uploads to the registry with an existence-guarded PUT.

**Files:**
- Create: `.gitea/workflows/preview.yml`

**Interfaces:**
- Consumes (from pr_crew's actions executor): `workflow_dispatch` with string inputs `preview_id`, `head_sha`, `pr_number`; repo secret `CI_GITEA_TOKEN`.
- Produces (for the orchestrator's deterministic success comment): the zip at `…/generic/<preview_id>/<head_sha>/GitWizardUI-windows-x64.zip`.

**Failure mode:** If the Windows runner's registry upload fails TLS validation (there is no direct-to-gitea HTTPS precedent on the Windows legs — `release.yml`'s Windows job uploads via `actions/upload-artifact`, and its curl-to-gitea `release` job runs on ubuntu; the cert is a mkcert-CA root per `ci.yml:97-100`), the fix is installing the mkcert root into that runner's certificate store — **never** `-SkipCertificateCheck` / `-k`.

- [ ] **Step 1: Write the workflow file**

Create `.gitea/workflows/preview.yml` with exactly this content:

```yaml
name: Preview

# PR preview build (spec 2026-07-10-preview-platform-executors-design.md §4).
# Dispatched by pr_crew's actions executor; builds the self-contained Windows
# GUI and uploads it to the gitea generic package registry. The orchestrator
# constructs the download URL deterministically from the fixed filename.
on:
  workflow_dispatch:
    inputs:
      preview_id:
        description: 'Registry package name (e.g. git-wizard-pr-42)'
        required: true
      head_sha:
        description: 'PR head commit SHA (registry package version)'
        required: true
      pr_number:
        description: 'PR number'
        required: true

jobs:
  build-windows:
    name: Build + publish Windows preview artifact
    runs-on: windows-latest
    timeout-minutes: 25

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

      # VS MSBuild is the only toolchain that can compile MFTLib's native vcxproj.
      # Directory.Build.targets then registers the built DLL for publish.
      - name: Build MFTLib native (MSVC)
        shell: pwsh
        run: msbuild external\MFTLib\MFTLibNative\MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal

      # Plain self-contained folder publish (no single-file flags) so run-preview
      # can launch GitWizardUI.exe straight from the unzipped folder.
      - name: Publish GitWizardUI (win-x64, self-contained)
        shell: pwsh
        run: dotnet publish GitWizardUI/GitWizardUI.csproj -c Release -r win-x64 --self-contained -o publish/gui/win-x64

      - name: Zip artifact
        shell: pwsh
        run: Compress-Archive -Path publish/gui/win-x64/* -DestinationPath GitWizardUI-windows-x64.zip -Force

      # Existence-guarded upload: a re-dispatch for the same SHA finds the file
      # already present (HEAD 2xx) and skips the PUT — the registry refuses a
      # duplicate name+version+file with 409, so re-dispatch must stay green.
      - name: Upload to gitea generic registry (idempotent)
        shell: pwsh
        env:
          CI_GITEA_TOKEN: ${{ secrets.CI_GITEA_TOKEN }}
        run: |
          $url = "https://gitea.llamabox.sticktoitive.net/api/packages/schoen/generic/${{ inputs.preview_id }}/${{ inputs.head_sha }}/GitWizardUI-windows-x64.zip"
          $headers = @{ Authorization = "token $env:CI_GITEA_TOKEN" }
          try {
            Invoke-WebRequest -Method Head -Uri $url -Headers $headers -ErrorAction Stop | Out-Null
            Write-Host "Artifact already present at $url - skipping upload."
            exit 0
          } catch {
            $code = $_.Exception.Response.StatusCode.value__
            if ($code -and $code -ne 404) { throw }
          }
          Write-Host "Uploading $url"
          Invoke-WebRequest -Method Put -Uri $url -Headers $headers -InFile GitWizardUI-windows-x64.zip -ContentType 'application/octet-stream' -ErrorAction Stop | Out-Null
          Write-Host "Upload complete."
```

- [ ] **Step 2: YAML sanity check**

Run: `python -c "import yaml; yaml.safe_load(open('.gitea/workflows/preview.yml')); print('ok')"`
Expected: `ok` (no traceback). Note: YAML 1.1 parses the `on:` key as boolean `True` — that is expected and harmless for a load-only sanity check.

- [ ] **Step 3: Commit**

```bash
git add .gitea/workflows/preview.yml
git commit -m "feat(preview): windows actions executor (preview.yml)"
```

---

## Task 2: Linux local executor — `.preview/up` and `.preview/down`

`.preview/up` runs on llamabox in a detached PR-head worktree: CMake native build, self-contained Linux publish, zip, existence-guarded registry PUT, then — if a live graphical session exists (a Wayland socket signals the hyprland session, and an XWayland X socket supplies `DISPLAY`) — launch the app on the hyprland display via XWayland and emit `kind: "app"`, else emit `kind: "artifact"`. `.preview/down` kills the launched PID (idempotent). GitWizardUI is Avalonia 11.2, which is X11-only on Linux (no native Wayland backend), so `DISPLAY` is the load-bearing launch var, not `WAYLAND_DISPLAY`.

**Files:**
- Create: `.preview/up`
- Create: `.preview/down`

**Interfaces:**
- Consumes (from pr_crew's local executor env): `PREVIEW_ID`, `PREVIEW_REF` (head SHA), `PREVIEW_WORKTREE`, `PREVIEW_PLATFORM`, `PREVIEW_PID_FILE`, `PREVIEW_GITEA_TOKEN`. cwd = the detached worktree.
- Produces: last stdout line JSON `{"kind":"app","note":…}` (PID written to `$PREVIEW_PID_FILE`) or `{"kind":"artifact","url":…,"note":…}`; the zip at `…/generic/<PREVIEW_ID>/<PREVIEW_REF>/GitWizardUI-linux-x64.zip`.

- [ ] **Step 1: Write `.preview/up`**

Create `.preview/up` with exactly this content:

```sh
#!/bin/sh
# git-wizard preview provider (linux local executor).
#
# Contract (spec 2026-07-10-preview-platform-executors-design.md §4; pr_crew
# preview.py): env in = PREVIEW_ID, PREVIEW_REF (head sha), PREVIEW_WORKTREE,
# PREVIEW_PLATFORM, PREVIEW_PID_FILE, PREVIEW_GITEA_TOKEN. cwd = detached
# worktree of the PR head. Out = one JSON line as the LAST stdout line
# {"kind", "url"?, "note"?}; all diagnostics to stderr; nonzero exit = failure
# (stderr tail lands in the PR comment).
#
# Unlike schoen-lab's site-kind provider there is no slot port and no URL
# probe: this builds the desktop app, uploads it to the gitea generic registry,
# and - if a graphical session is live - launches it on llamabox's hyprland
# display via XWayland (kind "app", PID-file liveness; see the display branch
# below). No display => artifact only.
set -eu

ROOT="$(dirname "$PREVIEW_PID_FILE")"
LOG="$ROOT/app.log"
ZIP="$ROOT/GitWizardUI-linux-x64.zip"
URL="https://gitea.llamabox.sticktoitive.net/api/packages/schoen/generic/$PREVIEW_ID/$PREVIEW_REF/GitWizardUI-linux-x64.zip"

# Build native (CMake/Ninja) then publish the self-contained linux GUI. Same
# two-step as ci.yml; Directory.Build.targets adds libMFTLibNative.so to the
# publish output once the native build exists. Progress to stderr so stdout
# stays clean for the JSON contract line.
cmake -S external/MFTLib/MFTLibNative -B external/MFTLib/build-linux -G Ninja -DCMAKE_BUILD_TYPE=Release 1>&2
cmake --build external/MFTLib/build-linux 1>&2
dotnet publish GitWizardUI/GitWizardUI.csproj -c Release -r linux-x64 --self-contained -o publish/gui/linux-x64 1>&2

# Zip the publish folder's CONTENTS (exe at archive root, matching run-preview).
( cd publish/gui/linux-x64 && zip -qr "$ZIP" . ) 1>&2

# Existence-guarded upload: a re-run for the same sha finds the file already
# present and skips the PUT (the registry 409s on duplicate name+version+file).
if curl -fsSI -o /dev/null -H "Authorization: token $PREVIEW_GITEA_TOKEN" "$URL"; then
    echo "artifact already present at $URL - skipping upload" 1>&2
else
    echo "uploading $URL" 1>&2
    curl -fsSL -X PUT -H "Authorization: token $PREVIEW_GITEA_TOKEN" \
        --data-binary "@$ZIP" "$URL" 1>&2
fi

# Display branch: launch on the live hyprland session if one exists. pr_crew may
# run under a different session than the interactive one, so derive the display
# vars explicitly and pass them to the child rather than inheriting.
#
# GitWizardUI is Avalonia 11.2, which has NO native Wayland backend: on Linux it
# uses the X11 backend and runs under hyprland via XWayland. DISPLAY (the X
# socket) is the load-bearing var - WAYLAND_DISPLAY is irrelevant to Avalonia 11.
# Do NOT drop DISPLAY in a future cleanup: without it the app opens no window.
# A live wayland-* socket signals the graphical session is up; the X socket under
# /tmp/.X11-unix supplies XWayland's DISPLAY. Wayland up but no X socket =>
# XWayland absent, so treat it as no-display (artifact only).
XDG="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
wayland_socket=""
for s in "$XDG"/wayland-*; do
    case "$s" in *.lock) continue ;; esac
    [ -S "$s" ] && { wayland_socket="$s"; break; }
done
display=""
for x in /tmp/.X11-unix/X*; do
    [ -S "$x" ] && { display=":${x##*/X}"; break; }
done

if [ -n "$wayland_socket" ] && [ -n "$display" ]; then
    app="$PREVIEW_WORKTREE/publish/gui/linux-x64/GitWizardUI"
    echo "launching $app on DISPLAY=$display (XWayland under ${wayland_socket##*/})" 1>&2
    env XDG_RUNTIME_DIR="$XDG" DISPLAY="$display" WAYLAND_DISPLAY="${wayland_socket##*/}" \
        nohup "$app" </dev/null >"$LOG" 2>&1 &
    pid=$!
    echo "$pid" > "$PREVIEW_PID_FILE"
    # If it dies immediately, fail loudly (and reap) rather than report a live
    # app that isn't.
    sleep 2
    if ! kill -0 "$pid" 2>/dev/null; then
        echo "app exited immediately; app.log tail:" 1>&2
        tail -n 30 "$LOG" 1>&2 || :
        kill "$pid" 2>/dev/null || :
        exit 1
    fi
    printf '{"kind": "app", "note": "running on the llamabox display \302\267 artifact: %s"}\n' "$URL"
    exit 0
fi

echo "no live X/XWayland display (wayland_socket='$wayland_socket' display='$display') - artifact only" 1>&2
printf '{"kind": "artifact", "url": "%s", "note": "no display session - use scripts/run-preview"}\n' "$URL"
exit 0
```

Note: `\302\267` is the UTF-8 encoding of the `·` middle-dot; `printf` emits it literally so the note reads `running on the llamabox display · artifact: …`.

- [ ] **Step 2: Write `.preview/down`**

Create `.preview/down` with exactly this content:

```sh
#!/bin/sh
# git-wizard preview teardown (linux local executor). Idempotent: kill the
# launched app if its PID is still alive; a no-op otherwise (artifact-only
# previews wrote no PID). The orchestrator also SIGTERMs by pidfile and rmtrees
# the scratch root, so nothing else is needed here.
set -eu

if [ -f "${PREVIEW_PID_FILE:-}" ]; then
    pid="$(cat "$PREVIEW_PID_FILE" 2>/dev/null || :)"
    if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
        kill "$pid" 2>/dev/null || :
    fi
fi
exit 0
```

- [ ] **Step 3: Mark both executable**

```bash
git update-index --chmod=+x .preview/up .preview/down 2>/dev/null || chmod +x .preview/up .preview/down
```

(The `git add` in Step 5 records the exec bit; on Windows checkouts `chmod` is a no-op but `git add --chmod=+x` in Step 5 still sets the mode.)

- [ ] **Step 4: Syntax-check both scripts**

Run: `bash -n .preview/up && bash -n .preview/down && echo "parse ok"`
Expected: `parse ok`

Run (if shellcheck is installed — `command -v shellcheck`): `shellcheck -s sh .preview/up .preview/down`
Expected: no errors. If shellcheck is absent, note "shellcheck not installed — skipped" and rely on `bash -n`. (Runtime display/launch behavior — the XWayland `DISPLAY` discovery and the app launch — cannot be exercised here; it is only smoke-testable on llamabox post-merge — see Task 5.)

- [ ] **Step 5: Commit (with exec bit)**

```bash
git add --chmod=+x .preview/up .preview/down
git commit -m "feat(preview): linux local executor (.preview/up + down)"
```

---

## Task 3: `run-preview` launcher scripts

Two functionally identical scripts that download and launch a published preview artifact for the current OS. Optional arg = PR number; no arg → newest git-wizard preview package.

**Files:**
- Create: `scripts/run-preview.sh`
- Create: `scripts/run-preview.ps1`

**Interfaces:**
- Consumes: gitea packages list API `GET /api/v1/packages/schoen?type=generic&q=<query>` (array of `{name, version, created_at, …}`); generic download `GET …/generic/<package>/<version>/GitWizardUI-<platform>-x64.zip`. Token from `GITEA_TOKEN` env if set; anonymous GET tried first.
- Produces: a launched `GitWizardUI` process from a temp dir.

- [ ] **Step 1: Write `scripts/run-preview.sh`**

Create `scripts/run-preview.sh` with exactly this content:

```sh
#!/bin/sh
# Download and launch a git-wizard PR preview artifact for the current OS.
#
# Usage:
#   scripts/run-preview.sh            # newest git-wizard preview package
#   scripts/run-preview.sh 42         # newest artifact for PR #42
#
# The preview provider (.preview/up on llamabox, or the windows preview.yml
# workflow) publishes the app to the gitea generic package registry. This
# fetches the current-OS zip, unpacks it to a temp dir, and launches
# GitWizardUI. Set GITEA_TOKEN if the registry needs auth; anonymous GET first.
# Requires: curl, jq, unzip.
set -eu

GITEA="https://gitea.llamabox.sticktoitive.net"
OWNER="schoen"

case "$(uname -s)" in
    Linux)  platform="linux" ;;
    *)      echo "no git-wizard preview artifact is published for $(uname -s)" >&2; exit 1 ;;
esac
file="GitWizardUI-${platform}-x64.zip"

# curl wrapper: add the auth header only when GITEA_TOKEN is set (anonymous
# first).
api() {
    if [ -n "${GITEA_TOKEN:-}" ]; then
        curl -fsSL -H "Authorization: token $GITEA_TOKEN" "$@"
    else
        curl -fsSL "$@"
    fi
}

if [ $# -ge 1 ]; then
    package="git-wizard-pr-$1"
    resp="$(api "$GITEA/api/v1/packages/$OWNER?type=generic&q=$package")"
    version="$(printf '%s' "$resp" | jq -r --arg n "$package" \
        '[.[] | select(.name == $n)] | sort_by(.created_at) | last | .version')"
else
    resp="$(api "$GITEA/api/v1/packages/$OWNER?type=generic&q=git-wizard-pr-")"
    package="$(printf '%s' "$resp" | jq -r 'sort_by(.created_at) | last | .name')"
    version="$(printf '%s' "$resp" | jq -r 'sort_by(.created_at) | last | .version')"
fi

if [ -z "$package" ] || [ "$package" = "null" ] || [ -z "$version" ] || [ "$version" = "null" ]; then
    echo "no git-wizard preview package found" >&2
    exit 1
fi

url="$GITEA/api/packages/$OWNER/generic/$package/$version/$file"
tmp="$(mktemp -d)"
echo "downloading $file ($package @ $version)" >&2
api -o "$tmp/$file" "$url"
( cd "$tmp" && unzip -q "$file" )
exe="$tmp/GitWizardUI"
chmod +x "$exe"
echo "launching $exe" >&2
exec "$exe"
```

- [ ] **Step 2: Write `scripts/run-preview.ps1`**

Create `scripts/run-preview.ps1` with exactly this content:

```powershell
#!/usr/bin/env pwsh
# Download and launch a git-wizard PR preview artifact for the current OS.
#
# Usage:
#   scripts/run-preview.ps1          # newest git-wizard preview package
#   scripts/run-preview.ps1 42       # newest artifact for PR #42
#
# The preview provider publishes the app to the gitea generic package registry;
# this fetches the current-OS zip, unpacks it to a temp dir, and launches
# GitWizardUI. Set $env:GITEA_TOKEN if the registry needs auth; anonymous first.
param([int]$PrNumber)

$ErrorActionPreference = "Stop"
$gitea = "https://gitea.llamabox.sticktoitive.net"
$owner = "schoen"

if ($IsWindows -or $env:OS -eq "Windows_NT") { $platform = "windows" }
elseif ($IsLinux) { $platform = "linux" }
else { Write-Error "no git-wizard preview artifact is published for this OS"; exit 1 }
$file = "GitWizardUI-$platform-x64.zip"

# Auth header only when GITEA_TOKEN is set (anonymous first).
$headers = @{}
if ($env:GITEA_TOKEN) { $headers["Authorization"] = "token $env:GITEA_TOKEN" }

if ($PSBoundParameters.ContainsKey('PrNumber')) {
    $query = "git-wizard-pr-$PrNumber"
    $listUrl = "{0}/api/v1/packages/{1}?type=generic&q={2}" -f $gitea, $owner, $query
    $pkgs = @(Invoke-RestMethod -Uri $listUrl -Headers $headers |
        Where-Object { $_.name -eq $query })
} else {
    $listUrl = "{0}/api/v1/packages/{1}?type=generic&q=git-wizard-pr-" -f $gitea, $owner
    $pkgs = @(Invoke-RestMethod -Uri $listUrl -Headers $headers)
}
$newest = $pkgs | Sort-Object { [datetime]$_.created_at } | Select-Object -Last 1
if (-not $newest) { Write-Error "no git-wizard preview package found"; exit 1 }

$url = "{0}/api/packages/{1}/generic/{2}/{3}/{4}" -f $gitea, $owner, $newest.name, $newest.version, $file
$tmp = New-Item -ItemType Directory -Path (Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName()))
$zip = Join-Path $tmp $file
Write-Host "downloading $file ($($newest.name) @ $($newest.version))"
Invoke-WebRequest -Uri $url -Headers $headers -OutFile $zip
Expand-Archive -Path $zip -DestinationPath $tmp -Force
$exe = if ($platform -eq "windows") { Join-Path $tmp "GitWizardUI.exe" } else { Join-Path $tmp "GitWizardUI" }
if ($platform -ne "windows") { chmod +x $exe }
Write-Host "launching $exe"
& $exe
```

- [ ] **Step 3: Syntax-check both scripts**

Run: `bash -n scripts/run-preview.sh && echo "sh parse ok"`
Expected: `sh parse ok`

Run (if shellcheck is installed): `shellcheck scripts/run-preview.sh`
Expected: no errors (or note "shellcheck not installed — skipped").

Run: `pwsh -NoProfile -Command "$null = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path scripts/run-preview.ps1), [ref]$null, [ref]$e); if ($e) { $e; exit 1 } else { 'ps parse ok' }"`
Expected: `ps parse ok` (no parse errors). (Download/launch cannot be exercised until an artifact exists — that is smoke 2, post-merge, Task 5.)

- [ ] **Step 4: Commit (sh with exec bit)**

```bash
git add --chmod=+x scripts/run-preview.sh
git add scripts/run-preview.ps1
git commit -m "feat(preview): run-preview launcher scripts (sh + ps1)"
```

---

## Task 4: Documentation

Document `/preview` usage for this repo. The **CI infrastructure** section of `AGENTS.md` already covers the runners and the release/screenshot workflows and is the fitting home; add a short subsection there. Add a one-line pointer to `README.md` alongside the other build/run docs.

**Files:**
- Modify: `AGENTS.md` (append a `### Preview builds (/preview)` subsection under `## CI infrastructure`)
- Modify: `README.md` (one pointer line)

- [ ] **Step 1: Add the AGENTS.md subsection**

In `AGENTS.md`, immediately after the `## CI infrastructure` bullet list (before the `## Tips` header), insert:

```markdown
### Preview builds (`/preview`)

`/preview` on a git-wizard PR (pr-crew comment command or the ops-dashboard button) builds and runs that PR's `GitWizardUI` on the platform you ask for. Default platform is **windows**; `/preview linux` targets llamabox.

- **Windows** (`.gitea/workflows/preview.yml`, dispatched on `windows-latest`): MSVC native build → self-contained `win-x64` publish → zip → upload to the gitea generic package registry at `…/packages/schoen/generic/git-wizard-pr-<N>/<head-sha>/GitWizardUI-windows-x64.zip` (auth: `CI_GITEA_TOKEN`). The upload is existence-guarded so a re-dispatch for the same SHA is a no-op.
- **Linux** (`.preview/up`, run by pr-crew on llamabox in a detached PR-head worktree): CMake native build → self-contained `linux-x64` publish → zip → registry upload (auth: `PREVIEW_GITEA_TOKEN`). If a live graphical session exists it launches the app on llamabox's hyprland display via XWayland — GitWizardUI is Avalonia 11.2 (X11-only on Linux), so `DISPLAY` is the load-bearing var (`kind: "app"`, PID in `$PREVIEW_PID_FILE`); otherwise it posts artifact links only (`kind: "artifact"`). `.preview/down` kills the launched process.
- **Run a published artifact yourself:** `scripts/run-preview.ps1 [<PR#>]` (Windows) or `scripts/run-preview.sh [<PR#>]` (Linux). No arg fetches the newest git-wizard preview package; both download the current-OS zip from the registry, unzip to a temp dir, and launch `GitWizardUI`. Set `GITEA_TOKEN` if anonymous registry reads are refused.

Enrollment (adding git-wizard to pr-crew's `[preview]` config on llamabox) is an operator step, not in this repo — see the plan's deploy notes.
```

- [ ] **Step 2: Add the README pointer**

In `README.md`, after the "Getting Started" build block (before the `## Projects` header), insert:

```markdown
### Preview a PR build

`/preview` on a git-wizard PR builds and publishes the desktop app for Windows or Linux; `scripts/run-preview.ps1` / `scripts/run-preview.sh` download and launch a published preview. See **CI infrastructure → Preview builds** in [AGENTS.md](AGENTS.md).
```

- [ ] **Step 3: Run the docs-update check**

Invoke the `docs-update` skill (or manually re-scan `README.md`, `AGENTS.md`, `CLAUDE.md`, and inline doc comments) to confirm no other doc statement drifted from the new behavior. This PR adds new files and describes new behavior; it changes no existing documented command, so the expected outcome is "no other docs affected."

- [ ] **Step 4: Commit**

```bash
git add AGENTS.md README.md
git commit -m "docs(preview): document /preview usage + run-preview scripts"
```

---

## Task 5: Branch finish — verification, draft PR, deploy + smoke notes

Consolidated syntax/sanity checks across every new file, then push and open the DRAFT PR on the canonical (gitea) remote and confirm all CI legs green. Deploy steps (llamabox config) and smokes 1–2 are recorded here because they cannot run pre-merge.

**Files:**
- No new files (verification + PR + notes only).

- [ ] **Step 1: Re-run all local static checks together**

```bash
python -c "import yaml; yaml.safe_load(open('.gitea/workflows/preview.yml')); print('yaml ok')"
bash -n .preview/up && bash -n .preview/down && bash -n scripts/run-preview.sh && echo "sh parse ok"
pwsh -NoProfile -Command "\$e=\$null; \$null=[System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path scripts/run-preview.ps1),[ref]\$null,[ref]\$e); if(\$e){\$e;exit 1}else{'ps parse ok'}"
command -v shellcheck >/dev/null && shellcheck -s sh .preview/up .preview/down scripts/run-preview.sh || echo "shellcheck not installed - skipped"
```
Expected: `yaml ok`, `sh parse ok`, `ps parse ok`, and either a clean shellcheck run or the "skipped" note.

- [ ] **Step 2: Confirm the worktree is clean and the exec bits are set**

Run: `git status --porcelain && git ls-files -s .preview/up .preview/down scripts/run-preview.sh`
Expected: no uncommitted changes; the three scripts show mode `100755` (executable). If any is `100644`, run `git update-index --chmod=+x <file>` and amend the owning commit.

- [ ] **Step 3: Push the branch to the canonical remote**

```bash
git push -u gitea feat/preview-provider
```
Expected: branch pushed to gitea (canonical). (The github mirror is secondary; gitea CI is the gate.)

- [ ] **Step 4: Open a DRAFT PR on gitea**

```bash
curl -fsSL -X POST \
  -H "Authorization: token $(cat ~/.gitea-token)" \
  -H "Content-Type: application/json" \
  -d '{"head":"feat/preview-provider","base":"main","title":"feat: /preview provider (windows actions + linux local + run-preview)","body":"Spec ④ of the preview-platform-executors design. Adds .gitea/workflows/preview.yml (windows actions executor), .preview/up + down (linux local executor), scripts/run-preview.{sh,ps1}, and docs. Wayland launch + registry round-trip are smoke-tested on llamabox post-merge (smokes 1-2). Deploy: add schoen/git-wizard to pr-crew [preview] config on llamabox.","draft":true}' \
  "https://gitea.llamabox.sticktoitive.net/api/v1/repos/schoen/git-wizard/pulls"
```
Expected: JSON with the new PR number.

- [ ] **Step 5: Confirm all CI legs are green**

Watch `https://gitea.llamabox.sticktoitive.net/schoen/git-wizard/actions` (or poll the runs API for the pushed head SHA). Confirm green: `Build + Test (Linux…)`, `Build + Test (Windows…)`, `aislop`, and the release publish-smoke if `paths` triggers it (this PR touches none of `release.yml`'s `paths`, so it should not). `preview.yml` itself does **not** run on push/PR (it is `workflow_dispatch`-only) — it is exercised by a real `/preview` post-merge, not by branch CI. If any leg is red, fix and re-push before marking the PR ready.

- [ ] **Step 6: Record the deploy steps (do NOT run pre-merge)**

These are operator actions on llamabox after this PR merges — capture them in the PR description or a follow-up note; they are not repo changes:

1. On llamabox, edit `~/.pr-crew/config.toml`:
   - Add `"schoen/git-wizard"` to the `[preview] repos` list.
   - Add the platform block:
     ```toml
     [preview.platforms."schoen/git-wizard"]
     linux = "local"
     windows = "actions:preview.yml"
     default = "windows"
     app = "GitWizardUI"
     ```
   - Restart the pr-crew service to reload config. No pr_crew code change is needed (machinery merged in PR #310).
2. Confirm the `windows-latest` act_runner VM (`win-runner`) is up so `workflow_dispatch` gets picked up.

- [ ] **Step 7: Record the post-merge smokes (spec §Testing, smokes 1–2 — run on llamabox)**

Cannot run pre-merge (need the deployed config + a live PR + llamabox's display). After deploy:

1. **Smoke 1 — `/preview linux`:** comment `/preview linux` on a git-wizard PR. Expect: `.preview/up` builds on llamabox, uploads `GitWizardUI-linux-x64.zip` to the registry, and — with the hyprland session live — launches the app on the display; the PR comment reports "running on the llamabox display" plus the artifact link. Verify the PID in the record is alive and the app window appears; then `/preview linux stop` kills it.
2. **Smoke 2 — bare `/preview` (→ windows default), coexisting with smoke 1 on the same commit:** comment `/preview`. Expect: `preview.yml` dispatches on the VM, builds, uploads `GitWizardUI-windows-x64.zip` to the *same* registry version (different file — no collision); the comment carries the artifact URL. On the Windows desktop, `scripts/run-preview.ps1 <PR#>` downloads and launches it. Confirm smoke 1's linux app is still live on the same commit (proves per-platform coexistence).

- [ ] **Step 8: Final commit (plan pruning at branch finish is handled by the executing skill)**

No code commit here. When the branch is finished (post-smoke), fold any durable insight into `AGENTS.md`/`README.md` and delete this plan per the writing-plans lifecycle; the deploy steps above move into the PR/merge record.
```
