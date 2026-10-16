"""Run shared CI gates, aggregate failures, and stamp clean complete runs."""

import argparse
import json
import os
import platform
import subprocess
import sys
import uuid
from pathlib import Path

GATES = (
    "native",
    "restore",
    "format",
    "build",
    "inspect",
    "test",
    "coverage",
    "contracts",
    "aislop",
)
ROOT = Path(__file__).resolve().parents[1]


def git(*arguments):
    return subprocess.check_output(["git", *arguments], cwd=ROOT, text=True).strip()


def parse_arguments(arguments):
    parser = argparse.ArgumentParser()
    selection = parser.add_mutually_exclusive_group(required=True)
    selection.add_argument("--all", action="store_true")
    selection.add_argument("--changed-from", metavar="REF")
    selection.add_argument("--only", action="append", choices=GATES)
    return parser.parse_args(arguments)


def publish_stamp(stamp, head, base):
    if git("status", "--porcelain") or git("rev-parse", "HEAD") != head:
        print(
            "No publication stamp: working tree is dirty or HEAD changed.", flush=True
        )
        return
    stamp.parent.mkdir(parents=True, exist_ok=True)
    stamp.write_text(
        json.dumps(
            {
                "head": head,
                "base": base,
                "protocol_version": 1,
                "gate_set": ["gates", "node", "python", "test-delta"],
            }
        )
        + "\n",
        encoding="utf-8",
    )
    print(f"Publication stamp: {stamp}", flush=True)


def has_current_coverage(results):
    if not results.is_absolute():
        results = ROOT / results
    pointer = results / "current-test-directory"
    if not pointer.is_file():
        return False
    directory = pointer.read_text().strip()
    return bool(directory) and any(
        (results / directory).rglob("coverage.cobertura.xml")
    )


def run(arguments):
    options = parse_arguments(arguments)
    host = platform.system()
    if host not in ("Windows", "Linux"):
        print(f"REFUSED: {host} has no matching CI job", flush=True)
        return 2
    selected = options.only or list(GATES)
    base = options.changed_from or "main"
    complete = not options.only
    stamp = Path(
        os.environ.get("CI_PREFLIGHT_STAMP_PATH")
        or git("rev-parse", "--git-path", "pr-crew/preflight-stamp.json")
    )
    if not stamp.is_absolute():
        stamp = ROOT / stamp
    # Invalidate even targeted runs: they can expose failures after a prior pass.
    stamp.unlink(missing_ok=True)
    if options.changed_from:
        git("rev-parse", "--verify", f"{base}^{{commit}}")
        print("Changed scope conservatively runs every host gate.", flush=True)
    head = git("rev-parse", "HEAD")
    if host == "Windows":
        state = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData/Local"))
        state = state / "GitWizard/test-results"
    else:
        state = Path(os.environ.get("XDG_STATE_HOME", Path.home() / ".local/state"))
        state = state / "git-wizard/test-results"
    results = Path(os.environ.get("CI_RESULTS_DIRECTORY") or state / uuid.uuid4().hex)
    environment = dict(os.environ, CI_RESULTS_DIRECTORY=str(results))
    print(f"Protocol 1; platform {host}; results: {results}", flush=True)
    print(
        "SKIP remote effects: status posting, artifact upload, provisioning, release/preview/screenshot jobs.",
        flush=True,
    )
    print(
        "SKIP destructive main-only clean/init verification and attended elevation/desktop tests.",
        flush=True,
    )
    failed = False
    test_failed = False
    for gate in selected:
        if gate == "inspect" and host != "Windows":
            print(
                "SKIP inspect: Windows runner JetBrains/global naming configuration only",
                flush=True,
            )
            continue
        if gate == "coverage" and host == "Windows":
            print("SKIP coverage: enforced by the Linux job only", flush=True)
            continue
        if gate == "coverage" and test_failed and not has_current_coverage(results):
            print(
                "SKIP coverage: test failed and coverage input is missing", flush=True
            )
            continue
        print(f"RUN {gate}", flush=True)
        result = subprocess.run(
            ["bash", "ci/gates.sh", gate], cwd=ROOT, env=environment, check=False
        )
        print(
            f"{'PASS' if result.returncode == 0 else 'FAIL'} {gate} (exit {result.returncode})",
            flush=True,
        )
        failed |= result.returncode != 0
        if gate == "test":
            test_failed = result.returncode != 0
    if complete and not failed:
        publish_stamp(stamp, head, base)
    return int(failed)


if __name__ == "__main__":
    try:
        sys.exit(run(sys.argv[1:]))
    except (OSError, subprocess.CalledProcessError) as error:
        print(f"Preflight inspection failed: {error}", file=sys.stderr)
        sys.exit(1)
