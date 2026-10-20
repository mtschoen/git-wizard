# CI, quality gates and release

## Local CI preflight

Run `bash tools/ci-preflight.sh --all` before pushing, or
`bash tools/ci-preflight.sh --changed-from main`. The latter validates the ref
and conservatively runs every host gate; it never guesses which gates a diff can skip.
Use `--only format` (repeatable) for diagnosis. Scopes: `native`, `restore`,
`format`, `build`, `inspect`, `test`, `coverage`, `contracts`, `aislop`.
Every selected gate completes even after another fails; the final exit is nonzero
on failure (2 for invalid arguments or unsupported platforms).

The wrapper declares `pr-crew-preflight-protocol: 1`. Complete successful runs
on clean, unchanged HEAD write the protocol stamp at the Git-resolved
`pr-crew/preflight-stamp.json` path. Targeted runs invalidate but never mint stamps.
The protocol's fixed gate-set labels (`gates`, `node`, `python`, `test-delta`)
identify a complete publication run, not this repository's scope names.

Linux runs the native/managed MFTLib build, project restore/build and analyzer gate,
solution format check, NUnit with coverage, the existing 55% coverage gate,
preflight contracts, and pinned aislop. Windows (Git Bash + Python 3 + PowerShell 7)
runs its native/full-solution build, format, NUnit, contracts, pinned aislop and
InspectCode; provision `jb` on PATH with the runner's global naming configuration.
Linux explicitly skips Windows InspectCode; Windows explicitly skips Linux coverage
enforcement. Neither host proves the other host's platform-specific tests. macOS is
refused because there is no matching CI job. Missing required tools fail their gate.
The contracts gate also validates the maintained-tools inventory, parses Bash/Python scripts, and tests hook logic on both hosts; Windows requires PowerShell parsing and isolated helper tests. Linux reports an explicit PowerShell skip when pwsh is absent.

CI and local execution share `ci/gates.sh`, `ci/windows-gates.ps1` and
`ci/aislop-gate.sh`, including the pinned fork and C# file-count check. Prepare
the scanner once with `bash ci/aislop-gate.sh prepare` (network required);
the local gate refuses a missing/mismatched pin or successful-build receipt instead
of downloading implicitly. Preparation invalidates that receipt before any work
and writes the pin only after installation (including its build lifecycle) succeeds.
Its default tool directory is `git-wizard/aislop` under `LOCALAPPDATA` on Windows
or `${XDG_STATE_HOME:-$HOME/.local/state}` elsewhere, outside the scanned checkout
(`RUNNER_TEMP/aislop` on CI); `AISLOP_TOOL_DIRECTORY` can point
to an owned cache elsewhere. With populated tool/NuGet caches no network services
or forge credentials are required for gate verdicts. Results use unique directories
outside the checkout as described in [testing](testing.md). Set `CI_RESULTS_DIRECTORY` explicitly to
reuse a results root, including separate `--only test` and `--only coverage` invocations.
Each test invocation writes a fresh child directory and replaces the root's
`current-test-directory` pointer. Coverage evaluates only that invocation's reports;
older results remain available for diagnosis but never contribute to the verdict.
When a selected test gate fails without producing Cobertura input, a later coverage
gate in that run is explicitly skipped; the failed test still makes the run fail.
Current-invocation Cobertura input is evaluated even if tests fail. Coverage alone still
fails on missing input. `CI_PREFLIGHT_STAMP_PATH` overrides the Git-resolved stamp
location for isolated execution; contract tests redirect it and state/results paths
to owned temporary directories, leaving developer stamps and state untouched.
Provisioning, status posts, uploads,
release/preview/screenshot jobs, destructive main-only clean/init verification,
and attended elevation/desktop tests are explicitly skipped at runtime, not passed.

## Lint gates

The bar is **0 findings** across every configured analyzer.

- **Analyzers + naming (CA*/IDE*/RCS*)** - `Directory.Build.props` sets `EnableNETAnalyzers`, `AnalysisLevel=latest-Recommended`, `EnforceCodeStyleInBuild`, and **`TreatWarningsAsErrors`**, so a normal `dotnet build`/MSBuild build *is* the gate. Roslynator 4.15.0 contributes only the explicitly pinned RCS1161 (explicit enum values), RCS1168 (contract-consistent parameter names), and RCS1238 (no nested conditional operators) rules from `.editorconfig`; every other RCS rule remains disabled by default, so dependency updates cannot silently expand the gate. AsyncFixer supplies the async checks, and `CA2000`/`CA1849`/`CA2016` the disposal, blocking-call, and token-forwarding ones. `EnforceCodeStyleInBuild` also turns the `.editorconfig` naming ruleset (IDE1006, at `warning`) into a **build error** - so the editorconfig is the **authoritative naming gate**, not just an IDE hint. MFTLib does not inherit these.

