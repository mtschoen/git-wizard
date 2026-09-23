# Manual test plan

Attended verification steps that need a real Windows machine, a real elevation
prompt, or a real desktop session, and so cannot run on CI. Run the CLI pass
below first, before any GitWizardUI (desktop) testing - it exercises the same
discovery and watch machinery with far less setup, and its known gaps
(including the stale-cache startup regression) usually explain UI-level symptoms
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
  below depends on this: it guards against gw#219 by editing a repository created
  *after* that cache was last written, so an empty cache would make the step
  vacuous rather than a real reproduction.
- The selected scratch root must be under a configured search path and outside
  ignored paths. Fresh startup discovery applies the configured scope; a scratch
  repository outside that scope is not an eligible stale-cache regression fixture.
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

The script exits non-zero if any step fails. Step 2 must pass: the CLI discovers
repo-a at startup even though repo-a was created after the cache was last written.

### Steps and expectations

| # | Step | Expectation | Notes |
|---|------|-------------|-------|
| 0 | Preflight | Scratch root created; `repo-a` created with `git init` and one commit (using `-c user.name`/`-c user.email` so the run needs no global git identity), all **before** the watch starts. | |
| 1 | Start `git-wizard --watch -v` | The session-started banner ("git-wizard Session Started") appears on stdout, and the elevated broker child appears: another `git-wizard.exe` process whose `ParentProcessId` is the client's PID. Its command line is hidden by elevation, which is why parent-PID matching (via `Win32_Process`/CIM) is used instead of matching on arguments. | One UAC prompt for the whole run. |
| 2 | Edit `repo-a` (append to the tracked file) and commit | Expect `changed: ...\repo-a`. | Regression for [gw#219](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/219): repo-a postdates the cached list but exists before startup discovery. It must be tracked without a rename. Report missing events as a real FAIL. |
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
- **Step 2 is the regression check for gw#219 and must pass.** The watch
  rediscovers repositories on every startup, so a repository created after
  the last cache refresh is tracked before the watch is armed. A failure at
  step 2 is a real regression, not flakiness: capture the watch output and
  reopen the issue.
- **One broker per machine, effectively.** Running the CLI watch and
  GitWizardUI's Live mode at the same time is out of scope for this smoke
  test; close GitWizardUI first.

## Index cache maintenance

Run the existing CLI live smoke pass first. Then, using an intentionally owned
test home and two synthetic cached drives, keep a FileIndex owner open on one
drive. Run `git-wizard --clear-index-cache --silent` in that same redirected
home. Expect exit 1, one InUse row, one Deleted row, and an inventory containing
the still-owned block. Verify both lock files remain and report/list/config
sentinels are unchanged. Release the owner and retry: expect exit 0 and no
remaining canonical blocks. These steps are automated by the index-cache CLI
process test; do not use the real home for an unattended destructive smoke.

In the desktop at its default 1100x700 size, expand Index Cache in the sidebar.
Inspect, then Clear. Check wrapping/readability of full paths and per-drive
results, that action buttons re-enable, and that the displayed inventory updates
after clear. With a live index, expect InUse without Live stopping. After closing
owners, retry and expect successful deletion. No extra elevation prompt should
appear. Confirm the original Clear Cache remains distinct. Resize narrower and
confirm the panel remains scrollable without widening/clipping the top toolbar.

## Index cache status

Run the existing CLI live smoke pass first on any attended re-test.

1. Run the built CLI with `--index-cache-status` against an isolated `GITWIZARD_HOME` with no cache. Expect explicit empty inventory, exit 0, no `mft-index` directory, no UAC prompt and no repository-list/report creation.
2. Against a known cache, record per-volume fields and a hash of an unlocked canonical test block before/after inventory. Only a persistent lock sibling may be newly present.
3. While the UI holds a canonical test slot, run the CLI action. It must report an unidentified holder; the UI panel may name its own ownership. Refresh after stopping Live and confirm the current-session annotation clears.
4. Use synthetic tests, not a second desktop instance, to demonstrate normal private fallback and cache-only `InUse`. The desktop's existing single-instance mutex intentionally rejects a second UI process.
5. Verify field readability and scrolling at 500-pixel and 850-pixel window widths. Refresh has no elevation prompt. The window has no delete/clear action.
6. On Linux run the same flag and open the panel: not applicable, no Windows-cache initialization. Retain the screenshot/terminal evidence outside the disposable checkout.

## Desktop single instance

Run the existing CLI live smoke pass first, as required by this document.
Use the Release GitWizardUI executable for the remaining checks.

1. Start GitWizardUI normally and leave its window open. Launch that executable
   again from Explorer. Confirm there is still one GitWizard main window and a
   native `git-wizard is already running` notice. Dismiss the notice. The second
   launch must not request UAC, discover repositories or start another watch.
2. With the first window open, use PowerShell to capture a real second-process
   exit: `$second = Start-Process .\GitWizardUI\bin\Release\net10.0\GitWizardUI.exe -PassThru`.
   Dismiss its notice, then run `$second.WaitForExit(); $second.ExitCode`.
   Expected: 1. The first window remains usable.
3. Close the first window normally and launch again. Expect one working window,
   no duplicate notice. Repeat after an abnormal termination of only the owned
   test instance; OS mutex abandonment must not leave a stale-instance block.
4. With the normal UI open, exercise its existing attended Live/cold-discovery
   and Configure Windows Defender checks. The broker and Defender child must
   perform their existing task without an already-running notice or extra main
   window. Never run Defender configuration solely as an unattended unit test.
5. On a Linux desktop, launch the Release executable from two terminals. Expect
   one main window, the second terminal's already-running message and exit 1.
   Close the first and verify a subsequent launch succeeds. Repeat the launch
   check on macOS when an attended machine is available; there is no macOS CI.
6. Where two user sessions are available, verify two different users can each
   open one UI, while the same user in a second login session is refused. Record
   unavailable multi-user/platform checks explicitly rather than claiming them.

Record the build, platform and observed outcomes with the implementation's test
evidence. Mutex admission is not a test of canonical cache ownership.
