#!/usr/bin/env bash
# pr-crew-preflight-protocol: 1
# Platform contract: Linux and Windows (Git Bash); macOS is refused.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
if command -v python3 >/dev/null 2>&1 && python3 --version >/dev/null 2>&1; then
    exec python3 ci/preflight.py "$@"
fi
exec python ci/preflight.py "$@"
