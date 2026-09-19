# Manual test plan

Attended verification steps that need a real Windows machine, a real elevation
prompt, or a real desktop session, and so cannot run on CI. Run the CLI pass
below first, before any GitWizardUI (desktop) testing - it exercises the same
discovery and watch machinery with far less setup, and its known gaps
(gw#219, and anything else it turns up) usually explain UI-level symptoms
before they are chased there.

## CLI live smoke (run first)

Scripted as `scripts/cli-live-smoke.ps1`. It drives a real `git-wizard --watch`
session against scratch repositories, prints PASS/FAIL per step with an
evidence line, and writes logs plus a summary to an evidence directory. It
needs an interactive session: `--watch` self-elevates one broker child
process, which means exactly one UAC prompt for the whole run. The script
prints a reminder immediately before it expects that prompt.

### Prerequisites

- A Release build: `.\init.ps1 -Build` from the repository root (builds MFTLib
  native + managed, then the git-wizard solution). The script's default
  `-CliPath` points at `git-wizard/bin/Release/net10.0/git-wizard.exe`.
- GitWizardUI closed. The desktop app and the CLI both dispatch the same
  elevated broker; running both at once is untested and adds a second broker
  to the machine's process tree that the script's parent-PID matching does
  not expect.
- A git-wizard cached repository list must already exist at
  `~/.GitWizard/repositories.txt` (run `git-wizard` once, or run the
  GitWizardUI app once and close it, on a first-time machine setup). Step 2
  below depends on this: it reproduces gw#219 by editing a repository created
  *after* that cache was last written, so an empty cache would make the step
  vacuous rather than a real reproduction.
- Nothing here edits `~/.GitWizard/config.json`. Other accounts' repositories
  (for example `C:\Users\gitea-runner\.cache\act` during CI activity on the
  same machine) will still appear in the raw watch output; the script filters
  its own pass/fail checks to lines mentioning the scratch root instead of
  adding `ignoredPaths` entries, so nothing about the real configuration
  needs to be touched or restored afterward.

### Running it

```powershell
.\scripts\cli-live-smoke.ps1                 # full run (one UAC prompt)
.\scripts\cli-live-smoke.ps1 -DryRun         # print the resolved plan only, touch nothing
```

Parameters: `-CliPath`, `-ScratchDrive` (default `C`), `-UacTimeoutSeconds`
(default 300), `-EvidenceDirectory` (default
`%USERPROFILE%\git-wizard-cli-smoke\<yyyyMMdd-HHmmss>`). The scratch
repositories themselves live under
`<ScratchDrive>:\gw-cli-smoke-<yyyyMMdd-HHmmss>` and are removed at the end of
the run regardless of outcome.

The script exits non-zero if any step fails, including the currently-expected
gw#219 failure in step 2 - see below.

### Steps and expectations

| # | Step | Expectation | Notes |
|---|------|-------------|-------|
| 0 | Preflight | Scratch root created; `repo-a` created with `git init` and one commit (using `-c user.name`/`-c user.email` so the run needs no global git identity), all **before** the watch starts. | |
| 1 | Start `git-wizard --watch -v` | The session-started banner ("git-wizard Session Started") appears on stdout, and the elevated broker child appears: another `git-wizard.exe` process whose `ParentProcessId` is the client's PID. Its command line is hidden by elevation, which is why parent-PID matching (via `Win32_Process`/CIM) is used instead of matching on arguments. | One UAC prompt for the whole run. |
| 2 | Edit `repo-a` (append to the tracked file) and commit | Expect `changed: ...\repo-a`. | **Currently FAILS by design** of [gw#219](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/219): `--watch` only tracks the cached repository list, so a repository created after the last discovery but before the watch started is invisible to it. Report this as a real FAIL with the issue link - do not mark it expected-fail or skip it. |
| 3 | Create `repo-b` during the watch, then edit it | Expect `created: ...\repo-b`, then `changed: ...\repo-b`. | Fixed by [gw#205](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/205). |
| 4 | Rename the tracked `repo-b` to `repo-b2`, then edit it | Expect `deleted: ...\repo-b`, then `created: ...\repo-b2`, then `changed: ...\repo-b2`. | Depends on step 3 having made `repo-b` tracked first. Covered by [gw#206](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/206) and [gw#209](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/209). |
| 5 | Delete `repo-b2` | Expect `deleted: ...\repo-b2`. | |
| 6 | Stop the client (`Stop-Process -Force`) | The broker exits within 10 seconds, and no new Windows Application event-log Error or Critical entry mentions `git-wizard` since the watch started. | |
| 7 | Cleanup | Scratch root removed; PASS/FAIL summary table printed with the evidence directory path. | |

Actions are spaced roughly 8 seconds apart so the watch stream does not
coalesce distinct filesystem events. Pass/fail for each step comes from
polling the redirected stdout log for the expected line against a deadline,
never from a fixed sleep.

### Gotchas

- **Output format:** `--watch` prints one line per event: `created: <path>`,
  `changed: <path>`, or `deleted: <path>` (see
  `git-wizard/Program.Watch.cs`'s `FormatChangeEvent`). The session-started
  banner is printed once at startup, before the broker is even launched.
- **Broker discovery has no explicit marker.** The broker child is just
  another `git-wizard.exe` process; the only reliable way to find it
  non-elevated is `ParentProcessId` via `Get-CimInstance Win32_Process`, since
  its elevated command line cannot be read from an unelevated session.
- **Noisy stream from other accounts.** On a shared or CI-active machine,
  other users' repositories (notably an `act_runner` checkout's `.cache`
  during Gitea Actions runs) will flow through the same watch stream. Filter
  to the scratch root rather than editing `~/.GitWizard/config.json`'s
  `ignoredPaths` - that keeps the real configuration untouched and avoids a
  cleanup step.
- **gw#219 is a known, currently-real failure**, not flakiness. If step 2
  ever passes, treat that as news (the issue may have been fixed) and
  re-verify against the tracker before assuming the smoke test itself is
  broken.
- **One broker per machine, effectively.** Running the CLI watch and
  GitWizardUI's Live mode at the same time is out of scope for this smoke
  test; close GitWizardUI first.
