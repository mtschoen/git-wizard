# Watch-in-UI Session Handoff

## Integrated branch

`feat/watch-in-ui` is clean at `772a3b0`. Phase 1, Windows Phase 2, the Live UI toggle,
and the executable icon are committed. The real Windows UAC/filesystem-event smoke is
still outstanding.

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
