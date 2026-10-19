# git-wizard verification report

## Conflict-resolution verification

2026-10-19 UTC, Windows, Release, non-elevated.

| Field | Value |
| --- | --- |
| Status | PASS: final complete Windows preflight exited 0 |
| Mode | best-effort; configured 55% line coverage floor |
| Git | 34e2aca96e93d289053dff4544440cc13479ca34 merged with 684339b from gitea/main; MFTLib pin unchanged |
| Tests | Targeted CLI: 123 passed; targeted watch: 66 passed; full NUnit: 1805 passed, 0 failed, 3 reported skips |
| Coverage | 12098/15923 lines (75.98%); 5163/7436 branches (69.43%); explicit 55% line gate passed |
| Build | Native, managed dependencies and Release solution passed; solution 0 warnings, 0 errors |
| Lint | Roslyn/build: 0 warnings, 0 errors; InspectCode: 0 findings; format: passed; pinned aislop: 100 over 337 files; staged scan: 100, 0 issues |
| Contracts | 12 Python contract tests passed |
| Smoke | Actual CLI summary stdout parsed as schema 2.1 JSON; session diagnostics on stderr; attention exit 1 |
| Limits | Linux-only, elevated/live-volume and explicit stress cases not exercised; Windows preflight skips Linux coverage enforcement |
| Exclusions | No coverage exclusions, analyzer suppressions or rule changes added |

```bash
dotnet build git-wizard.slnx -c Release --no-restore
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build --filter 'FullyQualifiedName~ReportCliOutputTests|FullyQualifiedName~RunConfigurationCommandKindTests|FullyQualifiedName~InventoryCommandOutputTests|FullyQualifiedName~ProgramHelperTests|FullyQualifiedName~UpdateHandlerTests|FullyQualifiedName~ReportPublicationTests' --results-directory <external-targeted-results>
FORCE_COLOR=0 CI_RESULTS_DIRECTORY=<external-preflight-results> bash tools/ci-preflight.sh --all
dotnet format whitespace git-wizard.slnx --no-restore --include GitWizardTests/ProgramEntryPointTests.ClearCache.cs GitWizardTests/ProgramEntryPointTests.Inventory.cs GitWizardTests/ReportCliOutputTests.cs
FORCE_COLOR=0 bash tools/ci-preflight.sh --only format
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build --filter 'FullyQualifiedName~IndexVolumeChangeSource' --results-directory <external-watch-results>
aislop scan --staged
python ci/post-coverage-status.py --cobertura '<external-preflight-results>/test-OqMsluWD/**/coverage.cobertura.xml' --gate-line 55 --summary --skip-post
dotnet git-wizard/bin/Release/net10.0/git-wizard.dll --paths <worktree> --summary --no-mft --no-local-commit-count --rebuild-report
```

Full-suite TRX, Cobertura and smoke output are retained outside the checkout under
`%LOCALAPPDATA%/GitWizard/test-results/pr360-conflicts-final-20261018-2011`
(smoke output: `pr360-conflicts-preflight-20261018-2003`).
The initial complete preflight failed only the format gate; after CRLF correction and
unchanged extraction of drive-error collection, the final complete preflight passed.
The two pre-merge verification records below are retained from both sides of the conflict.

2026-10-18 UTC, Windows, Release, non-elevated.

## PR branch verification before conflict resolution

| Field | Value |
| --- | --- |
| Status | PASS: all selected Windows preflight gates and CLI smoke passed |
| Mode | best-effort; configured 55% line coverage floor |
| Git | 6e63a2db543bc80fefcf46a5b16e5600a49ea84e plus issue/335-gw-cli-stdout-summary changes; MFTLib b458b84232c416c63cf66d0853b01871b244ff70 unchanged |
| Tests | Full NUnit after merging main: 1663 passed, 0 failed, 3 skipped; subprocess clear-cache stdout/stderr case fails without the stderr routing and passes with it |
| Coverage | 12050/15928 lines (75.65%); 5112/7428 branches (68.82%); 55% line gate passed; previous recorded line coverage 75.09% |
| Build | Release solution: 0 warnings, 0 errors |
| Lint | Roslyn/build: 0 warnings, 0 errors; installed aislop: 100/100, 0 errors, 0 warnings, 6 informational punctuation observations in untouched files |
| Preflight | Native, restore, format, build, inspect, test, contracts and pinned aislop passed; Windows skips Linux coverage enforcement; the retained Windows coverage artifact also passed an explicit 55% check |
| Smoke | Actual CLI stdout parsed as schema 2.1 JSON and matched the saved report; session, progress and verbose callback diagnostics appeared on stderr; dirty repository returned attention exit 1 |
| Exclusions | No coverage exclusions, analyzer suppressions, or rule configuration changes added |

