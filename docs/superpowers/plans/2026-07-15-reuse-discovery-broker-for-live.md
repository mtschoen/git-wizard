# Reuse Discovery Broker for Live Mode

Status: blocked on an independently reviewed MFTLib scan-to-watch API decision

## Goal

When GitWizardUI has just performed broker-backed MFT discovery because the repository cache was absent or a hard refresh was requested, starting Live should reuse that same armed broker session, scan result, advanced cursors, and catch-up state. It must not show a second UAC prompt or repeat the full MFT scan.

This optimization applies only when the current process actually performed MFT discovery. A normal cached startup has no live broker session and still needs to arm and scan when Live starts.

## Evidence for the current problem

`GitWizardApi.BrokerScanAsync` currently:

1. spawns `JournalBrokerClient`
2. calls `ArmScanAndCatchUpAsync`
3. returns only `scan.Records`
4. disposes the client

Later, `UsnVolumeChangeSource.ArmAndCatchUpAsync` spawns a new client and performs the same scan again. On the six configured volumes, broker diagnostics showed a sequential scan taking roughly 132 seconds. The reduced scan profile prevented the 2 GiB payload overflow but does not eliminate this redundant second scan.

## Repository boundary

Do not invent the ownership abstraction in GitWizard first. Complete and review the MFTLib plan at:

`external/MFTLib/docs/plans/2026-07-15-scan-to-watch-session-api.md`

Then bump the submodule and integrate against the finalized generic API. Keep the MFTLib API commit and GitWizard consumer commit separate.

## Integration design requirements

### Discovery result

Extend the broker-backed discovery path with a rich, transient result that can carry both:

- the scan records used to find repositories
- ownership of the reusable MFTLib broker/session

Preserve the existing public bool-returning discovery API for callers that do not want a retained session. Those callers must continue to dispose the broker promptly. Do not require existing tests or consumers to construct a real broker.

### Report and view-model handoff

Allow a cold-cache `GitWizardReport` generation to return the transient session to `MainViewModel`. Requirements:

- never serialize the session into `report.json`
- transfer ownership atomically and at most once
- dispose an older pending session before replacing it
- do not retain a session from recursive fallback, elevated in-process discovery, `--no-mft`, failed discovery, or cached repository loading
- invalidate and dispose the pending session if search roots or requested volumes change

### Live source reuse

Teach `UsnVolumeChangeSource` to begin from the prepared MFTLib session as well as from its existing broker-launch constructor. On the prepared path it must:

- seed the record-number path index from the existing scan records
- reuse `AdvancedCursors`, `CatchUpEntries`, and per-drive `Errors`
- return the existing cold records from `ArmAndCatchUpAsync` without spawning or scanning again
- start live streaming on the same broker connection
- fall back to one fresh arm/scan if the parked broker died, the journal became invalid, or the requested volume set no longer matches
- remain the single disposal owner after the handoff

The first source created by `LiveWatchController` may consume the prepared session. Any later respawn after broker death must use the ordinary fresh source factory and must never attempt to consume the same session twice.

### Lifecycle

Add an explicit UI shutdown path. A prepared broker may remain parked after discovery if the user never clicks Live, so it must be disposed when:

- the window/application closes
- a new hard discovery replaces it
- configuration invalidates it
- Live start fails terminally

Starting Live remains unavailable while refresh/discovery is active. Stopping Live while arming must continue to cancel and dispose safely.

## Tests required in GitWizard

Add deterministic non-admin tests covering:

- cold broker discovery publishes a reusable session
- cached loading and every non-broker discovery path publish none
- report/session state is transient and one-shot
- first Live start reuses the session with no launcher call and no second arm scan
- existing scan records seed path resolution correctly
- catch-up entries are replayed before live entries
- per-drive scan errors remain visible
- second Live start and controller respawn use a fresh source
- volume mismatch, parked broker death, and journal invalidation perform one safe fresh fallback
- refresh replacement and application shutdown dispose unused sessions exactly once
- Live cancellation during prepared startup remains responsive

Preserve all existing Windows, Linux, analyzer, formatting, coverage, InspectCode, and aislop gates.

## Manual acceptance

Run on Windows with broker diagnostics enabled:

1. Back up or clear `~/.GitWizard/repositories.txt` through the normal hard-refresh workflow.
2. Start GitWizardUI and approve the discovery UAC prompt.
3. Wait for discovery and repository refresh to finish.
4. Click Live.
5. Confirm there is no second UAC prompt and no second `ArmAndScan` frame.
6. Confirm the button transitions from amber to green quickly.
7. Create and delete a file in a tracked repository and confirm its row refreshes.
8. Create/delete or rename a repository where practical and confirm discovery/removal behavior.
9. Close the application without starting Live in a second run and confirm the parked elevated broker exits.

Record before/after timing and relevant broker frame timestamps in `TEST-REPORT.md`.

## Non-goals for this change

- Persisting the MFT path index or broker state across application processes
- Parallelizing per-volume scans
- Moving reduced-profile filtering into native enumeration
- Changing Linux fanotify work
- Changing the normal cached-start Live behavior

Those may be worthwhile follow-ups, but they should not muddy the scan-to-watch ownership fix.
