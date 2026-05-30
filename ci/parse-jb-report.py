#!/usr/bin/env python3
"""Gate CI on JetBrains InspectCode (jb inspectcode) findings.

Reads the SARIF report jb writes (despite the .xml extension it is SARIF JSON),
prints a per-rule summary, and exits non-zero when the finding count exceeds the
allowed maximum. The lint gate's bar is zero, so --max defaults to 0.

Usage:
    python3 ci/parse-jb-report.py <report.xml> [--max N]
"""
import argparse
import collections
import json
import sys


def main() -> int:
    parser = argparse.ArgumentParser(description="Gate CI on jb inspectcode findings.")
    parser.add_argument("report", help="Path to the jb inspectcode SARIF report.")
    parser.add_argument("--max", type=int, default=0,
                        help="Maximum allowed findings before failing (default: 0).")
    arguments = parser.parse_args()

    try:
        with open(arguments.report, encoding="utf-8") as report_file:
            data = json.load(report_file)
    except (OSError, json.JSONDecodeError) as error:
        print(f"::error::Could not read jb report '{arguments.report}': {error}")
        return 2

    results = data["runs"][0]["results"]
    by_rule = collections.Counter(result["ruleId"] for result in results)

    print(f"jb inspectcode: {len(results)} finding(s)")
    for rule, count in by_rule.most_common():
        print(f"  {count:>4}  {rule}")
        for result in results:
            if result["ruleId"] != rule:
                continue
            location = result["locations"][0]["physicalLocation"]
            uri = location["artifactLocation"]["uri"]
            line = location.get("region", {}).get("startLine", "?")
            print(f"          {uri}:{line}")

    if len(results) > arguments.max:
        print(f"::error::jb inspectcode reported {len(results)} finding(s); "
              f"the gate allows at most {arguments.max}.")
        return 1

    print(f"jb inspectcode gate passed ({len(results)} <= {arguments.max}).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