## Main branch verification before conflict resolution

| Field | Value |
| --- | --- |
| Status | PASS |
| Mode | best-effort; configured 55% line coverage floor |
| Git | 61dd8d2ebec0083cbde6b0cf37b20c7a541169b5 merged with 334609ad5852c4206dcef1e62a83fe48db093022, plus shared-guard regression assertion |
| Tests | 1671 passed, 0 failed, 3 reported skips, 1674 adapter total; TRX inventories 1720 entries |
| Coverage | 12132/16009 lines (75.78%); 5137/7462 branches (68.84%); Windows skips Linux coverage enforcement |
| Build | Native Release/x64, managed MFTLib and TestExtensions, solution Release: passed; solution 0 warnings, 0 errors |
| Lint | Roslyn/build: 0; format: 0; InspectCode: 0; installed aislop: 100/100, 325 files, 0 errors, 0 warnings, 0 fixable |
| Contracts | 12 Python contract tests passed |
| Regression | ClearAndRefreshShareOneGuard: separate guards failed (1 inspection); restored shared guard passed (0 inspections) |
| Exclusions | No coverage exclusions, analyzer suppressions, or rule changes added |

## PR branch commands

```bash
pwsh -NoProfile -File ./init.ps1 -Build
dotnet build GitWizardTests/GitWizardTests.csproj -c Release --no-restore
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build --filter 'FullyQualifiedName~UpdateHandlerTests|FullyQualifiedName~ReportCliOutputTests|FullyQualifiedName~GitWizardLogTests|FullyQualifiedName~ReportPublicationTests' --results-directory <owned-targeted-results>
FORCE_COLOR=0 bash tools/ci-preflight.sh --all
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build --logger 'trx;LogFileName=TestResults.trx' --results-directory C:/Users/mtsch/AppData/Local/GitWizard/test-results/issue335-final2-20261018/test-hyunI54O
python ci/post-coverage-status.py --cobertura 'C:/Users/mtsch/AppData/Local/GitWizard/test-results/issue335-final2-20261018/test-hyunI54O/**/coverage.cobertura.xml' --gate-line 55 --summary --skip-post
aislop scan .
aislop scan --staged
```

The final full NUnit command above was executed by the final preflight test gate. Its
TRX and Cobertura artifacts remain outside the checkout. One earlier preflight attempt
passed NUnit but failed six disposal inspections in the new lifetime test. That test
was restructured without suppressions; targeted tests, review and the final complete
preflight then passed. Production source stayed unchanged during this test-only correction.

## Known gaps

Linux-only, elevated/live-volume and explicit stress cases are not proven by this
non-elevated Windows run. The TRX retains 49 NotExecuted entries, including platform
and explicit cases; the VSTest summary counts three skips. Attended desktop/elevation,
remote publication checks and destructive main-only clean/init verification are skipped
by the preflight contract. A dirty working tree prevents publication-stamp creation.

The installed scanner is older than the repository pin; the complete preflight used
the matching pinned scanner. Six informational observations remain in untouched
`docs/report-schema.md` and `.gitea/workflows/preview.yml`.

## Main branch commands

```bash
"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" external/MFTLib/MFTLibNative/MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
dotnet build external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj -c Release -p:Platform=x64
dotnet restore git-wizard.slnx
dotnet build git-wizard.slnx -c Release --no-restore
dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release --nologo --filter FullyQualifiedName=GitWizardTests.UI.IndexCacheViewModelTests.ClearAndRefreshShareOneGuard --results-directory .claude/scripts/results-restored-final
dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release --nologo --results-directory .claude/scripts/results-full-final
dotnet format git-wizard.slnx --verify-no-changes --no-restore
"$HOME/.dotnet/tools/jb.exe" inspectcode git-wizard.slnx -o=.claude/scripts/jb-verified.xml --settings=git-wizard.slnx.DotSettings --severity=WARNING --no-updates --properties:Configuration=Release
python ci/parse-jb-report.py .claude/scripts/jb-verified.xml
python -m unittest discover -s ci -p 'test_*.py' -v
aislop scan .
```

## Verification limits

Linux/macOS execution, attended desktop/elevation checks, real broker/journal integration,
and the explicit respawn stress test were not exercised. Six informational aislop punctuation
findings remain in untouched files. No remote CI result is claimed.
