# Gitea CI for git-wizard

**Date:** 2026-04-28
**Status:** Approved (brainstorming)
**Author:** Matt + Claude

## Summary

Set up Gitea Actions on `schoen/git-wizard` (mirrored from GitHub, hosted on
`gitea.llamabox.internal`) to build and test on every push to `main` and every
pull request, and to publish a multi-platform release artifact set on every
`v*` tag push. Builds split across the two registered Gitea runners:

- `llamabox-ubuntu` (label `ubuntu-latest`) — fast feedback on the
  cross-platform projects.
- `llamabox-windows` (label `windows-latest`) — full-solution build including
  the MAUI desktop UI, plus all unit tests.

A new dedicated Gitea bot identity (`ci-bot`) holds the PAT used by the
release workflow to publish releases and upload assets.

## Goals

- Block merges to `main` if either the Linux build or the Windows
  build/test job fails.
- Automate the manual MAUI release-zip step from `CLAUDE.md`.
- Add CLI and Avalonia builds for `win-x64`, `linux-x64`, `osx-x64` to every
  release.
- Use a dedicated `ci-bot` Gitea user (reusable across other personal repos),
  matching the per-harness identity pattern from pr-crew.

## Non-goals

- Running the MAUI UI screenshot tests (`GitWizardUI.UITests`) in CI. They
  remain a manual screenshot tool — they require an interactive desktop
  session and were designed for that use case.
- Building MAUI for macOS or Android. Today the MAUI csproj only conditions
  the macOS TFM on macOS hosts; we have no macOS runner. (Avalonia covers
  the cross-platform desktop story.)
- Code coverage reporting / SonarQube / external dashboards.
- Signing the published binaries.
- Mirror-side replication. The GitHub mirror keeps its own (separate) CI
  story or none at all; this spec is Gitea-only.

## References

- Gitea Actions docs: <https://docs.gitea.com/usage/actions/overview>
- pr-crew per-harness identity spec:
  `~/pr-crew/docs/specs/2026-04-26-per-harness-gitea-identity-design.md`
  (`ci-bot` follows the same provisioning pattern as the harness bots).
- `CLAUDE.md` § Build, § Release checklist — current manual MAUI publish flow.
- `actions/setup-dotnet@v4`, `actions/checkout@v4`, `actions/cache@v4`.

## 1. Architecture

Two workflow files under `.gitea/workflows/`:

### 1.1 `.gitea/workflows/ci.yml`

**Triggers:** `push` to `main`; `pull_request` targeting `main`.

**Concurrency:** cancel in-progress runs on the same ref when a new push
lands. Group key `ci-${{ github.ref }}`.

**Jobs (run in parallel):**

| Job             | Runner label    | What it does                                                                                         |
|-----------------|-----------------|------------------------------------------------------------------------------------------------------|
| `build-linux`   | `ubuntu-latest` | `dotnet restore` + `dotnet build -c Release` for each cross-platform csproj (see §2.1).              |
| `test-windows`  | `windows-latest`| `dotnet restore` for the solution, install MAUI workload, build full solution, run `dotnet test`.    |

Both jobs are required for PR merge (branch protection — see §6).

### 1.2 `.gitea/workflows/release.yml`

**Triggers:** `push` of tags matching `v*` (e.g. `v0.5.0`, `v0.5.0-rc1`).

**Jobs:**

| Job             | Runner label    | Needs            | What it does                                                                          |
|-----------------|-----------------|------------------|---------------------------------------------------------------------------------------|
| `publish-cross` | `ubuntu-latest` | —                | `dotnet publish` for `git-wizard` and `GitWizardAvalonia`, each × {win-x64, linux-x64, osx-x64}. Uploads each as a workflow artifact. |
| `publish-maui`  | `windows-latest`| —                | `dotnet publish GitWizardUI/...` (existing MSBuild target produces `Releases/GitWizardUI-{version}.zip`). Uploads zip as workflow artifact. |
| `release`       | `ubuntu-latest` | both above       | Downloads all artifacts, validates tag-vs-csproj version match, creates Gitea release via API, uploads assets. |

## 2. Step details

### 2.1 Cross-platform project list (Linux job)

The Linux runner builds these csprojs explicitly (a `dotnet build` of
`git-wizard.slnx` would try to evaluate `GitWizardUI.csproj`, which has no
`TargetFrameworks` outside Windows/macOS and would error):

- `GitWizard/GitWizard.csproj`
- `git-wizard/git-wizard.csproj`
- `GitWizardUI.ViewModels/GitWizardUI.ViewModels.csproj`
- `GitWizardAvalonia/GitWizardAvalonia.csproj`
- `GitWizardTests/GitWizardTests.csproj`

