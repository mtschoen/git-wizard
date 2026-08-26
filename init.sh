#!/usr/bin/env bash
# init.sh - restore this checkout to a working state after `git clean -ffxd`.
#
# A deep clean removes everything not tracked, which is safe by design - but
# several things do not come back on their own: the git submodule checkout,
# the NuGet restore, and the agent instruction files the aislop installer
# generates. This does all three, and reports any prerequisite it cannot
# install for you.
#
# Usage:
#   git clean -ffxd && ./init.sh
#   git clean -ffxd && ./init.sh --build    # also build native (cmake/ninja) + managed
#
# Safe to run at any time; every step is idempotent.
# Windows equivalent: init.ps1

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD=0

for arg in "$@"; do
    case "$arg" in
        --build) BUILD=1 ;;
        -h|--help) sed -n '2,15p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "unknown flag: $arg" >&2; exit 2 ;;
    esac
done

missing=()
completed=()

have() { command -v "$1" >/dev/null 2>&1; }

echo "Checking prerequisites..."

if have dotnet; then
    printf '  %-20s %s\n' "dotnet SDK" "$(dotnet --version 2>/dev/null)"
else
    printf '  %-20s %s\n' "dotnet SDK" "MISSING"
    missing+=("dotnet SDK 10 - https://dotnet.microsoft.com/download")
fi

for tool in cmake ninja; do
    if have "$tool"; then
        printf '  %-20s %s\n' "$tool" "present"
    else
        printf '  %-20s %s\n' "$tool" "MISSING"
        missing+=("$tool - required to build external/MFTLib's native library on Linux")
    fi
done

if have g++; then
    printf '  %-20s %s\n' "g++" "$(g++ -dumpversion 2>/dev/null)"
else
    printf '  %-20s %s\n' "g++" "MISSING"
    missing+=("g++ - required to build external/MFTLib's native library on Linux")
fi

if have aislop; then
    printf '  %-20s %s\n' "aislop" "$(aislop --version 2>/dev/null)"
    aislop_available=1
else
    printf '  %-20s %s\n' "aislop" "missing"
    missing+=("aislop - see the quality gate section of AGENTS.md for the pinned install command")
    aislop_available=0
fi

echo

if ! have dotnet; then
    echo "Cannot continue without the dotnet SDK." >&2
    exit 1
fi

# external/MFTLib is a git submodule that a fresh clone or a post-clean checkout
# leaves empty. Nothing here builds until it is checked out, so restore it
# unconditionally rather than only under --build.
if [ ! -f "$ROOT/external/MFTLib/MFTLib/MFTLib.csproj" ]; then
    echo "Initializing external/MFTLib submodule..."
    git -C "$ROOT" submodule update --init --recursive
    completed+=("external/MFTLib submodule checked out")
fi

# Restore each cross-platform project individually rather than git-wizard.slnx
# as a whole: this mirrors the Linux CI job exactly (test-linux in
# .gitea/workflows/ci.yml), which restores this same list.
echo
echo "Restoring NuGet packages..."
for proj in \
    GitWizard/GitWizard.csproj \
    git-wizard/git-wizard.csproj \
    GitWizardUI/GitWizardUI.csproj \
    GitWizardTests/GitWizardTests.csproj
do
    dotnet restore "$ROOT/$proj"
done
completed+=("NuGet packages restored")

if [ "$aislop_available" -eq 1 ]; then
    echo
    echo "Restoring agent instruction files..."
    (cd "$ROOT" && aislop hook install claude --project)
    completed+=(".claude/AISLOP.md and .claude/CLAUDE.md restored")
else
    echo
    echo "Skipping agent instruction files: aislop is not installed."
    echo "Once installed, run: aislop hook install claude --project"
fi

# Optional. A clean removes .claude/settings.local.json, which can hold project-scope
# settings written by external provisioning tooling rather than by hand. If that tooling
# is on PATH, let it re-apply its own settings; otherwise this is skipped and nothing
# here depends on it. The feature argument is required, not incidental: a bare
# `onboard apply` runs the full host pipeline, which is far outside a repository's remit.
# The same tool ships under several console-script names; use whichever is exposed.
# The bare name `onboard` is deliberately not among them: it is also the GNOME
# on-screen keyboard binary at /usr/bin/onboard on Debian and Ubuntu, so probing it
# would run a GUI keyboard with our arguments on any Linux host that has the package
# and not this tool. The namespaced names cost nothing - they are entry points of the
# same installation.
provisioner=""
for candidate in schoen-lab-onboard schoen-lab; do
    if have "$candidate"; then provisioner="$candidate"; break; fi
done

if [ -n "$provisioner" ]; then
    echo
    echo "Re-applying provisioned project settings..."
    if (cd "$ROOT" && "$provisioner" apply auto-memory); then
        completed+=("Project-scope provisioned settings re-applied")
    else
        echo "$provisioner apply auto-memory failed; continuing." >&2
    fi
fi

if [ "$BUILD" -eq 1 ]; then
    if ! have cmake || ! have ninja; then
        echo "Cannot build: cmake and ninja are required." >&2
        exit 1
    fi

    echo
    echo "Building MFTLib native library..."
    cmake -S "$ROOT/external/MFTLib/MFTLibNative" -B "$ROOT/external/MFTLib/build-linux" -G Ninja -DCMAKE_BUILD_TYPE=Release
    cmake --build "$ROOT/external/MFTLib/build-linux"

    echo
    echo "Building MFTLib managed projects..."
    dotnet build "$ROOT/external/MFTLib/MFTLib/MFTLib.csproj" -c Release -p:Platform=x64
    dotnet build "$ROOT/external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj" -c Release -p:Platform=x64

    echo
    echo "Building git-wizard (cross-platform projects)..."
    for proj in \
        GitWizard/GitWizard.csproj \
        git-wizard/git-wizard.csproj \
        GitWizardUI/GitWizardUI.csproj \
        GitWizardTests/GitWizardTests.csproj
    do
        dotnet build "$ROOT/$proj" -c Release --no-restore
    done
    completed+=("MFTLib and git-wizard built (Release)")
fi

echo
echo "Done."
for step in "${completed[@]}"; do
    echo "  $step"
done

if [ "${#missing[@]}" -gt 0 ]; then
    echo
    echo "Missing prerequisites (install manually):"
    for item in "${missing[@]}"; do
        echo "  - $item"
    done
fi

if [ "$BUILD" -eq 0 ]; then
    echo
    echo "Next: ./init.sh --build, or dotnet test GitWizardTests/GitWizardTests.csproj for the test suite."
fi
