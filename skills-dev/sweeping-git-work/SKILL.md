---
name: sweeping-git-work
description: Use when auditing one or more machines before travel, migration, shutdown, repair, or cleanup for dirty tracked files, unpushed branch commits, or git stashes.
---

# Sweeping Git Work

## Overview

Use git-wizard's stable JSON sweep instead of rebuilding ad-hoc `find`, `status`, and `rev-list` pipelines. Treat the sweep as read-only; creating a recovery archive is a separate, explicit action.

## Run the sweep

1. Resolve the CLI on every target:

   ```bash
   command -v git-wizard
   ```

   If it is absent, report the host and stop. Ask the operator to install a current git-wizard release or provide a source checkout. Do not silently download or execute a release. From an already-built checkout, invoke `dotnet /path/to/git-wizard.dll` in place of `git-wizard`.

2. Capture stdout; diagnostics belong on stderr:

   ```bash
   git-wizard --sweep --print-minified > local-sweep.json
   ssh -- "$host" 'git-wizard --sweep --print-minified' > "$host-sweep.json"
   ```

   Use `--rebuild-repo-list` for fresh discovery, `--no-mft` to force recursive discovery, or `--paths <file-or-comma-list>` for bounded checks.

3. Validate before summarizing:

   ```bash
   jq -e '.SchemaVersion == "1.0" and (.Repositories | type == "array")' host-sweep.json
   ```

4. Report a table with host, repository path, dirty tracked files, unpushed branches as `name (N commits)`, stash count, and error. Omit clean repositories, but explicitly say when a host is clean. Treat any `Error` as an incomplete audit.

The branch comparison uses local remote-tracking refs and performs no network fetch. State that freshness limitation. Never run `git fetch` across the fleet without explicit approval.

## Reap affected repositories

Only create an archive when the user asks. For each affected repository, create a collision-free directory under one staging directory, then run:

```bash
repo=/absolute/path/to/repository
dest=/path/to/staging/unique-repository-id
mkdir -p "$dest"
printf '%s\n' "$repo" > "$dest/source-path.txt"
git -C "$repo" bundle create "$dest/repository.bundle" --all
git -C "$repo" bundle verify "$dest/repository.bundle"
git -C "$repo" diff --binary HEAD -- . > "$dest/tracked.patch"
```

`--all` makes the bundle self-contained and includes local branches plus `refs/stash`; it may also include remote refs and prerequisite history. If the repository has no refs, omit the bundle and record that fact. The patch includes staged and unstaged tracked changes, but intentionally excludes untracked files.

After staging every repository, create one transfer artifact:

```bash
tar -C /path/to/staging -czf git-sweep-recovery.tar.gz .
```

Keep the JSON sweep beside the archive as its manifest. Do not delete, reset, stash, or otherwise mutate source repositories after reaping.

## Common mistakes

- Parsing normal git-wizard reports instead of using `--sweep`.
- Treating a zero finding count as fresh-remote proof.
- Losing JSON by mixing stderr into stdout over SSH.
- Claiming untracked files are protected by `tracked.patch`.
