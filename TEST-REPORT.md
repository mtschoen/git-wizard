# git-wizard verification report

2026-10-18 UTC, Windows, Release, non-elevated.

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

## Commands

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
