# git-wizard verification report

2026-10-18 UTC, Windows Chonkers, Release, non-elevated.

| Field | Value |
| --- | --- |
| Status | Local Windows gates passed after removing one unused import |
| Mode | best-effort; configured 55% line coverage floor |
| Git | Base 6e63a2db543bc80fefcf46a5b16e5600a49ea84e; formatter commit aea2d05 plus issue 347 source changes |
| MFTLib | b458b84232c416c63cf66d0853b01871b244ff70, unchanged |
| Tests | Full NUnit: 1668 passed, 0 failed, 3 reported skips; TRX also inventories 49 unexecuted platform/elevation/explicit entries; broad targeted: 418 passed; common coverage subset: 185 passed |
| Coverage | 12147/16023 lines (75.80%); 5144/7468 branches (68.88%); Windows skips Linux coverage enforcement |
| Build | Release solution: 0 warnings, 0 errors |
| Lint | Roslyn/build: 0; format: 0; InspectCode: 0; pinned aislop: 100 over 326 files; installed scan: 0 errors, 0 warnings, 0 fixable |
| Preflight | Native, restore, format, build, inspect, test, contracts and pinned aislop passed; 12 Python contracts passed |
| Exclusions | No coverage exclusions, analyzer suppressions or rule changes added |

## Verification

The full suite ran through the test gate in `bash tools/ci-preflight.sh --all`.
`FORCE_COLOR=0` and an external `CI_RESULTS_DIRECTORY` were supplied. The initial
complete preflight found one redundant import in the extracted entry-point test file.
After removing it, the complete preflight was repeated on unchanged executable source.

```text
Passed!  - Failed:     0, Passed:  1668, Skipped:     3, Total:  1671, Duration: 2 m 10 s - GitWizardTests.dll (net10.0)
jb inspectcode: 0 finding(s)
aislop score 100 over 326 files
```

The installed scanner also runs as `aislop scan .` and `aislop scan --staged`.
Its checkout predates the gate pin; the preflight separately uses the current pinned
scanner. Six informational punctuation observations remain in untouched
`.gitea/workflows/preview.yml` and `docs/report-schema.md`.

## Changed-code coverage

The comparable targeted baseline had 171 tests. Its final counterpart has 185:
ten reflected formatter cases became 25 direct cases, and one duplicate constant-array
assertion was deleted. Configuration cache reset: 3/3 lines; report cache reset: 3/3;
watch subscription observations and controller service accessor: 4/4; shared byte
formatter: 14/14. The broader 418-test run covers every new executable production line.
Existing helper coverage was preserved while duplicate formatter lines were removed.

The external implementation report retains per-file before/after counts, TRX files,
both preflight rounds and their logs. The initial baseline is a targeted subset;
it is not a full-suite baseline.

## Verification limits

Linux/macOS execution, attended desktop/elevation checks, real broker/journal integration
and the explicit respawn stress test were not exercised. The Windows test adapter reports
three skips and additionally records platform/elevation/explicit exclusions as unexecuted
TRX entries. No remote CI result is claimed here.

README, AGENTS.md and report-schema documentation were checked. The public formatter XML
comments describe its compact invariant and current-culture UI contracts.
