#!/usr/bin/env bash
# Use exactly the workflow pin, never whichever aislop happens to be installed.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
commit=$(tr -d '[:space:]' < .aislop/fork-commit)
if [[ ! "$commit" =~ ^[0-9a-f]{40}$ ]]; then
    echo 'Invalid .aislop/fork-commit' >&2
    exit 1
fi
state=${LOCALAPPDATA:-${XDG_STATE_HOME:-$HOME/.local/state}}
directory=${AISLOP_TOOL_DIRECTORY:-${RUNNER_TEMP:+$RUNNER_TEMP/aislop}}
directory=${directory:-$state/git-wizard/aislop}
receipt="$directory/.git/build-receipt"
if [[ "${1:-}" == pin ]]; then
    echo "AISLOP_FORK_COMMIT=$commit" >> "${GITHUB_ENV:?}"
    exit 0
fi
if [[ "${1:-}" == prepare ]]; then
    rm -f "$receipt"
    if [[ ! -d "$directory/.git" ]]; then
        git clone https://gitea.fleet.sticktoitive.net/schoen/aislop.git "$directory"
    fi
    git -C "$directory" fetch origin "$commit"
    git -C "$directory" checkout "$commit"
    (cd "$directory" && npx -y pnpm@10.28.0 install --frozen-lockfile)
    # The pinned package's prepare lifecycle builds dist during installation.
    test -f "$directory/dist/cli.js"
    printf '%s\n' "$commit" > "$receipt"
    exit 0
fi
if [[ ! -f "$directory/dist/cli.js" || ! -f "$receipt" ]] ||
    [[ "$(cat "$receipt")" != "$commit" ]] ||
    [[ "$(git -C "$directory" rev-parse HEAD)" != "$commit" ]]; then
    echo "Pinned aislop $commit is unavailable. Run: bash ci/aislop-gate.sh prepare" >&2
    echo 'Preparation needs network access; the gate never downloads tools implicitly.' >&2
    exit 1
fi
export REPORT=${REPORT:-${CI_RESULTS_DIRECTORY:-$PWD/artifacts}/aislop-report.json}
mkdir -p "$(dirname "$REPORT")"
node "$directory/dist/cli.js" ci . --json > "$REPORT" &
aislop_pid=$!
( while kill -0 "$aislop_pid" 2>/dev/null; do
    echo "aislop scan in progress ($(date -u +%H:%M:%S))"
    sleep 60
done ) &
heartbeat_pid=$!
trap 'kill "$heartbeat_pid" 2>/dev/null || true' EXIT
failure=0
wait "$aislop_pid" || failure=1
node -e "const r=JSON.parse(require('fs').readFileSync(process.env.REPORT,'utf8')); const f=r.summary?.files??0; console.log('aislop score',r.score,'over',f,'files'); if(f<10){console.error('C# engine did not run');process.exit(1);}"
if [[ "$failure" != 0 ]]; then
    node -e "const r=JSON.parse(require('fs').readFileSync(process.env.REPORT,'utf8')); for(const f of(r.diagnostics??[])) console.log([f.severity,f.rule,f.filePath+':'+(f.line??0),f.message].join(' | '));"
fi
exit "$failure"