- **Formatting + naming** - `.editorconfig` is the canonical C# standard: **utf-8 (no BOM)**, CRLF, 4-space, and the full naming ruleset at `warning` -> build error (PascalCase types/members/const/static-readonly; `_camelCase` **all** private/internal fields incl. mutable static; camelCase params/locals; no `m_`/`k_`/`s_`). Formatting/charset check: `dotnet format git-wizard.slnx --verify-no-changes`; apply: `dotnet format git-wizard.slnx`. Local on-save hooks (`scripts/format-on-save.py`, wired in the tracked `.claude/settings.json` for Claude and `.agents/hooks.json` for Antigravity/AGY) run whitespace formatting per edit. The Python entry point is the single cross-platform implementation for both harnesses: it parses the hook JSON from stdin and runs `dotnet format whitespace` directly via `subprocess`, reporting one of three outcomes the calling harness can tell apart - a benign `skipped` (not applicable, or `dotnet` unavailable, exit 0), a successful `applied` (exit 0), or a loud `FAILED` when the formatter ran and returned a nonzero exit code (stderr, exit 2). (`dotnet format` fixes charset/whitespace/style but **cannot** auto-fix IDE1006 naming - rename via Rider/`jb cleanupcode`.)

- **Deep inspection** - `jb inspectcode git-wizard.slnx -o=jb-report.json --settings=git-wizard.slnx.DotSettings --severity=WARNING --no-updates --properties:Configuration=Release`, gated by `ci/windows-gates.ps1 inspect` (parses the SARIF report, prints per-rule counts and exits non-zero on any finding). To locate findings, run `python ci/parse-jb-report.py <report> [--max N]` on the same report (it prints each finding as `uri:line`); `ci/test_parse_jb_report.py` exercises it in the contracts gate. Match the gate's invocation exactly - omitting `--settings`/`--severity` reports suggestion-tier findings the gate never sees. The editorconfig is the primary naming gate. **jb-CLI binds `InconsistentNaming` to the machine's global ReSharper config, not the solution `.DotSettings`**; provision that configuration consistently with `.editorconfig`.

- **aislop** - `.gitea/workflows/aislop.yml` gates C# and Python at `failBelow: 100` in `.aislop/config.yml`. The workflow uses the fork pinned in `.aislop/fork-commit` through `ci/aislop-gate.sh`, including its build lifecycle and minimum C# file-count check. Do not substitute the public npm scanner: it does not supply this fork's C# engine. `lint.csharp.projectEvaluation` is `false`, so project-backed C# tools stay in `ci.yml`. Findings map to fixes, not mutes: oversized files split into partials, long functions extracted, TODOs tracked as issues, narrative comments retain why-rationale. Pin updates are deliberate commit changes, not a moving branch reference.

Run evidence is not tracked in the repository: coverage verdicts live in the informational `pr-crew/coverage` commit status and the CI job summary, and attended verification passes are recorded as comments on the issue they verify.

## CI infrastructure

Workflows live in `.gitea/workflows/`; YAML is the source of truth for jobs and
steps. Preserve job names and required checks. `ci/checkout.py pin-only` is the
single early-exit implementation for Linux and Windows. Only a successful diff
containing exactly `.aislop/fork-commit` skips expensive steps. Probe failures
run the job in full. Never use workflow-level path filtering on required checks.
`ci/checkout.py clean` supplies both destructive Windows clean/status checks.

Both test jobs initialize the submodule and use shared native, restore, build and
test recipes. Release, preview and screenshot builds also use
`bash ci/gates.sh native`; screenshots use Release consistently.

- **Windows toolchain:** `windows-latest` is a host-mode runner; the .NET SDK is provisioned on the host, so Windows jobs in `ci.yml` carry no `actions/setup-dotnet` step under the runner provisioning policy (setup actions are permitted only in Linux containers; `preview.yml`, `release.yml`, and `screenshot.yml` still use setup-dotnet until updated).
- **Runners:** `ci.yml` uses `ubuntu-latest` for cross-platform build and NUnit with coverage, and `windows-latest` for full-solution build and NUnit. Both runners run the suite; only Linux collects coverage.
- **Coverage gate:** `ci/post-coverage-status.py` posts informational `pr-crew/coverage`, writes the summary table and enforces the 55% line threshold via `ci/gates.sh coverage`. Whole-assembly GitWizardUI coverage includes Avalonia glue. The Cobertura XML is uploaded as `coverage-cobertura`.
- **Release identity:** the release workflow reads `CI_GITEA_TOKEN` for repository releases/assets. Provision a write collaborator and a repository-scoped token. Rotate by replacing the repository Actions secret, not by editing code.
- **GitHub release mirror:** after a tag push creates the Gitea release and uploads all assets, `release.yml` creates the matching GitHub release and uploads the same zip files. The mirror is deliberately skipped by PR publish-smoke runs. A mirror failure fails the release job loudly; the Gitea release may already exist, so repair or remove the partial releases before rerunning the tag workflow.
- **GitHub mirror secret:** store a GitHub PAT with `repo` scope for the configured mirror repository as `GITHUB_RELEASE_TOKEN`. The workflow validates that credential before creating the Gitea release, so a missing or revoked token fails without leaving a Gitea-only release. Before first release, use a throwaway branch/version/tag to verify both prerelease pages and assets, then remove the test releases/tag.
- **Branch protection:** require `CI / Build + Test (Linux, cross-platform projects) (pull_request)`, `CI / Build + Test (Windows, full solution) (pull_request)` and `aislop / quality-gate (pull_request)`, one approval, and restricted force-pushes. Configure only after the checks first run successfully. Protection is an operator setting, not a workflow declaration.
- **Deliberate non-goals:** no binary signing, no MAUI, and no macOS CI runner.
- **Artifact-upload cert gap:** TLS verification stays enabled. `ci.yml` tolerates coverage-artifact upload failure (`continue-on-error: true`); the coverage gate reads Cobertura directly. Upload success is not implied by a passing gate. The runner CA investigation remains in `PLAN.md` under Infrastructure.

