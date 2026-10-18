#!/usr/bin/env python3
"""PostToolUse on-save formatter hook entry point (.cs files only).

Reads the hook payload (JSON) from stdin and, when the edited file is a
tracked .cs file, runs `dotnet format whitespace` against it. This is the
single implementation for every harness and every platform: Python's
subprocess module invokes `dotnet` identically on Linux, macOS, and
Windows, so there is no platform-specific reason to hand the work off to a
second interpreter. Delegating to one would also be fragile: some pwsh
installs are a "dotnet tool" shim that must itself exec `dotnet` to start,
so an absent or broken `dotnet` crashes the delegate with an unhandled
exception before any dotnet-absent handling can run.

Reports exactly one of three outcomes, and never conflates them:
  - skipped: not applicable, or dotnet is unavailable - benign  (stdout, exit 0)
  - applied: the formatter ran and succeeded                    (stdout, exit 0)
  - FAILED:  the formatter ran and returned a nonzero exit code  (stderr, exit 2)

Exit code 2 is deliberate, not arbitrary: for a Claude Code PostToolUse hook
it is the one exit code whose stderr is surfaced back to the agent. Since
the edited file was already written before this hook runs, no PostToolUse
exit code can undo or block that edit, so reporting failure loudly costs
nothing and is strictly better than silently claiming success.

Tracked under scripts/ and wired from the tracked .claude/settings.json
(Claude) and .agents/hooks.json (Antigravity/AGY).
"""

import json
import logging
import os
import shutil
import subprocess
import sys
from typing import NoReturn

FORMATTER_FAILURE_EXIT_CODE = 2

logging.basicConfig(level=logging.WARNING, format="format-on-save: %(message)s")
logger = logging.getLogger(__name__)


def extract_file_path(payload):
    if not isinstance(payload, dict):
        return None
    tool_input = payload.get("tool_input")
    if not isinstance(tool_input, dict):
        tool_input = {}
    tool_response = payload.get("tool_response")
    if not isinstance(tool_response, dict):
        tool_response = {}
    return (
        tool_input.get("file_path")
        or tool_input.get("TargetFile")
        or tool_input.get("targetFile")
        or tool_response.get("filePath")
    )


def report_skip(reason) -> NoReturn:
    print(f"format-on-save: skipped ({reason})")
    sys.exit(0)


def report_failure(file_path, exit_code, combined_output) -> NoReturn:
    print(
        f"format-on-save: FAILED - dotnet format whitespace exited {exit_code} for {file_path}",
        file=sys.stderr,
    )
    if combined_output.strip():
        print(combined_output, file=sys.stderr)
    sys.exit(FORMATTER_FAILURE_EXIT_CODE)


def find_repository_root(file_path):
    directory = os.path.dirname(file_path) or "."
    try:
        return subprocess.check_output(
            ["git", "-C", directory, "rev-parse", "--show-toplevel"],
            text=True,
            stderr=subprocess.DEVNULL,
        ).strip()
    except (subprocess.CalledProcessError, OSError):
        return None


def run_dotnet_format(file_path):
    repository_root = find_repository_root(file_path)
    if not repository_root:
        report_skip(f"{file_path} is not inside a git repository")

    solution_path = os.path.join(repository_root, "git-wizard.slnx")
    if not os.path.isfile(solution_path):
        report_skip(f"no git-wizard.slnx at {repository_root}")

    if not shutil.which("dotnet"):
        report_skip("dotnet not found on PATH")

    result = subprocess.run(
        [
            "dotnet",
            "format",
            "whitespace",
            solution_path,
            "--include",
            file_path,
            "--no-restore",
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode != 0:
        report_failure(
            file_path, result.returncode, (result.stdout or "") + (result.stderr or "")
        )

    print(f"format-on-save: whitespace applied to {file_path}")
    sys.exit(0)


def main():
    raw_stdin = "" if sys.stdin.isatty() else sys.stdin.read()
    try:
        payload = json.loads(raw_stdin) if raw_stdin.strip() else {}
    except json.JSONDecodeError as error:
        logger.warning("malformed hook payload: %s", error)
        report_skip("malformed hook payload")

    file_path = extract_file_path(payload)
    if not file_path:
        report_skip("no file path in hook payload")
    if not str(file_path).endswith(".cs"):
        report_skip(f"{file_path} is not a .cs file")
    if not os.path.isfile(file_path):
        report_skip(f"{file_path} does not exist")

    run_dotnet_format(file_path)


if __name__ == "__main__":
    main()