`GitWizardUI/` and `GitWizardUI.UITests/` are skipped on Linux.

The Linux job does **not** run tests — per the user's instruction, all
test execution happens on the Windows runner where the MAUI/MFTLib pieces
are available and matched to the target platform.

### 2.2 .NET SDK

Both jobs use `actions/setup-dotnet@v4` with `dotnet-version: 10.0.x`.
If `setup-dotnet` cannot resolve a stable `10.x` channel at workflow time,
fall back to `quality: preview`. (No `global.json` change is needed; we can
add one in a follow-up if SDK version drift becomes a problem.)

### 2.3 MAUI workload (Windows job only)

After `setup-dotnet`:

```pwsh
dotnet workload install maui-windows
```

`maui-windows` is sufficient because `GitWizardUI.csproj` only conditions
TFMs on Windows/macOS — we don't build Android/iOS. This step is slow on a
fresh runner; it is the prime caching candidate after NuGet.

### 2.4 NuGet caching

`actions/cache@v4` keyed on `${{ runner.os }}-nuget-${{ hashFiles('**/*.csproj') }}`
with restore-keys falling back to `${{ runner.os }}-nuget-`. Caches
`~/.nuget/packages` (and `%USERPROFILE%\.nuget\packages` on Windows).

### 2.5 MFTLib native DLL

CI uses the NuGet `PackageReference` form (per `CLAUDE.md` the local
`ProjectReference` form is dev-only). The pre-built native DLL ships inside
the package, so no special handling is required. The existing
`BlockLocalMFTLibOnPublish` MSBuild target stays as a guardrail and would
fire if someone accidentally checks in a `ProjectReference`.

### 2.6 Test execution (Windows job)

```pwsh
dotnet test GitWizardTests/GitWizardTests.csproj `
    -c Release `
    --no-build `
    --logger "trx;LogFileName=TestResults.trx" `
    --results-directory ./TestResults
```

`GitWizardUI.UITests` is **not** invoked. TRX results are uploaded as a
workflow artifact for inspection on failure.

### 2.7 Checkout

`actions/checkout@v4` with default `fetch-depth: 1`. The release workflow
needs the tag, but `${{ github.ref_name }}` exposes it without deeper history.

## 3. Release flow

### 3.1 Version derivation

The release job reads the tag from `${{ github.ref_name }}`. It strips a
leading `v` for use in filenames (e.g. `v0.5.0` → `0.5.0`).

It then reads `ApplicationDisplayVersion` from
`GitWizardUI/GitWizardUI.csproj` and asserts it matches the stripped tag.
On mismatch, the job fails fast with a message like:

> Tag is `v0.5.0` but `ApplicationDisplayVersion` is `0.4.0`. Bump the
> csproj or move the tag.

This catches the existing manual error case from `CLAUDE.md` § Release
checklist where the version bump is one of nine steps.

### 3.2 CLI artifacts (Linux runner)

For each `rid` in `{win-x64, linux-x64, osx-x64}`:

```bash
dotnet publish git-wizard/git-wizard.csproj \
    -c Release -r "$rid" \
    --self-contained \
    -p:PublishSingleFile=true \
    -o "publish/cli/$rid"
zip -j "git-wizard-${VERSION}-${rid}.zip" "publish/cli/${rid}/git-wizard"*
```

(Windows publish output is `git-wizard.exe`; Linux/macOS is `git-wizard`.
The glob covers both.)

### 3.3 Avalonia artifacts (Linux runner)

Same pattern for `GitWizardAvalonia/GitWizardAvalonia.csproj`. The
`OutputType=WinExe` setting in the csproj is benign on Linux/macOS (it only
affects Windows console-window behavior).

If `dotnet publish` fails on a particular RID due to ReadyToRun
incompatibility, add `-p:PublishReadyToRun=false`. Not added preemptively.

### 3.4 MAUI artifact (Windows runner)

```pwsh
dotnet publish GitWizardUI/GitWizardUI.csproj `
    -f net10.0-windows10.0.19041.0 -c Release
