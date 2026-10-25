# Maintained developer tools

Only redundant code is deleted. A developer tool is not redundant because
nothing in production or CI calls it. If a tool has no CI use, add a CI check
instead of deleting it.

The table is checked against every `.ps1`, `.sh`, and `.py` below `scripts/`,
including nested directories. Add a row whenever a tool is added, and remove
the row only when an approved redundant tool is removed. Each row records its
purpose, operator, and executable CI check.

| Tool | Purpose | Who runs it | CI coverage |
| --- | --- | --- | --- |
| `scripts/update-screenshots.ps1` | Regenerate the Avalonia screenshot from three synthetic repositories | Maintainers and Windows CI | PowerShell parser; Generate headless screenshot with 180-second capture timeout; required GitWizardUI-screenshot artifact |
| `scripts/cli-live-smoke.ps1` | Verify elevated CLI watch behavior against scratch repositories | Attended Windows operator | PowerShell parser; ci/script-tests/cli-live-smoke.tests.ps1 |
| `scripts/ui-live-timing.ps1` | Measure Live readiness and check toolbar layout | Attended Windows operator | PowerShell parser; ci/script-tests/ui-live-timing.tests.ps1 |
| `scripts/run-coverage.ps1` | Run local NUnit coverage with optional attended elevated tests | Maintainers | PowerShell parser |
| `scripts/run-preview.ps1` | Download and launch a published desktop preview | Maintainers on Windows or Linux with PowerShell | PowerShell parser |
| `scripts/run-preview.sh` | Download and launch a published Linux desktop preview | Linux maintainers | bash -n |
| `scripts/verify-windows.sh` | Run remote Windows non-admin verification | Maintainers with an authorized Windows host | bash -n; ci/test_verify_windows.py |
| `scripts/format-on-save.py` | Apply C# whitespace formatting after edits | Claude/AGY hooks and maintainers | python -m py_compile; ci/test_format_on_save.py |
| `scripts/generate-test-report.ps1` | Generate the deterministic CI summary from TRX and Cobertura artifacts | CI publish-report and maintainers with an explicit output path | PowerShell parser; ci/script-tests/generate-test-report.tests.ps1 |

## Dispatcher watchdog

`GitWizardUI/Services/DispatcherWatchdog.cs` measures synchronous dispatcher
callback time and writes one `Trace.WriteLine` warning for each callback over
500 ms. Measurement and Trace warnings are active in both Debug and Release.
Async dispatcher callbacks measure only the synchronous segment before the
first await, so dialog waits are not counted as UI stalls. Attended
`scripts/ui-live-timing.ps1` runs can read the `[DispatcherWatchdog]` Trace
output. CI gates only the deterministic `DispatcherWatchdogTests` using
`ControlledDelayClock`, never real wall-clock callback durations.

Run `bash tools/ci-preflight.sh --only contracts` for the shared contract
suite. Linux checks inventory, Bash/Python syntax, and Python behavior.
PowerShell checks run on Linux when pwsh is available; otherwise they are
explicitly skipped there. Windows CI requires PowerShell 7 and checks every
PowerShell tool before running each helper self-test in a separate process.
Missing required interpreters fail their host's gate.

`-FunctionsOnly` on the CLI-smoke and UI-timing scripts loads reusable logic
without starting attended work. These tests do not establish real UAC,
journal, desktop, remote-host, registry-download, or coverage behavior.
The attended procedures remain in [the manual test plan](manual-test-plan.md).

The screenshot wrapper redirects GITWIZARD_HOME to a three-repository fixture
and sets GITWIZARD_NO_MFT=1. Both screenshot workflows use it, impose a
10-minute step deadline in addition to its capture timeout, and upload its
nonempty PNG. Never run screenshot capture against real repositories.

## CLI ordering checks

The CLI ordering checks in `git-wizard/UpdateHandler.cs` diagnose callback ordering:
`[SKIPPED] Submodule parent not created yet`, `[SKIPPED] Uninitialized submodule parent
not created yet`, and `[MISSING] Refresh completed but item not created`. They are
collected even in silent runs, shown after the human run summary, and serialized in
`run.warnings` for JSON and saved reports. `UpdateHandlerTests` exercises each check;
`MultipleProgress_Updates_AreThreadSafe` additionally guards progress-lock ownership
under concurrent updates. These diagnostics are maintained runtime tooling, not dead
code merely because their consumer is the run summary.
