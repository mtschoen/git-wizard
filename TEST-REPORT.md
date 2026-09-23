# git-wizard verification report

2026-09-23 UTC, Windows chonkers, Release, non-elevated.

| Field | Value |
| --- | --- |
| Status | PASS for the required Windows non-admin gates |
| Mode | best-effort; configured 55% line coverage floor |
| Git | branch fix/249-persist-mft-index off main f12ad16 (git-wizard issue 249, persistent MFT index and snapshot opening) |
| Tests | VSTest: 1389 total, 1385 passed, 0 failed, 4 skipped |
| Coverage | 7789/12262 lines (63.52%); 3070/5297 branches (57.96%) |
| Build | Native DLL, MFTLib, MFTLibTestExtensions, solution: each 0 warnings, 0 errors |
| Lint | Roslyn/build: 0; format: 0; InspectCode: 0; aislop: 100 on changed files with 0 findings; full scan 100 with the existing RepositoryWatchService.cs file-size warning |
| Exclusions | No coverage exclusions or analyzer suppressions added |

The persistent and snapshot tests use the portable in-memory broker and packed-block fixtures with
owned temporary cache directories; no UAC, real-home cache, or live USN journal was used. Linux
runtime tests were not run on this Windows host, so platform slot-lock behavior on Linux is covered
only by CI. No attended cold-versus-warm timing was recorded here.

## Attended: live-after-discovery (#143, #137)

2026-09-17, Windows chonkers, owner-attended, GitWizardUI Release on main `b07960d` with MFTLib `d64efd0`. PASS with findings.

- Cache cleared (`repositories.txt`, `report.json`), broker frame tracing on (`MFTLIB_BROKER_DIAG=1`).
- One elevated broker and one UAC for discovery: ArmAndScan x5 at 09:01:00-09:01:20 UTC.
- Clicking Live sent StartWatch only (09:03:14 UTC): no second UAC, no second ArmAndScan, indicator amber then green.
- Live edit of a tracked scratch repo refreshed it within a second and it turned dirty. Live delete of a tracked root removed it (rename's delete half; also the 2026-09-15 `--watch` smoke).
- Both brokers exited cleanly (EndWatch/EndWatchAck, no `GitWizardUI.exe` left after close).
- Findings: #205 a repo created after Live starts is not tracked for changes; #206 renaming a tracked repo folder drops it; #207 Live stop/start costs a new broker, UAC and cold scan with no progress; #208 other accounts' temp repos flood the log; MFTLib#185 diagnostics log feeds itself.
- Evidence: https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/143#issuecomment-82298

## Commands

```powershell
& 'C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/amd64/MSBuild.exe' external/MFTLib/MFTLibNative/MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal -clp:Summary
dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
dotnet build external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj -c Release -p:Platform=x64
dotnet restore git-wizard.slnx
dotnet build git-wizard.slnx -c Release --no-restore
dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release --nologo
dotnet format git-wizard.slnx --verify-no-changes --no-restore
jb inspectcode git-wizard.slnx -o='.claude/scripts/jb-review.json' --settings='git-wizard.slnx.DotSettings' --severity=WARNING --no-updates --properties:Configuration=Release
aislop ci .
python ci/post-coverage-status.py --cobertura 'GitWizardTests/TestResults/82df5d92-7d4c-46b5-a570-c55aba218e31/coverage.cobertura.xml' --gate-line 55 --summary --skip-post
git diff --check
```