```

The existing MSBuild target (`CreateReleaseZip`, see csproj) emits
`Releases/GitWizardUI-{version}.zip` and deletes any older zip. The job
uploads the resulting zip as a workflow artifact.

### 3.5 Release creation (Linux runner)

The `release` job:

1. Downloads all artifacts from the two publish jobs.
2. Determines `prerelease` from the tag suffix: `v0.5.0-rc1` → `prerelease: true`.
3. Creates the release:

   ```bash
   curl -fsSL -X POST \
       -H "Authorization: token ${{ secrets.CI_GITEA_TOKEN }}" \
       -H "Content-Type: application/json" \
       -d "{\"tag_name\":\"$TAG\",\"name\":\"$TAG\",\"draft\":false,\"prerelease\":$PRE}" \
       "$GITEA_BASE_URL/api/v1/repos/$REPO/releases"
   ```

4. For each artifact, uploads via `POST /repos/{owner}/{repo}/releases/{id}/assets`.

If any asset upload fails, the job fails loudly. The release exists at that
point but is incomplete; re-running the workflow re-creates the missing
assets idempotently (the implementation plan covers the
"already-uploaded" handling).

## 4. CI bot Gitea identity

A new dedicated Gitea user, **`ci-bot`**, holds the PAT that the release
workflow uses. Reusable across other personal repos that want CI later.

### 4.1 Provisioning (one-time, on the Gitea host)

Per the pr-crew identity-bot pattern:

```bash
sudo -u git gitea admin user create \
    --username ci-bot \
    --email ci-bot@llamabox.internal \
    --random-password \
    --must-change-password=false

sudo -u git gitea admin user generate-access-token \
    --username ci-bot \
    --token-name git-wizard-ci \
    --scopes write:repository \
    --raw
```

The `--raw` output is the PAT to copy into the repo secret (§4.4).

### 4.2 Repo access

Add `ci-bot` as a collaborator on `schoen/git-wizard` with **Write**
permission (needed to create releases and upload assets):

```bash
curl -fsSL -X PUT \
    -H "Authorization: token $(cat ~/.gitea-token)" \
    -H "Content-Type: application/json" \
    -d '{"permission":"write"}' \
    https://gitea.llamabox.internal/api/v1/repos/schoen/git-wizard/collaborators/ci-bot
```

(Run from the `schoen` admin token, not from `ci-bot` itself.)

### 4.3 Token scope

`write:repository` is the only scope required:

- `POST /repos/{owner}/{repo}/releases` — create release.
- `POST /repos/{owner}/{repo}/releases/{id}/assets` — upload asset.

No `write:user`, no `admin`, no SSH key.

### 4.4 Secret storage

The PAT is stored as the repo secret **`CI_GITEA_TOKEN`** under
*Settings → Actions → Secrets and Variables*. The release workflow reads
it as `${{ secrets.CI_GITEA_TOKEN }}`. The token never appears in any
committed file.

### 4.5 Token rotation

If the PAT is revoked or compromised, re-run §4.1's
`generate-access-token` command and re-paste the new value into the
`CI_GITEA_TOKEN` secret. No code change needed.

## 5. Defaults and operational details

| Knob                  | Value                                                               |
|-----------------------|---------------------------------------------------------------------|
| Artifact retention    | Default (90 days for workflow artifacts; release assets are forever)|
| Concurrency on PR     | Cancel in-progress on the same ref                                  |
| Concurrency on tags   | No cancellation (releases must complete)                             |
| Required PR checks    | `build-linux` and `test-windows` (configured in branch protection)  |
| Workflow timeout      | 30 minutes per job                                                  |

## 6. Branch protection

After the workflows are working, configure on `schoen/git-wizard`:

- *Settings → Branches → Add rule* for `main`.
- Require status checks: `build-linux`, `test-windows`.
- Require branches up to date before merging: yes.
- Restrict force-pushes: yes.

Branch protection is configured manually in the Gitea UI (or via API) — not
part of the workflow files. Listed here so the spec is complete.

## 7. Pre-flight checklist (one-time setup)

Before the workflows can run end-to-end:

1. The currently-offline `llamabox-windows` runner needs to come back
   online (it shows offline in `/api/v1/admin/actions/runners`). The Linux
   runner is online.
2. `ci-bot` user provisioned on Gitea (§4.1).
3. `ci-bot` added as collaborator with Write on `schoen/git-wizard` (§4.2).
4. `CI_GITEA_TOKEN` repo secret set to `ci-bot`'s PAT (§4.4).
5. Branch protection configured (§6) — *after* the first successful CI run
   on `main`, so we don't lock ourselves out.

The implementation plan will track these as explicit pre-implementation
tasks the user runs by hand; the workflow YAML changes are independent and
can be committed first.

## 8. Failure modes and fallbacks

- **`setup-dotnet` cannot find .NET 10 stable.** Use `quality: preview` or
  pin to a specific `10.0.100-preview.x` version.
- **MAUI workload install times out / network blip.** Re-run the failing
  job; cache will hit on retry.
- **Avalonia ReadyToRun fails for a RID.** Add
  `-p:PublishReadyToRun=false` only for the affected RID step.
- **Tag/csproj version drift.** §3.1 fails fast. Fix path: bump csproj
  and re-tag (or move the tag) — same as the manual checklist today.
- **Partial release-asset upload.** Job fails; rerunning the workflow
  retries idempotently.
