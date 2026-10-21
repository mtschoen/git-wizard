#!/usr/bin/env bash
# Windows verification gate for plan 4 (git-wizard packed-index port).
#
#   verify-windows.sh <ref>
#
# Runs the WINDOWS NON-ADMIN suite on chonkers. This exists because platform-specific tests
# only execute on their matching OS (such as IndexVolumeChangeSourceWindowsTests on Windows
# or fanotify tests on Linux), creating the requirement to run both OS jobs.
#
# It does NOT run the elevated / RequiresAdmin tests. Those need an interactive UAC prompt and are
# an attended step. NEVER report an elevated result from this script's output.
#
# Adapted from plan 3's lanes/verify-windows.sh. Two conventions carried over verbatim, each of
# which cost the campaign a session:
#   - invoke MSBuild over ssh via `powershell -NoProfile -Command`, never `cmd /c`: the nested
#     quoting collapses and cmd silently opens an interactive shell and exits 0.
#   - on chonkers `origin` is the GitHub mirror; only the `gitea` remote has feature branches.
set -euo pipefail

# HOST and REMOTE default to this fleet's Windows box but are environment overrides, so a
# different machine or checkout needs no edit:  HOST=user@box REMOTE=D:/src/git-wizard ...
REF="${1:?usage: verify-windows.sh <ref>}"
HOST="${HOST:-mtsch@chonkers}"
REMOTE="${REMOTE:-C:/Users/mtsch/git-wizard}"
# .gitmodules uses the relative URL ../MFTLib.git, which resolves against the SUPERPROJECT's
# origin. On this fleet's Windows box that origin is the GitHub mirror, which lags gitea, so a
# plain `git submodule update` fails with "upload-pack: not our ref" on a pin that only gitea has.
# The pinned commit is therefore fetched from gitea explicitly. Override for a different forge.
MFTLIB_URL="${MFTLIB_URL:-gitea@gitea.fleet.sticktoitive.net:schoen/MFTLib.git}"
# MSBuild is not on PATH in a non-interactive PowerShell session, and the 32-bit MSBuild cannot
# build this solution from a WOW64-virtualized path, so the amd64 one is resolved through vswhere.
VSWHERE="${VSWHERE:-C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe}"

# Validate REF as a commit SHA (7-40 hex chars) or resolvable commit.
if [[ "$REF" =~ ^[0-9a-fA-F]{7,40}$ ]]; then
    COMMIT_SHA="$REF"
elif git rev-parse --verify --quiet "$REF^{commit}" >/dev/null 2>&1; then
    COMMIT_SHA=$(git rev-parse --verify "$REF^{commit}")
else
    echo "ERROR: REF must be a commit SHA (7-40 hex chars) or valid commit ref (got: $REF)" >&2
    exit 1
fi

if ! [[ "$COMMIT_SHA" =~ ^[0-9a-fA-F]{7,40}$ ]]; then
    echo "ERROR: Validated commit SHA contains invalid characters (got: $COMMIT_SHA)" >&2
    exit 1
fi

# Transport configurable values as base64-encoded UTF-8 strings to prevent command injection.
B64_REMOTE=$(printf '%s' "$REMOTE" | base64 -w 0)
B64_MFTLIB_URL=$(printf '%s' "$MFTLIB_URL" | base64 -w 0)
B64_VSWHERE=$(printf '%s' "$VSWHERE" | base64 -w 0)

PS_CONFIG_PREAMBLE="
\$remote = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('$B64_REMOTE'))
\$mftlibUrl = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('$B64_MFTLIB_URL'))
\$vswhere = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('$B64_VSWHERE'))
\$targetRef = '$COMMIT_SHA'
"

run_remote() {
    local ps_script="$1"
    local encoded
    encoded=$(printf '%s' "$ps_script" | iconv -f UTF-8 -t UTF-16LE | base64 -w 0)
    timeout 900 ssh -o BatchMode=yes -o ConnectTimeout=15 "$HOST" \
        "powershell -NoProfile -EncodedCommand $encoded" 2>&1 | tr -d '\r' | sed -E '/post-quantum|store now, decrypt later|openssh\.com\/pq/d'
}

