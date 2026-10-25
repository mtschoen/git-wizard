"""Shared pin/report-only detection and destructive CI checkout verification."""

import argparse
import os
import subprocess
from pathlib import Path

EARLY_EXIT_PATHS = frozenset({".aislop/fork-commit", "TEST-REPORT.md"})


def git(*arguments):
    return subprocess.run(
        ["git", *arguments], capture_output=True, text=True, check=False
    )


def pin_only():
    event = os.environ.get("GITHUB_EVENT_NAME")
    base = os.environ.get("GITHUB_BASE_REF")
    before = os.environ.get("EVENT_BEFORE")
    after = os.environ.get("EVENT_AFTER")
    if event == "pull_request" and base:
        reference, comparison = base, ("FETCH_HEAD", "HEAD")
    elif event == "push" and before and before != "0" * 40 and after:
        reference, comparison = before, (before, after)
    else:
        return False
    if git("fetch", "--depth=1", "origin", reference).returncode:
        print("Changed-file fetch failed; running all gates.")
        return False
    changed = git("diff", "--name-only", *comparison)
    print(changed.stdout)
    lines = changed.stdout.splitlines()
    return changed.returncode == 0 and len(lines) == 1 and lines[0] in EARLY_EXIT_PATHS


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
        output.write(f"pin_only={str(pin_only()).lower()}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
