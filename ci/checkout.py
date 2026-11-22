"""Shared pin/report-only detection and destructive CI checkout verification."""

import argparse
import os
import subprocess
from pathlib import Path

EARLY_EXIT_PATHS = frozenset({".aislop/fork-commit", "TEST-REPORT.md"})
# Everything the ci/test_*.py contracts read or run: the ci, scripts and tools trees, the
# workflows and previews they parse, the docs and instructions they cross-check, and the
# solution, project and lock files they copy. A path outside this list cannot change them.
CONTRACT_INPUT_PREFIXES = (
    "ci/",
    "scripts/",
    "tools/",
    ".gitea/workflows/",
    ".preview/",
    ".aislop/",
    "docs/",
)
CONTRACT_INPUT_FILES = frozenset(
    {
        "AGENTS.md",
        ".gitignore",
        "TEST-REPORT.md",
        "Directory.Build.props",
        "git-wizard.slnx",
    }
)
CONTRACT_INPUT_NAMES = frozenset({"packages.lock.json", ".gitattributes"})


def git(*arguments):
    return subprocess.run(
        ["git", *arguments],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="surrogateescape",
        check=False,
    )


def contracts_input(path):
    name = path.rsplit("/", 1)[-1]
    return (
        path.startswith(CONTRACT_INPUT_PREFIXES)
        or path in CONTRACT_INPUT_FILES
        or name in CONTRACT_INPUT_NAMES
        or (path.count("/") == 1 and name.lower().endswith(".csproj"))
    )


def run_contracts(paths):
    """False only for a successfully computed, nonempty list with no contract input."""
    return paths is None or not paths or any(contracts_input(path) for path in paths)


def changed_paths():
    """Changed paths for the event, or None when they cannot be computed."""
    event = os.environ.get("GITHUB_EVENT_NAME")
    base = os.environ.get("GITHUB_BASE_REF")
    before = os.environ.get("EVENT_BEFORE")
    after = os.environ.get("EVENT_AFTER")
    if event == "pull_request" and base:
        reference, comparison = base, ("FETCH_HEAD", "HEAD")
    elif event == "push" and before and before != "0" * 40 and after:
        reference, comparison = before, (before, after)
    else:
        return None
    if git("fetch", "--depth=1", "origin", reference).returncode:
        print("Changed-file fetch failed; running all gates.")
        return None
    changed = git("diff", "--name-only", "--no-renames", "-z", *comparison)
    print(ascii(changed.stdout))
    if changed.returncode:
        return None
    return [path for path in changed.stdout.split("\0") if path]


def pin_only():
    return only_early_exit(changed_paths())


def only_early_exit(paths):
    return paths is not None and len(paths) == 1 and paths[0] in EARLY_EXIT_PATHS


def clean_tree():
    cleaned = git("clean", "-ffxd")
    print(cleaned.stdout)
    if cleaned.returncode:
        print("::error::git clean failed: " + cleaned.stderr)
        return 1
    status = git("status", "--porcelain")
    if status.returncode or status.stdout:
        print(
            "::error::Checkout cleanliness is unverified or dirty: "
            + status.stdout
            + status.stderr
        )
        return 1
    print("git clean -ffxd left a clean tree.")
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("pin-only", "clean"))
    arguments = parser.parse_args()
    if arguments.operation == "clean":
        return clean_tree()
    with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as output:
        paths = changed_paths()
        output.write(f"pin_only={str(only_early_exit(paths)).lower()}\n")
        output.write(f"run_contracts={str(run_contracts(paths)).lower()}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
