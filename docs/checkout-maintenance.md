# Checkout maintenance

`git clean -ffxd` must always be safe to run. It is the check that this checkout still matches a fresh clone, so it is run before starting new work, and it must never be the thing that loses something.

That safety comes from an invariant, not from a wrapper: **nothing unrecoverable lives in the working tree.** Every file here is either tracked and pushed, or reproducible by re-running a tool. There is no exclude list, because an exclude list would leave the tree unequal to a fresh clone and defeat the reason for cleaning.

Anything that fails that test belongs somewhere else. If a file is worth keeping, track it and push it; if it is only worth keeping on one machine, keep it outside the working tree. Do not add a file to this repository that is neither.

Git offers no way to protect a file from `git clean`. Aliases cannot shadow built-in commands, there is no `pre-clean` hook, and `-x` overrides `.git/info/exclude`. Only tracked files and files outside the tree are safe, which is why the invariant above is the whole mechanism.

What a clean removes and how each comes back:

| Removed | Restored by |
| --- | --- |
| `bin/`, `obj/`, `Debug/`, `.vs/`, `TestResults/` | rebuild / re-run the test suite |
| `build-diag.log`, `build.binlog`, `smoke-stderr.log` at the repository root | re-run the build or coverage command that produced them |
| `.claude/scripts/` scan output and tool caches (`.pi-subagents/`, `.ruff_cache/`, `.superpowers/`) | re-run the corresponding tool |
| `.claude/settings.local.json` | re-granted as needed; broad grants live in the user-scope settings, and any provisioned project-scope overrides are re-applied by the tool that wrote them |
| `.claude/AISLOP.md`, `.claude/CLAUDE.md` | `aislop hook install claude --project` |
| `external/MFTLib/` (submodule contents) | `git submodule update --init --recursive`, or `init.ps1`/`init.sh` below |

`.claude/AISLOP.md` and `.claude/CLAUDE.md` are generated boilerplate the aislop installer writes into a sentinel-fenced block, which is why they are not tracked. They do not restore themselves - nothing rewrites them until that command is run, so run it after a clean. It is also how to refresh them after an aislop upgrade.

## Getting back to work after a clean

`init.ps1` (Windows) and `init.sh` (Linux) at the repository root do the things a clean does not undo by itself - the `external/MFTLib` submodule checkout, the NuGet restore, and the generated agent files - and report any prerequisite they cannot install for you:

```powershell
git clean -ffxd && .\init.ps1
git clean -ffxd && .\init.ps1 -Build
```

From `cmd.exe`, use `init.bat`, which forwards to the same script:

```bat
git clean -ffxd && .\init.bat
git clean -ffxd && .\init.bat -Build
```

Keep the `.\` prefix. On machines with `NoDefaultCurrentDirectoryInExePath=1`,
cmd does not search the working directory for a bare executable name.

```bash
git clean -ffxd && ./init.sh
git clean -ffxd && ./init.sh --build
```

Both are idempotent, so they are safe to run at any time, not only after a clean.

CI verifies this post-clean path in `.gitea/workflows/ci.yml`: `test-windows` runs a main-push-only clean/status assertion after tests to catch any stray tracked-file modifications before discarding that checkout, while the dedicated `init-verify` job runs the full post-clean cycle (`git clean -ffxd`, `init.ps1` twice, `init.bat`, `init.ps1 -Build`, then a clean-tree check). That job runs only on pushes to `main` (the merge), never on pull requests, and not for a pin-only push; it is gated by a job-level `if:` rather than workflow-level `paths`, and no branch-protection check depends on it. A pull request that breaks the clean-tree invariant is therefore caught one merge later; run the commands above locally when changing `init.*`, the submodule wiring, or build inputs.

They also make one optional call: if a settings-provisioning tool is on PATH, they ask it to re-apply the project-scope settings it owns, since a clean removes `.claude/settings.local.json`. The call names a single feature rather than running the tool's whole pipeline, and the step is skipped entirely when the tool is absent, so nothing here depends on it.

They are separate scripts rather than one cross-platform script because the work genuinely differs: Windows resolves MSBuild through `vswhere` and builds `MFTLibNative.vcxproj` (the only toolchain that can), while Linux drives cmake/Ninja directly and restores/builds the four cross-platform projects individually rather than `git-wizard.slnx` as a whole, mirroring the Linux CI job exactly (`.gitea/workflows/ci.yml`).

Prerequisites checked but not installed - Windows: .NET SDK, Visual Studio with the MSVC C++ workload, aislop. Linux: .NET SDK, cmake, ninja, g++, aislop.

## Sources

Restore and optional provisioning are implemented in `init.ps1`, `init.sh` and
`init.bat`; shared clean/status verification is `ci/checkout.py clean`, invoked by
the main-only Windows steps in `.gitea/workflows/ci.yml`.