### Release credential provisioning

Provision a dedicated Gitea `ci-bot` identity for the `CI_GITEA_TOKEN` secret:

```bash
sudo -u git gitea admin user create --username ci-bot --email ci-bot@llamabox.internal --random-password --must-change-password=false
sudo -u git gitea admin user generate-access-token --username ci-bot --token-name git-wizard-ci --scopes write:repository --raw
```

Add `ci-bot` as a **Write** collaborator on `schoen/git-wizard` (needed to create releases + upload assets). `write:repository` is the only scope required.
Store its PAT as the repository Actions secret `CI_GITEA_TOKEN`; rotate by
generating a replacement and updating that secret. Provisioning is an operator
action, not a build step or a claim that the checkout records live account state.

### Preview builds

`/preview` on a git-wizard PR (pr-crew comment command or the ops-dashboard button) builds and runs that PR's `GitWizardUI` on the platform you ask for. Default platform is **windows**; `/preview linux` selects the Linux executor. Enrollment in pr-crew's `[preview]` configuration is an operator step, not repository code.

- **Windows** (`.gitea/workflows/preview.yml`, dispatched on `windows-latest`): shared native/managed build -> self-contained `win-x64` publish -> zip -> generic package-registry upload using `PREVIEW_REGISTRY_TOKEN`. The registry token needs package scope; `CI_GITEA_TOKEN` is for repository release assets. The upload is existence-guarded so a re-dispatch for the same SHA is a no-op.
- **Linux** (`.preview/up`, run by pr-crew in a detached PR-head worktree): shared native/managed build -> self-contained `linux-x64` publish -> zip -> registry upload. The provider prefers the operator's package-scoped token file, otherwise `PREVIEW_GITEA_TOKEN`. If a live graphical session exists it launches through XWayland: GitWizardUI uses Avalonia 11.2's X11 backend, so retain `DISPLAY` (`kind: "app"`, PID in `$PREVIEW_PID_FILE`); otherwise it posts artifact links only (`kind: "artifact"`). `.preview/down` kills the launched process.
- **Run a published artifact yourself:** `scripts/run-preview.ps1 [<PR#>]` (Windows) or `scripts/run-preview.sh [<PR#>]` (Linux). No arg fetches the newest git-wizard preview package; both download the current-OS zip from the registry, unzip to a temp dir, and launch `GitWizardUI`. Set `GITEA_BASE_URL` to the Gitea origin; set `GITEA_TOKEN` if anonymous registry reads are refused.

## Release checklist

1. Update the single `<Version>` in `Directory.Build.props`. The desktop inherits
   it; the CLI reads the generated assembly informational version, without its
   source-revision suffix. `AgentInstructionsTests` checks both against the property.
2. Update the screenshot using [local capture or CI](testing.md#screenshots).
3. Commit and push intended version/screenshot changes.
4. Tag `v<Version>` and push the tag to the canonical release remote.
5. Verify the release workflow and both forge release pages. Release validates the
   tag against the evaluated MSBuild Version on both publication hosts.

The release attaches `git-wizard-{ver}-{rid}.zip` and
`GitWizardUI-{ver}-{rid}.zip` for `win-x64`, `linux-x64`, and `osx-x64`.
The macOS artifact is managed-only; there is no macOS native-build runner.
Manual workflow versions and PR smoke filenames are separate from tag validation.

## Verification sources

`tools/ci-preflight.sh` / `ci/preflight.py` implement host selection, aggregation,
stamp publication and skip reporting; `ci/test_preflight.py` tests those paths.
`ci/checkout.py` / `ci/test_checkout.py` own conservative pin detection and
clean/status failures. Analyzer settings are in `Directory.Build.props` and
`.editorconfig`; `.aislop/config.yml`, `.aislop/fork-commit` and
`ci/aislop-gate.sh` define the scanner gate. Release secrets, mirror order,
tag validation and artifact names are in `.gitea/workflows/release.yml`;
preview auth and launch behavior are in `.gitea/workflows/preview.yml`,
`.preview/up`, `.preview/down` and `scripts/run-preview.*`.
