# Watch-in-UI Session Handoff

## Current Windows state

`feat/watch-in-ui` now has additional validated but uncommitted Windows work on top of
`022d83d`. Live mode correctly refreshes tracked repositories for ordinary file creation and
deletion, uses a relevant-event fixed debounce window, requests MFTLib's reduced directory +
`.git` broker profile to avoid the 2 GiB C: payload overflow, and shows amber `Starting Live...`
until every requested volume has armed. Green `Live` means the watch is active, and clicking
again during startup cancels it.

Validation completed this session: Release build clean, 914 non-admin tests passed with two
platform/admin skips, three elevated broker tests passed, formatting clean, aislop 100/100,
coverage 85.58%, and a real CLI create/delete smoke emitted the expected repository change.
The user also manually confirmed the UI reaches green, but broker diagnostics showed the six
volumes arming sequentially in roughly 132 seconds.

The next optimization is deliberately split across repositories. MFTLib must first receive an
independent scan-to-watch session API review; GitWizard then consumes the finalized API so a
cold-cache discovery broker is not discarded and rescanned. See:

- `external/MFTLib/docs/plans/2026-07-15-scan-to-watch-session-api.md`
- `docs/superpowers/plans/2026-07-15-reuse-discovery-broker-for-live.md`

## Reviewed branches awaiting integration

### Linux path semantics

- Branch: `openai/watch-ui-linux-watch-tests`
- Location: llamabox worktree and local remote-tracking ref
- Commit: `c9d34e8120a5282061a8690824149a2226bfa29d`
- Base: `772a3b0a15c60f16a05ee45c5a4fa9aa383396a9`
- Status: two reviewer rounds found and fixed host-dependent roots, Windows/POSIX case
  semantics, POSIX debounce collisions, UNC paths, rooted Windows paths, and `C:\` root
  corruption. The final amendment reports 923 Linux tests passed with four platform
  skips, but it still needs the final reviewer pass and independent Windows verification.
- Integration note: the remote commit author is `T <t@e.com>`; amend attribution to
  `claude-code <claude-code@llamabox.sticktoitive.net>` when integrating.

### Task 11 fanotify interop and capability probe

- Branch: `openai/watch-ui-fanotify-interop`
- Local worktree: sibling `git-wizard-openai-fanotify-interop`
- Commit: `b6d569d66741930c0698d9ddea7ab28cde28589e`
- Base: `772a3b0a15c60f16a05ee45c5a4fa9aa383396a9`
- Status: final GPT-5.6 Sol review approved. It resolves the earlier EPERM/elevation,
  descriptor-zero lifetime, ENODEV/EOPNOTSUPP, Linux 5.9 floor, and directory-probe
  findings. Windows tests/build passed. Native Linux and root integration verification
  remains mandatory before integration is called complete.

## Resume order

1. Run the final review of `c9d34e8`, then verify its focused and full suites on Windows.
2. Integrate the Linux path branch and fix commit attribution.
3. Rebase or cherry-pick Task 11, run its targeted non-root and `RequiresRoot` tests on
   llamabox, then run combined Windows and Linux suites.
4. Perform the round-boundary review for duplicate path/native helpers and test/report
   drift before starting the next feature phase.
5. Implement Task 12 (`FanotifyVolumeChangeSource`) with real root event decoding tests.
6. Implement Task 13 (OS source factory and Linux `pkexec`).
7. Implement Task 14 (Linux graceful degradation and documentation).
8. Run the Windows Live-mode manual UAC smoke, full branch gates, durable-docs pass, and
   delete the consumed implementation plan.

## Pi delegation environment

Pi now has `pi-subagents` installed globally. Its default delegated model is
`openai-codex/gpt-5.6-sol`, model scope is restricted to `openai-codex/*`, and managed
worktrees live under `~/.pi/agent/worktrees`. Use the real `subagent` tool after a Pi
reload/restart rather than manually spawning `pi -p` subprocesses.
