#!/bin/sh
# Download and launch a git-wizard PR preview artifact for the current OS.
#
# Usage:
#   scripts/run-preview.sh            # newest git-wizard preview package
#   scripts/run-preview.sh 42         # newest artifact for PR #42
#
# The preview provider (.preview/up on llamabox, or the windows preview.yml
# workflow) publishes the app to the gitea generic package registry. This
# fetches the current-OS zip, unpacks it to a temp dir, and launches
# GitWizardUI. Set GITEA_TOKEN if the registry needs auth; anonymous GET first.
# Requires: curl, jq, unzip.
set -eu

GITEA="https://gitea.llamabox.sticktoitive.net"
OWNER="schoen"

case "$(uname -s)" in
    Linux)  platform="linux" ;;
    *)      echo "no git-wizard preview artifact is published for $(uname -s)" >&2; exit 1 ;;
esac
file="GitWizardUI-${platform}-x64.zip"

# curl wrapper: add the auth header only when GITEA_TOKEN is set (anonymous
# first).
api() {
    if [ -n "${GITEA_TOKEN:-}" ]; then
        curl -fsSL -H "Authorization: token $GITEA_TOKEN" "$@"
    else
        curl -fsSL "$@"
    fi
}

if [ $# -ge 1 ]; then
    package="git-wizard-pr-$1"
    resp="$(api "$GITEA/api/v1/packages/$OWNER?type=generic&q=$package")"
    version="$(printf '%s' "$resp" | jq -r --arg n "$package" \
        '[.[] | select(.name == $n)] | sort_by(.created_at) | last | .version')"
else
    resp="$(api "$GITEA/api/v1/packages/$OWNER?type=generic&q=git-wizard-pr-")"
    package="$(printf '%s' "$resp" | jq -r 'sort_by(.created_at) | last | .name')"
    version="$(printf '%s' "$resp" | jq -r 'sort_by(.created_at) | last | .version')"
fi

if [ -z "$package" ] || [ "$package" = "null" ] || [ -z "$version" ] || [ "$version" = "null" ]; then
    echo "no git-wizard preview package found" >&2
    exit 1
fi

url="$GITEA/api/packages/$OWNER/generic/$package/$version/$file"
tmp="$(mktemp -d)"
echo "downloading $file ($package @ $version)" >&2
api -o "$tmp/$file" "$url"
( cd "$tmp" && unzip -q "$file" )
exe="$tmp/GitWizardUI"
chmod +x "$exe"
echo "launching $exe" >&2
exec "$exe"
