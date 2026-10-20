# Manual test plan

Attended verification steps that need a real Windows machine, a real elevation
prompt, or a real desktop session, and so cannot run on CI. Run the CLI pass
below first, before any GitWizardUI (desktop) testing - it exercises the same
discovery and watch machinery with far less setup, gates on the `ready:` marker,
and its known gaps usually explain UI-level symptoms before they are chased there.

The drivers are [maintained developer tools](maintained-developer-tools.md).
CI checks their syntax and isolated helper logic. Those checks do not replace
the attended elevation, journal, and desktop procedures below.

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
| 1 | Start `git-wizard --watch -v` | The banner appears, then one ready: line (mode=journal) within -StartupTimeoutSeconds. The broker child is recorded as evidence only. | One UAC prompt; the script prints the last startup:/drive: line if the client exits or times out. |
| 2 | Edit `repo-a` (append to the tracked file) and commit | Expect `changed: ...\repo-a`. | The 30-second window begins after ready:. On the machine that produced the original report the edit arrived late, not lost: the changed: line was stdout line 6, printed before step 3's created: line at line 8, because the previous step 1 gate (broker process existence) preceded catch-up. A miss is a delivery-latency finding first; treat it as a gw#219 discovery regression only when the line never appears in the whole log. |
| 3 | Create `repo-b` during the watch, then edit it | Expect `created: ...\repo-b`, then `changed: ...\repo-b`. | Fixed by [gw#205](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/205). |
| 4 | Rename the tracked `repo-b` to `repo-b2`, then edit it | Expect `deleted: ...\repo-b`, then `created: ...\repo-b2`, then `changed: ...\repo-b2`. | Depends on step 3 having made `repo-b` tracked first. Covered by [gw#206](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/206) and [gw#209](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/209). |
| 5 | Delete `repo-b2` | Expect `deleted: ...\repo-b2`. | |
| 6 | Stop the client (`Stop-Process -Force`) | The broker exits within 10 seconds, and no new Windows Application event-log Error or Critical entry mentions `git-wizard` since the watch started. | |
| 7 | Cleanup | Scratch root removed; PASS/FAIL summary table printed with the evidence directory path. | |

Actions are spaced roughly 8 seconds apart so the watch stream does not
coalesce distinct filesystem events. Pass/fail for each step comes from
polling the redirected stdout log for the expected line against a deadline,
never from a fixed sleep.

### Start-to-caught-up timing (record on every attended pass)

Fill one row per run from the `-v` timestamps:
- `start` is the banner line's stamp from `GitWizardLog` (`[yyyy-MM-dd|HH:mm:ss.ffff] - Info: Session Start Message`).
- `ready` is the `ready:` line's stamp in ISO-8601 UTC format (`yyyy-MM-ddTHH:mm:ss.fffZ ready: ...`). Note that the session banner and `ready:` lines use different timestamp formats (GitWizardLog's bracketed UTC format vs. the watch line writer's standard ISO-8601 format), so calculating start-to-ready duration requires converting between the two rather than reading them as identically formatted strings.
- `drives` are the `caught-up=` list.
- `origin` per drive comes from the last `drive: <V> state=caught-up origin=<warm|cold>` line.
UI rows come from the toolbar text and a wall clock. Automated gates do not substitute for these values.
The UI rows are measured with `scripts/ui-live-timing.ps1`, which drives GitWizardUI through UI
Automation, presses Live, and stamps toolbar phase transitions the same way the CLI rows are driven
by `scripts/cli-live-smoke.ps1`. Run it once for each kind after a Release build:
`.\scripts\ui-live-timing.ps1 -Kind cold -UiExe <path to GitWizardUI.exe> -CliExe <path to git-wizard.exe>`,
then again with `-Kind warm` immediately after, reusing the same executables so the second run keeps
the cache from the first.

| Client | Start kind | Drives (origin) | Start stamp | Ready stamp | Start-to-ready | Notes |
|---|---|---|---|---|---|---|
| CLI | cold (index cache cleared with `--clear-index-cache`) | C, D, E, F, G (all cold) | `[2026-09-24\|06:45:39.1169]` | `2026-09-24T06:46:42.380Z` | 63.3 s (script: 64 s from process start) | chonkers, PR 264 head dea6a2b, 2026-09-23 23:45 PDT. Scanning C through G took 48 s (06:45:40 to 06:46:28), catch-up under 1 s, discovery plus reconciliation 13 s. Step 2 `changed:` arrived at 06:46:44.217Z, 1.8 s after the marker; all seven steps passed. The machine was not quiet: a pr-crew drain, a codex lane and dotnet build nodes were running, so treat these as loaded-machine numbers, not a baseline. Evidence: `~/git-wizard-cli-smoke/cold-20260923-234538`. |
| CLI | warm (second run, cache retained) | C, D, E, F, G (all warm) | `[2026-09-24\|06:47:41.5953]` | `2026-09-24T06:48:12.015Z` | 30.4 s (script: 31 s from process start) | Same machine, run immediately after the cold pass. The UAC approval accounted for 11.8 s (requesting-elevation 06:47:45.0 to broker-connected 06:47:56.8), catch-up 0.4 s, discovery plus reconciliation 14.4 s. Step 2 `changed:` arrived at 06:48:12.163Z, 0.15 s after the marker; all seven steps passed. Evidence: `~/git-wizard-cli-smoke/warm-20260923-234741`. |
| UI | cold | C, D, E, F, G (cache cleared with `--clear-index-cache` immediately before the Live press) | `2026-09-24T07:02:46.385Z` (Live pressed) | `2026-09-24T07:04:31.508Z` (toolbar read "Ready (journal): C, D, E, F, G") | 105.1 s | chonkers, PR 264 head 453197d, 2026-09-24 00:02 PDT, Release build of GitWizardUI. Live pressed and toolbar text read through UI Automation, stamps from the driver's wall clock polled every 250 ms. Toolbar phases: Requesting elevation 07:02:49, Broker connected 07:02:53, Scanning D through G 07:03:11 to 07:03:52, Catching up journal 07:03:52, Discovering 07:03:55, Reconciling 07:03:59. The startup refresh (2 m 44 s) finished before the press and is not counted. Machine under load, see the CLI rows. |
| UI | warm | C, D, E, F, G (cache retained from the cold run) | `2026-09-24T07:06:43.499Z` (Live pressed) | `2026-09-24T07:07:19.929Z` (toolbar read "Ready (journal): C, D, E, F, G") | 36.4 s | Same machine and build, run immediately after the cold pass. Toolbar phases: Opening index 07:06:46, Requesting elevation 07:06:47, Broker connected 07:06:48, Discovering 07:06:50, Reconciling 07:06:53; no scanning phase and no catching-up text appeared, the warm blocks caught up within the poll interval. Reconciliation (27 s) dominated. A repeat warm pass a few minutes later, run to measure the toolbar layout fix, took 117.8 s with a 99 s reconciliation on the same loaded machine, so expect wide variance in this row until it is measured on a quiet box. That pass also confirmed through UI Automation that the readiness line and all five per-drive entries end inside the window (rightmost right edge 2121 px against a window right edge of 2245 px). |
| UI | cold | C, D, E, F, G (all cold; cache cleared with `--clear-index-cache` immediately before the Live press) | `2026-10-02T10:05:01.791Z` (Live pressed) | `2026-10-02T10:05:31.139Z` (toolbar read "Ready (journal): C, D, E, F, G") | 29.3 s | chonkers, main c616823 (MFTLib pin d8439ab), 2026-10-02 03:05 PDT, Release build of GitWizardUI, no builds or agent lanes running. Toolbar phases: Requesting elevation 10:05:04, Broker connected 10:05:06, Discovering 10:05:27, Ready 10:05:31; the per-drive tooltip read "caught up (cold)" for all five drives. The UAC approval (about 2 s) is inside the interval. The startup refresh (1 m 32 s) finished before the press and is not counted. The script's layout check failed after the timing result: the readiness text element's right edge was 1 px past the window at full width (2026 against 2025) and 2 px past at 1100 px (1178 against 1176). |
| UI | warm | C, D, E, F, G (cache retained from the cold run) | `2026-10-02T10:07:09.565Z` (Live pressed) | `2026-10-02T10:07:19.436Z` (toolbar read "Ready (journal): C, D, E, F, G") | 9.9 s | Same machine and build, run immediately after the cold pass. Toolbar phases: Opening index 10:07:11.3, Requesting elevation 10:07:11.6, Catching up C through G 10:07:11.7, Discovering 10:07:13, Reconciling 10:07:16; no scanning phase. Reconciliation took 3.3 s. The layout check failed the same way (2246 against 2245, and 1398 against 1396). |

The attended pass must include a machine whose cold start exceeds 30 seconds and must show step 2
passing after the marker on that machine. In automated CI or unattended harness environments (where
interactive UAC approval, GUI wall-clock timing, and machines with >30s cold starts are not present),
this table template is preserved for the operator's attended verification pass.

### Single-line Live readiness

Run the CLI live smoke first, then perform the desktop pass on an attended build.
Record build identity, OS/display scale, actual window width, and screenshots or
UI Automation bounds. Do not replace this check with a headless-layout result.

1. Open Index Cache Status before starting Live. Keep it open through startup.
2. With five configured volumes where available, observe startup/catching up,
   then the caught-up steady state. The toolbar contains one readiness text
   and zero per-drive entries at every transition.
3. At 1925, 1100, and 1076 physical pixels, use UI Automation to record the
   readiness text's bounding rectangle and count visible readiness rows. Expect
   one row in the steady state and no extra rows as drives change state. Long
   summary text may ellipsize; it must not wrap or overflow the client area.
4. Hover over the readiness text. Compare each tooltip line with the Index Cache
   Status window: volume, state, origin, and reason must agree. The panel must
   update without pressing Refresh status, including when opened before Live.
5. During an available excluded/faulted-drive scenario, confirm the summary
   names the excluded drive, the detail includes its reason, and toolbar height
   stays unchanged. Do not deliberately damage a journal to produce a fault.
   If no safe attended failure scenario is available, record that case as not
   exercised; deterministic tests cover the state transition.
6. Stop and restart Live with the panel open. Stop clears the details; restart
   does not retain old drives/reasons. Closing and reopening the panel shows
   current rows immediately. Repeat the binding check on Linux when available;
   an MFT-not-applicable message must not hide Linux Live readiness details.

### Live watch state transitions and history

After the CLI live smoke, perform the desktop pass on an attended build:

1. Run `scripts/cli-live-smoke.ps1` before desktop checks; preserve the emitted evidence.
2. Start desktop Live and open Index Cache Status. Verify current readiness and recent events are separate and the toolbar stays one line at minimum width.
3. During an observed real recoverable watch failure, verify Faulted then Recovering remain visible after readiness returns to CaughtUp. Do not kill the broker to simulate a recoverable drive fault; broker death is terminal channel loss.
4. Verify recent entries disappear after their retention interval while current readiness stays visible, and that closing the dialog does not interrupt Live.
5. Explicitly stop Live; verify history clears and delayed dispatcher/timer callbacks do not repopulate it. Restart Live and confirm new low version numbers are accepted.

If no real recoverable failure can be induced safely, report that attended item as unexercised; the scripted integration tests are its deterministic verification. Do not claim an attended pass from a headless layout test.

A cache-only block with an unresumable checkpoint is rescanned before its drive is watched, and a drive already scanned at open is not scanned again. MFTLib stops retrying a drive after three consecutive catch-up losses, and Live then shows that drive as excluded; the scripted integration tests are the deterministic verification of both.

### Gotchas

- **Output format:** `--watch` prints one line per event: `created: <path>`,
  `changed: <path>`, or `deleted: <path>` (see
  `git-wizard/Program.Watch.cs`'s `FormatChangeEvent`). The session-started
  banner is printed once at startup, before the broker is even launched.
- **Readiness has an explicit marker.** ready: (README grammar) is the gate;
  ParentProcessId matching remains a diagnostic for locating the broker child.
- **Noisy stream from other accounts.** On a shared or CI-active machine,
  other users' repositories (notably an `act_runner` checkout's `.cache`
  during Gitea Actions runs) will flow through the same watch stream. Filter
  to the scratch root rather than editing `~/.GitWizard/config.json`'s
  `ignoredPaths` - that keeps the real configuration untouched and avoids a
  cleanup step.
- **Step 2 is the regression check for gw#219 and must pass.** The 30-second
  window begins after ready:. On the machine that produced the original report
  the edit arrived late, not lost: the changed: line was stdout line 6, printed
  before step 3's created: line at line 8, because the previous step 1 gate
  (broker process existence) preceded catch-up. A miss is a delivery-latency
  finding first; treat it as a gw#219 discovery regression only when the line
  never appears in the whole log.
- **One broker per machine, effectively.** Running the CLI watch and
  GitWizardUI's Live mode at the same time is out of scope for this smoke
  test; close GitWizardUI first.

## Discovery outcomes and failure semantics

Run the existing CLI live smoke pass first. These checks exercise the discovery
coordinator's publication contract attended, because they need real elevation
and a real drive mix.

1. **Partial discovery keeps watching healthy drives.** With search paths on two
   NTFS drives, make one drive's index unavailable (for example keep an external
   index owner open on it). Run `git-wizard --watch`. Expect a scan-error line
   for the failed drive, healthy-drive repositories still discovered, and
   `changed:` events flowing for a healthy-drive edit. A full report run in the
   same state must exit 3 and leave `~/.GitWizard/report.json` byte-identical;
   a `--save-path` file must not be created. An explicit `--paths` report in the
   same state is unaffected.
2. **Complete-empty discovery publishes empty.** With a redirected
   `GITWIZARD_HOME` whose search path contains no repositories, run the default
   report. Expect exit 0, an empty report, and `repositories.txt` cleared.
3. **`--no-mft` is explicit recursive discovery.** Run
   `git-wizard --scan-only --no-mft`. Expect recursive directory discovery with
   no UAC prompt and no broker child process. The help text for `--no-mft`
   (root, `tags`, and `branches`) states this contract, including that it does
   not promise an unelevated NTFS journal watch.
4. **Desktop Shift+Refresh preserves prior state on failure.** With a populated
   list and report, make discovery unusable (deny the broker at the UAC prompt),
   then Shift+click Refresh. Expect an error header, the previous repository
   rows still displayed, and `repositories.txt`/`report.json` unchanged. Repeat
   with a successful scan and confirm the fresh inventory replaces the display.
5. **Linux per-start crawl.** On a Linux desktop, start Live and `--watch` once
   each. Expect one recursive discovery pass per start after the fanotify marks
   install, with events queued during the crawl delivered afterward; no MFT
   cache is created under `~/.GitWizard`.

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
