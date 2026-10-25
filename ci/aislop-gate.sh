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
    (
        cd "$directory"
        package_manager=$(node -p "require('./package.json').packageManager" | tr -d '\r')
        if [[ ! "$package_manager" =~ ^pnpm@[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
            echo "aislop fork package.json packageManager is missing or not pnpm@x.y.z: '$package_manager'" >&2
            exit 1
        fi
        npx -y "$package_manager" install --frozen-lockfile
    )
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
# Child builds must not leave servers holding the runner's output pipes open.
export MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
export MSBUILDUSESERVER=0
export UseSharedCompilation=false
node "$directory/dist/cli.js" ci . --json > "$REPORT" &
aislop_pid=$!
(
    sleeper_pid=
    stop_sleeper() {
        if [[ -n "$sleeper_pid" ]]; then
            if kill "$sleeper_pid" 2>/dev/null; then :; fi
            if wait "$sleeper_pid" 2>/dev/null; then :; fi
        fi
    }
    trap 'stop_sleeper; exit 0' TERM
    while kill -0 "$aislop_pid" 2>/dev/null; do
        echo "aislop scan in progress ($(date -u +%H:%M:%S))"
        sleep 60 &
        sleeper_pid=$!
        # Waiting asynchronously lets TERM reap the timer before this shell exits.
        if wait "$sleeper_pid"; then sleeper_pid=; else exit 0; fi
    done
) &
heartbeat_pid=$!
stop_heartbeat() {
    if kill "$heartbeat_pid" 2>/dev/null; then :; fi
    if wait "$heartbeat_pid" 2>/dev/null; then :; fi
}
trap stop_heartbeat EXIT
failure=0
wait "$aislop_pid" || failure=1
node ci/aislop-report-summary.js
node -e "const r=JSON.parse(require('fs').readFileSync(process.env.REPORT,'utf8')); const f=r.summary?.files??0; console.log('aislop score',r.score,'over',f,'files'); if(f<10){console.error('C# engine did not run');process.exit(1);}"
if [[ "$failure" != 0 ]]; then
    node -e "const r=JSON.parse(require('fs').readFileSync(process.env.REPORT,'utf8')); for(const f of(r.diagnostics??[])) console.log([f.severity,f.rule,f.filePath+':'+(f.line??0),f.message].join(' | '));"
    # A perfect score can still exit 1 for a missing tool or engine failure; say which.
    fail_below=$(sed -n 's/^[[:space:]]*failBelow:[[:space:]]*\([0-9][0-9]*\).*/\1/p' .aislop/config.yml | head -n 1)
    node ci/aislop-report-summary.js --exit-reason "${fail_below:-100}"
fi
exit "$failure"