echo "== fetching $COMMIT_SHA on $HOST:$REMOTE =="
PS_CHECKOUT="$PS_CONFIG_PREAMBLE
Set-Location -LiteralPath \$remote
git fetch gitea
if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }
git checkout --force \$targetRef
if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }
git submodule init
if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }
\$expectedPin = (git rev-parse HEAD:external/MFTLib).Trim()
if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }
git -C external/MFTLib fetch \$mftlibUrl \$expectedPin
if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }
git -C external/MFTLib checkout --force \$expectedPin
if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }
\$actualHead = (git rev-parse HEAD).Trim()
if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }
\$actualPin = (git -C external/MFTLib rev-parse HEAD).Trim()
if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }
if (-not \$actualHead.StartsWith(\$targetRef, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Error \"Resolved HEAD \$actualHead does not match target \$targetRef\"
    exit 1
}
if (\$actualPin -ne \$expectedPin) {
    Write-Error \"Submodule HEAD \$actualPin does not match expected pin \$expectedPin\"
    exit 1
}
Write-Output \$actualHead
Write-Output \$actualPin
"

if ! CHECKOUT=$(run_remote "$PS_CHECKOUT"); then
    echo "$CHECKOUT"
    echo "RESULT: FAIL (checkout)"
    exit 1
fi
echo "$CHECKOUT" | tail -4
if echo "$CHECKOUT" | grep -q "not our ref"; then
    echo "RESULT: FAIL (submodule pin missing on $MFTLIB_URL)"
    exit 1
fi

echo "== native MFTLibNative.vcxproj (Release x64) =="
PS_NATIVE="$PS_CONFIG_PREAMBLE
Set-Location -LiteralPath \$remote
\$msbuild = & \$vswhere -latest -requires Microsoft.Component.MSBuild -find MSBuild/**/Bin/amd64/MSBuild.exe | Select-Object -First 1
if (-not \$msbuild) {
    Write-Error 'vswhere found no amd64 MSBuild'
    exit 1
}
& \$msbuild external\\MFTLib\\MFTLibNative\\MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64 -v:minimal
if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }
Write-Output \$LASTEXITCODE
"

if ! NATIVE=$(run_remote "$PS_NATIVE"); then
    echo "$NATIVE"
    echo "RESULT: FAIL (native build)"
    exit 1
fi
echo "$NATIVE" | tail -5

echo "== managed build =="
PS_MANAGED="$PS_CONFIG_PREAMBLE
Set-Location -LiteralPath \$remote
dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }
dotnet build external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj -c Release -p:Platform=x64
if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }
dotnet build GitWizardTests/GitWizardTests.csproj -c Release
if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }
"

if ! MANAGED=$(run_remote "$PS_MANAGED"); then
    echo "$MANAGED"
    echo "RESULT: FAIL (managed build)"
    exit 1
fi
echo "$MANAGED" | tail -8

echo "== WINDOWS NON-ADMIN SUITE =="
PS_TEST="$PS_CONFIG_PREAMBLE
Set-Location -LiteralPath \$remote
dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build --filter 'Category!=RequiresAdmin'
if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }
"

set +e
OUT=$(run_remote "$PS_TEST")
TEST_STATUS=$?
set -e
echo "$OUT" | tail -20

if [ $TEST_STATUS -eq 0 ] && echo "$OUT" | grep -qE "Passed!|Passed:.*Failed: *0"; then
    echo "RESULT: PASS (windows non-admin)"
else
    echo "RESULT: FAIL (windows non-admin)"
    # Print each failing test's OWN block, not lines grep-matched from anywhere in the run. The
    # earlier form (grep -E "error |Failed |Assert" | sort -u) surfaced a line printed by a
    # DIFFERENT, PASSING test while hiding the real message, and sort -u then detached every
    # surviving line from the test that produced it. That cost two single-test re-runs on
    # 2026-09-12. A block runs from a "  Failed <name>" line to the next test result or to the run
    # summary, so message and stack trace stay attached to the test that failed.
    BLOCKS=$(echo "$OUT" | awk '
        /^[[:space:]]+(Passed|Skipped) / { inblock = 0; next }
        /^(Failed!|Passed!|Test Run|Attachments:)/ { inblock = 0 }
        /^[[:space:]]+Failed / { inblock = 1 }
        inblock')
    if [ -n "$BLOCKS" ]; then
        echo "$BLOCKS"
    else
        # No per-test block at all: the run died before or outside a test (build break, crash,
        # host failure). Fall back to matched lines in run order rather than reporting nothing.
        echo "(no per-test failure block found; showing matched lines in run order)"
        echo "$OUT" | grep -E "error |Failed |Assert" | head -30
    fi
    exit 1
fi
