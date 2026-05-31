#!/usr/bin/env python3
"""Post the pr-crew/coverage commit status and (optionally) gate on coverage.

Computes line-coverage percent from a coverage report and can:
  * POST it as a Gitea commit status (context=pr-crew/coverage) on $GITHUB_SHA
    using the auto $GITHUB_TOKEN  (default behaviour; suppress with --skip-post),
  * write a line/branch coverage table to $GITHUB_STEP_SUMMARY  (--summary),
  * fail the build when line coverage is below a threshold  (--gate-line N).

Percent source:
  default                -> pytest-cov coverage.json ['totals']['percent_covered']
  --cobertura "<glob>"   -> merge Cobertura XML line hits across matched files
                            (a line counts covered if any report shows hits>0)

On a measurement failure it posts state=error (so pr-crew reads the gate as
'unreadable', not silently missing). A plain status post then still exits 0 so
an `if: always()` step does not double-fail the job; but when --gate-line is
set, an unreadable report is treated as a gate failure (exit 1). A POST/network
failure DOES raise.
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import ssl
import sys
import urllib.request
import xml.etree.ElementTree as ElementTree


def _percent_from_coverage_json(path: str) -> float:
    with open(path) as handle:
        return float(json.load(handle)["totals"]["percent_covered"])


def _cobertura_paths(patterns: list[str]) -> list[str]:
    paths = [
        path
        for pattern in patterns
        for path in glob.glob(pattern, recursive=True)
        if "/bin/" not in path and "/obj/" not in path
    ]
    if not paths:
        raise FileNotFoundError("no Cobertura XML matched")
    return paths


def _percent_from_cobertura(patterns: list[str]) -> float:
    lines: dict[tuple[str, str], bool] = {}
    for path in _cobertura_paths(patterns):
        for class_node in ElementTree.parse(path).getroot().iter("class"):
            filename = class_node.get("filename", "")
            for line_node in class_node.iter("line"):
                key = (filename, line_node.get("number", ""))
                lines[key] = (
                    lines.get(key, False) or int(line_node.get("hits", "0")) > 0
                )
    if not lines:
        raise ValueError("no source lines in Cobertura XML")
    return 100.0 * sum(1 for covered in lines.values() if covered) / len(lines)


def _branch_percent_from_cobertura(patterns: list[str]) -> float:
    """Average of the root branch-rate across reports (informational summary only).

    Exact for the single-report case CI produces; a reasonable approximation if
    coverage is ever split across multiple Cobertura files.
    """
    rates = [
        float(rate)
        for path in _cobertura_paths(patterns)
        if (rate := ElementTree.parse(path).getroot().get("branch-rate")) is not None
    ]
    return 100.0 * sum(rates) / len(rates) if rates else 0.0


def _write_summary(line_percent: float, branch_percent: float) -> None:
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not path:
        return
    with open(path, "a") as handle:
        handle.write("## Coverage\n\n")
        handle.write("| Metric | Coverage |\n| --- | --- |\n")
        handle.write(f"| Line | {line_percent}% |\n")
        handle.write(f"| Branch | {branch_percent}% |\n")


def _post(state: str, description: str) -> None:
    server = os.environ["GITHUB_SERVER_URL"]
    repository = os.environ["GITHUB_REPOSITORY"]
    sha = os.environ["GITHUB_SHA"]
    run_id = os.environ.get("GITHUB_RUN_ID", "")
    body = json.dumps(
        {
            "context": "pr-crew/coverage",
            "state": state,
            "description": description,
            "target_url": f"{server}/{repository}/actions/runs/{run_id}",
        }
    ).encode()
    request = urllib.request.Request(
        f"{server}/api/v1/repos/{repository}/statuses/{sha}",
        data=body,
        method="POST",
        headers={
            "Authorization": f"token {os.environ['GITHUB_TOKEN']}",
            "Content-Type": "application/json",
        },
    )
    context = ssl.create_default_context()
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE  # Gitea uses a self-signed mkcert cert
    urllib.request.urlopen(request, context=context).read()


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--coverage-json", default="coverage.json")
    parser.add_argument("--cobertura", nargs="+")
    parser.add_argument(
        "--gate-line",
        type=float,
        default=None,
        help="fail (exit 1) when line coverage is below this percent",
    )
    parser.add_argument(
        "--summary",
        action="store_true",
        help="write a line/branch coverage table to $GITHUB_STEP_SUMMARY",
    )
    parser.add_argument(
        "--skip-post",
        action="store_true",
        help="do not POST the commit status (e.g. for the dedicated gate step)",
    )
    arguments = parser.parse_args(argv[1:])

    try:
        if arguments.cobertura:
            percent = _percent_from_cobertura(arguments.cobertura)
        else:
            percent = _percent_from_coverage_json(arguments.coverage_json)
    except Exception as error:  # measurement failed
        print(f"coverage measurement failed: {error}", file=sys.stderr)
        if not arguments.skip_post:
            _post("error", "coverage measurement failed")
        # A gate step treats an unreadable report as a failure; a plain status
        # post stays non-fatal so an `if: always()` step won't double-fail.
        return 1 if arguments.gate_line is not None else 0

    percent = round(percent, 2)

    if not arguments.skip_post:
        _post("success", f"{percent}% line coverage")
        print(f"posted pr-crew/coverage success: {percent}% line coverage")

    if arguments.summary:
        branch_percent = (
            round(_branch_percent_from_cobertura(arguments.cobertura), 2)
            if arguments.cobertura
            else 0.0
        )
        _write_summary(percent, branch_percent)

    if arguments.gate_line is not None:
        if percent < arguments.gate_line:
            print(
                f"coverage gate FAILED: {percent}% line < {arguments.gate_line}% threshold",
                file=sys.stderr,
            )
            return 1
        print(
            f"coverage gate passed: {percent}% line >= {arguments.gate_line}% threshold"
        )

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
