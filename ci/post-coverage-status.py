#!/usr/bin/env python3
"""Post the pr-crew/coverage commit status from CI (stdlib only).

Computes line-coverage percent and POSTs it as a Gitea commit status
(context=pr-crew/coverage) on $GITHUB_SHA using the auto $GITHUB_TOKEN.

Percent source:
  default                -> pytest-cov coverage.json ['totals']['percent_covered']
  --cobertura "<glob>"   -> merge Cobertura XML line hits across matched files
                            (a line counts covered if any report shows hits>0)

On any measurement failure it posts state=error (pr-crew then reads the gate
as 'unreadable', not silently missing) and still exits 0 so an `if: always()`
step does not double-fail the job. A POST/network failure DOES raise.
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


def _percent_from_cobertura(patterns: list[str]) -> float:
    paths = [
        path
        for pattern in patterns
        for path in glob.glob(pattern, recursive=True)
        if "/bin/" not in path and "/obj/" not in path
    ]
    if not paths:
        raise FileNotFoundError("no Cobertura XML matched")
    lines: dict[tuple[str, str], bool] = {}
    for path in paths:
        for class_node in ElementTree.parse(path).getroot().iter("class"):
            filename = class_node.get("filename", "")
            for line_node in class_node.iter("line"):
                key = (filename, line_node.get("number", ""))
                lines[key] = lines.get(key, False) or int(line_node.get("hits", "0")) > 0
    if not lines:
        raise ValueError("no source lines in Cobertura XML")
    return 100.0 * sum(1 for covered in lines.values() if covered) / len(lines)


def _post(state: str, description: str) -> None:
    server = os.environ["GITHUB_SERVER_URL"]
    repository = os.environ["GITHUB_REPOSITORY"]
    sha = os.environ["GITHUB_SHA"]
    run_id = os.environ.get("GITHUB_RUN_ID", "")
    body = json.dumps({
        "context": "pr-crew/coverage",
        "state": state,
        "description": description,
        "target_url": f"{server}/{repository}/actions/runs/{run_id}",
    }).encode()
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
    arguments = parser.parse_args(argv[1:])
    try:
        if arguments.cobertura:
            percent = _percent_from_cobertura(arguments.cobertura)
        else:
            percent = _percent_from_coverage_json(arguments.coverage_json)
    except Exception as error:  # measurement failed -> post error, exit 0
        print(f"coverage measurement failed: {error}", file=sys.stderr)
        _post("error", "coverage measurement failed")
        return 0
    percent = round(percent, 2)
    _post("success", f"{percent}% line coverage")
    print(f"posted pr-crew/coverage success: {percent}% line coverage")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
