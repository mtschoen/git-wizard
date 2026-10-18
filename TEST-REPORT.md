# git-wizard verification report

2026-10-17 UTC, Windows Chonkers, Release, non-elevated.

| Field | Value |
| --- | --- |
| Status | PARTIAL: runtime checks pass; complete preflight failed |
| Mode | best-effort; configured 55% line coverage floor |
| Git | 145d940cb4f7f8f29072bed132ca0bf5bb4d572d plus lane/305-sweep-stale-list changes; MFTLib 008edd4550b179a2436964bdc1bca91f4388c1dc unchanged |
| Tests | Full NUnit: 1657 total, 1654 passed, 0 failed, 3 skipped; targeted: 24 passed; Python contracts with FORCE_COLOR=0: 12 passed |
| Coverage | 12200/16247 lines (75.09%); 5177/7568 branches (68.40%); configured 55% floor passed; changed discovery lines covered |
| Build | Release solution: 0 warnings, 0 errors |
| Lint | Roslyn/build: 0; final format: 0; InspectCode: 0; final installed aislop full scan: 100/100, 0 errors, 0 warnings, 6 informational punctuation observations |
| Preflight | Native, restore, build, inspect, test passed; initial format failed, targeted rerun passed; initial contracts failed, FORCE_COLOR=0 rerun passed; pinned aislop unavailable; Windows skips Linux coverage enforcement |
| Exclusions | No coverage exclusions, analyzer suppressions, or rule configuration changes added |

## Commands

```bash
"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" external/MFTLib/MFTLibNative/MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
dotnet build external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj -c Release -p:Platform=x64
dotnet build git-wizard.slnx -c Release --no-restore
dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release --nologo --filter FullyQualifiedName~SweepCliTests --results-directory C:/Users/mtsch/AppData/Local/GitWizard/test-results/305-red-final-20261017
dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release --nologo --filter "FullyQualifiedName~SweepCliTests|FullyQualifiedName~CliDiscoveryRoutingTests" --results-directory C:/Users/mtsch/AppData/Local/GitWizard/test-results/305-green-final-20261017
bash tools/ci-preflight.sh --changed-from gitea/main
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build --logger 'trx;LogFileName=TestResults.trx' --results-directory C:/Users/mtsch/AppData/Local/GitWizard/test-results/ffc4aad4d33943c6b087b1f456706e02/test-06exJn1v
python ci/post-coverage-status.py --cobertura 'C:/Users/mtsch/AppData/Local/GitWizard/test-results/ffc4aad4d33943c6b087b1f456706e02/test-06exJn1v/**/coverage.cobertura.xml' --gate-line 55 --summary --skip-post
bash tools/ci-preflight.sh --only format
FORCE_COLOR=0 python -m unittest discover -s ci -p 'test_*.py' -v
aislop scan .
```

The full NUnit command above was executed by the preflight test gate, not repeated separately.
