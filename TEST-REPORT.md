# git-wizard verification report

2026-10-13 UTC, Windows chonkers, Release, non-elevated.

| Field | Value |
| --- | --- |
| Status | PASS for the required Windows non-admin gates |
| Mode | best-effort; configured 55% line coverage floor |
| Git | chore/mftlib-pin-ce407279 based on c107172; MFTLib ce407279; uncommitted migration tree |
| Tests | VSTest: 1640 total, 1637 passed, 0 failed, 3 skipped; TRX: 1685 total, 1637 executed, 1637 passed, 0 failed |
| Coverage | 11951/15957 lines (74.89%); 5040/7440 branches (67.74%); configured 55% line floor held |
| Build | Native DLL, MFTLib, MFTLibTestExtensions, solution: each 0 warnings, 0 errors |
| Lint | Roslyn/build: 0; format: 0; InspectCode: 0; aislop full scan: 100, 0 errors, 1 existing guarded-task-result warning, 22 information findings |
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
pwsh -NoProfile -File external/MFTLib/scripts/build-windows.ps1
& 'C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe' external/MFTLib/MFTLibNative/MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
dotnet restore git-wizard.slnx
dotnet build git-wizard.slnx -c Release --no-restore
dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release --nologo --settings GitWizardTests/coverlet.runsettings --collect:"XPlat Code Coverage" --results-directory C:/Users/mtsch/AppData/Local/Temp/claude/C--Users-mtsch-MFTLib/238041f6-cdde-4285-be03-e4d87b70607d/scratchpad/reports/git-wizard-results-ce407279-20261012-final
dotnet format git-wizard.slnx --verify-no-changes --no-restore
jb inspectcode git-wizard.slnx --output=jb-report.json --settings=git-wizard.slnx.DotSettings --severity=WARNING --no-updates --properties=Configuration=Release --caches-home=inspectcode-cache
python ci/parse-jb-report.py jb-report.json
python ci/post-coverage-status.py --cobertura C:/Users/mtsch/AppData/Local/Temp/claude/C--Users-mtsch-MFTLib/238041f6-cdde-4285-be03-e4d87b70607d/scratchpad/reports/git-wizard-results-ce407279-20261012-final/9b3582fb-77ef-48b6-b70e-c0bc327b786f/coverage.cobertura.xml --gate-line 55 --summary --skip-post
aislop scan .
git diff --check
```

## Windows CI Test step profile (git-wizard issue 291)

2026-10-05 UTC, Windows Chonkers runner and Linux llamabox-ubuntu runner, CI TRX artifacts. Tracking issue: schoen/git-wizard#291 (the Windows Test step takes 115 to 160 seconds). Disposition: incomplete; the issue remains open (pull request 294 addresses it partially).

Test-step measurements (CI Test-step wall clock is recorded separately from TRX start-to-finish and summed test durations):

| Metric | Baseline (CI Run 40056) | After (CI Run 40094) | Delta |
| --- | ---: | ---: | ---: |
| Windows head commit | 1f5b3ac | 3c27223 | - |
| Windows Test step window (UTC) | 18:52:57 - 18:53:57 | 21:05:45 - 21:07:09 | - |
| Windows Test step wall clock | 60 s | 84 s | +24 s |
| Windows TRX start to finish | 58.815 s | 82.466 s | +23.651 s |
| Windows TRX summed test durations | 57.221 s | 80.217 s | +22.996 s |
| Linux Test step wall clock | 29 s | 30 s | +1 s |
| Linux TRX summed test durations | 25.523 s | 25.142 s | -0.381 s |

The Windows step got slower between the two runs. The pull request body attributes this to runner variability and background load on Chonkers (summed durations across all tests rose by about 23 seconds); the change itself removes 3.016 s of real delay from two tests.

Changed tests (per-test durations from the TRX artifacts; the delay in `MainViewModel.Commands.cs` now goes through the injected `TimeProvider` and the tests drive a `ControlledDelayClock`):

| Test | Baseline (Run 40056) | After (Run 40094) | Delta | Class |
| --- | ---: | ---: | ---: | --- |
| CopyToClipboardAsync_WritesToClipboardAndLightsJustCopied | 1.512942 s | 0.001430 s | -1.511512 s | GitWizardTests.MainViewModelCommandAdditionalTests |
| CopyToClipboardAsync_ClearsTheCopiedIndicatorAfterTheDelay | 1.505490 s | 0.000792 s | -1.504698 s | GitWizardTests.MainViewModelTests |
| Total | 3.018432 s | 0.002222 s | -3.016210 s | - |

Local measurement (Windows Chonkers, `dotnet test GitWizardTests/GitWizardTests.csproj --filter FullyQualifiedName~CopyToClipboardAsync`, 2 copied-indicator tests): 3018.4 ms before, 2.2 ms after.

Test-count dispositions (TRX `<Counters>` element of each linked artifact; `dotnet test` console summary line from each job log in parentheses; test project `GitWizardTests`, Release, net10.0, `--no-build`, trx logger; Linux runs also pass `--collect:"XPlat Code Coverage"`; no test was removed or filtered by this change, and the total is 1677 in all four TRX files):

| Run and OS | TRX total | TRX executed | TRX passed | TRX failed | Console line (job log) |
| --- | ---: | ---: | ---: | ---: | --- |
| Baseline Run 40056, Windows (Job 118483, head 1f5b3ac) | 1677 | 1629 | 1629 | 0 | Passed: 1629, Skipped: 3, Total: 1632, Failed: 0 |
| After Run 40094, Windows (Job 118660, head 3c27223) | 1677 | 1629 | 1629 | 0 | Passed: 1629, Skipped: 3, Total: 1632, Failed: 0 |
| Baseline Run 40056, Linux (Job 118482) | 1677 | 1658 | 1658 | 0 | Passed: 1658, Skipped: 6, Total: 1664, Failed: 0 |
| After Run 40094, Linux (Job 118659) | 1677 | 1658 | 1658 | 0 | Passed: 1658, Skipped: 6, Total: 1664, Failed: 0 |

- Not executed: the TRX files carry 48 `NotExecuted` results on Windows and 19 on Linux (the `notExecuted` counter attribute reads 0 in every file). The console lines report 3 skipped tests on Windows and 6 on Linux. The gap between the TRX total (1677) and the console total (1632 Windows, 1664 Linux) is not explained by either source and its cause was not investigated. Baseline and after counts are identical on each OS, so the change did not alter the number of tests run.
- Runners: Windows Chonkers (`Chonkers\gitea-runner`), Linux llamabox-ubuntu container.

Quality checks (CI Run 40094 for the after state, head 3c27223; every step concluded `success` per the Gitea Actions jobs API, and the jobs and runs concluded `success`):

| Check | Where | Outcome |
| --- | --- | --- |
| Build with analyzers (`TreatWarningsAsErrors`) | Windows Job 118660, step "Build (full solution)": 0 Warning(s), 0 Error(s). Linux Job 118659, step "Build (cross-platform projects)": 0 Warning(s), 0 Error(s) for each of four projects | passed |
| Format (`dotnet format git-wizard.slnx --verify-no-changes`) | Linux Job 118659, step "Format check (dotnet format --verify-no-changes)" | passed |
| InspectCode | Windows Job 118660, step "JetBrains InspectCode (deep lint gate)": log line `jb inspectcode: 0 finding(s)` and `jb inspectcode gate passed (0 findings).` | passed, 0 findings |
| aislop | Run 40093 (workflow `aislop.yml`), Job 118658 `quality-gate`, step "aislop quality gate": log line `aislop score 100 over 320 files` | passed, score 100 |

The baseline Run 40056 (and its aislop Run 40055, Job 118481) also concluded `success` for the same steps: Windows InspectCode `jb inspectcode: 0 finding(s)`, aislop `aislop score 100 over 319 files`. A final-head CI Run 40126 and aislop Run 40125 (head 2c3bd2d, the commit that precedes this evidence record) also concluded `success`; their logs were not read.

Coverage (Linux, Run 40094, Job 118659, steps "Post coverage status" and "Coverage gate"; collector `XPlat Code Coverage` with coverlet.collector 6.0.4 and `GitWizardTests/coverlet.runsettings`): log line `coverage gate passed: 88.96% line >= 55.0% threshold`. The Cobertura artifact `coverage-cobertura` reports line rate 0.8896 (7390 of 8307 lines) and branch rate 0.8339 (3655 of 4383 branches): https://gitea.fleet.sticktoitive.net/schoen/git-wizard/actions/runs/40094/artifacts/coverage-cobertura. Baseline Run 40056 (Job 118482): 89.0% line, line rate 0.8899 (7392 of 8306 lines), branch rate 0.8345 (3658 of 4383 branches), gate passed at the same 55% threshold: https://gitea.fleet.sticktoitive.net/schoen/git-wizard/actions/runs/40056/artifacts/coverage-cobertura. The Windows job does not collect coverage, so no Windows coverage figure exists for either run.

Full-step target: incomplete. Individual-test reductions do not establish a full-step improvement under runner variability, and the required reduction of the Windows Test step has not been demonstrated on CI (60 s baseline against 84 s after). Issue 291 stays open.

Evidence:

- Baseline CI Run 40056: https://gitea.fleet.sticktoitive.net/schoen/git-wizard/actions/runs/40056
- Baseline Windows Job 118483: https://gitea.fleet.sticktoitive.net/schoen/git-wizard/actions/runs/40056/jobs/118483
- Baseline Windows TRX artifact: https://gitea.fleet.sticktoitive.net/schoen/git-wizard/actions/runs/40056/artifacts/test-results-windows
- Baseline Linux Job 118482: https://gitea.fleet.sticktoitive.net/schoen/git-wizard/actions/runs/40056/jobs/118482
- Baseline Linux TRX artifact: https://gitea.fleet.sticktoitive.net/schoen/git-wizard/actions/runs/40056/artifacts/test-results-linux
- After CI Run 40094: https://gitea.fleet.sticktoitive.net/schoen/git-wizard/actions/runs/40094
- After Windows Job 118660: https://gitea.fleet.sticktoitive.net/schoen/git-wizard/actions/runs/40094/jobs/118660
- After Windows TRX artifact: https://gitea.fleet.sticktoitive.net/schoen/git-wizard/actions/runs/40094/artifacts/test-results-windows
- After Linux Job 118659: https://gitea.fleet.sticktoitive.net/schoen/git-wizard/actions/runs/40094/jobs/118659
- After Linux TRX artifact: https://gitea.fleet.sticktoitive.net/schoen/git-wizard/actions/runs/40094/artifacts/test-results-linux
