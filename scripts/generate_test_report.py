"""Generate a deterministic Markdown summary of CI test artifacts."""

import argparse
import re
import subprocess
import sys
from collections import Counter
from datetime import UTC, datetime
from decimal import ROUND_HALF_EVEN, Decimal, localcontext
from pathlib import Path
from xml.etree import ElementTree


def test_rows(path):
    root = ElementTree.parse(path).getroot()
    counters = root.findall("{*}ResultSummary/{*}Counters")
    if root.tag.split("}")[-1] != "TestRun" or len(counters) != 1:
        raise ValueError("Missing or duplicate TRX Counters")

    counts = {}
    for name in ("total", "passed", "failed"):
        text = counters[0].get(name, "")
        if not re.fullmatch(r"[0-9]+", text) or int(text) > 2**63 - 1:
            raise ValueError(f"Invalid TRX counter: {name}")
        counts[name] = int(text)

    outcomes = Counter()
    for result in root.findall("{*}Results/{*}UnitTestResult"):
        outcome = result.get("outcome", "").casefold()
        if outcome not in {"passed", "failed", "notexecuted"}:
            raise ValueError(f"Unsupported TRX result outcome: {outcome}")
        outcomes[outcome] += 1
    if (outcomes["passed"], outcomes["failed"], sum(outcomes.values())) != (
        counts["passed"],
        counts["failed"],
        counts["total"],
    ):
        raise ValueError("TRX counters do not reconcile with result outcomes")

    definitions = root.findall("{*}TestDefinitions/{*}UnitTest")
    methods = [
        method
        for definition in definitions
        for method in definition.findall("{*}TestMethod")
    ]
    sources = (
        [method.get("codeBase", "") for method in methods]
        if methods
        else [definition.get("storage", "") for definition in definitions]
    )
    projects = {}
    for source in sources:
        project = source.replace("\\", "/").rsplit("/", 1)[-1].rpartition(".")
        name = project[0] if project[1] else project[2]
        projects.setdefault(name.casefold(), name)
    if len(projects) != 1 or not next(iter(projects)).strip():
        raise ValueError(
            "TRX must identify exactly one test project in UnitTest definitions"
        )
    return (
        next(iter(projects.values())),
        counts["total"],
        counts["passed"],
        counts["failed"],
        outcomes["notexecuted"],
    )


def coverage_percentage(text):
    if not re.fullmatch(r"[0-9]+(?:\.[0-9]+)?", text) or not 0 <= Decimal(text) <= 1:
        raise ValueError("Invalid Cobertura rate")
    with localcontext() as context:
        context.rounding = ROUND_HALF_EVEN
        return f"{Decimal(text) * 100:.2f}%"


def coverage_row(path):
    root = ElementTree.parse(path).getroot()
    if root.tag != "coverage":
        raise ValueError("Missing Cobertura coverage root")
    return (
        path.name,
        coverage_percentage(root.get("line-rate", "")),
        coverage_percentage(root.get("branch-rate", "")),
    )


def markdown_row(values):
    cells = [
        str(value).translate(
            str.maketrans(
                {
                    "&": "&amp;",
                    "<": "&lt;",
                    ">": "&gt;",
                    "|": "&#124;",
                    "\r": " ",
                    "\n": " ",
                }
            )
        )
        for value in values
    ]
    return "| " + " | ".join(cells) + " |"


def ordered_paths(paths):
    if len({path.casefold() for path in paths}) != len(paths):
        raise ValueError("Duplicate report input")
    return [Path(path) for path in sorted(paths, key=str.casefold)]


def platform_lines(name, test_paths, coverage_paths=()):
    lines = [
        f"## {name}",
        "",
        "| Test project | Total | Passed | Failed | Skipped |",
        "| --- | ---: | ---: | ---: | ---: |",
        *(markdown_row(test_rows(path)) for path in test_paths),
        "",
    ]
    if coverage_paths:
        lines.extend(
            [
                "| Managed coverage | Line | Branch |",
                "| --- | ---: | ---: |",
                *(markdown_row(coverage_row(path)) for path in coverage_paths),
                "",
            ]
        )
    return lines


def utc_timestamp(text):
    if not re.fullmatch(
        r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}"
        r"(?:\.[0-9]+)?(?:Z|[+-](?:0[0-9]|1[0-4]):[0-5][0-9])",
        text,
    ):
        raise ValueError(
            "An ISO 8601 run timestamp with an explicit offset is required"
        )
    timestamp = datetime.fromisoformat(text)
    if abs(timestamp.utcoffset().total_seconds()) > 14 * 3600:
        raise ValueError("Invalid ISO 8601 offset")
    return (
        timestamp.astimezone(UTC).isoformat(timespec="seconds").replace("+00:00", "Z")
    )


def generate_report(arguments):
    if not re.fullmatch(r"[0-9a-f]{40}", arguments.commit):
        raise ValueError("Expected a full source commit SHA")
    if not re.fullmatch(r"[0-9]+", arguments.run_id):
        raise ValueError("Expected a numeric workflow run id")
    if not arguments.linux_trx or not arguments.linux_cobertura:
        raise ValueError("Missing inputs for Linux")
    if not arguments.windows_skipped and not arguments.windows_trx:
        raise ValueError("Missing inputs for Windows")

    windows_lines = (
        ["## Windows", "", "Windows CI did not run.", ""]
        if arguments.windows_skipped
        else platform_lines("Windows", ordered_paths(arguments.windows_trx))
    )
    lines = [
        "# Test report: generated on main by CI; must not be edited in a pull request",
        "",
        f"Source commit: {arguments.commit}",
        f"Workflow run: {arguments.run_id}",
        f"Generated at: {utc_timestamp(arguments.generated_at)} (workflow run creation time).",
        "",
        *windows_lines,
        *platform_lines(
            "Linux",
            ordered_paths(arguments.linux_trx),
            ordered_paths(arguments.linux_cobertura),
        ),
        "Full TRX and coverage reports are retained as CI artifacts.",
        "",
    ]
    content = "\n".join(lines).encode("utf-8")
    if len(content) >= 4096:
        raise ValueError("Test report exceeds the 4 KB summary budget")
    if "\u2014".encode() in content:
        raise ValueError("Test report contains an em dash")
    arguments.output_path.write_bytes(content)


def main():
    if len(sys.argv) == 1:
        return subprocess.call(
            [
                sys.executable,
                "-m",
                "unittest",
                "discover",
                "-s",
                "ci",
                "-p",
                "test_generate_test_report.py",
            ],
            cwd=Path(__file__).resolve().parents[1],
        )
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    windows = parser.add_mutually_exclusive_group(required=True)
    windows.add_argument("--windows-trx", nargs="+")
    windows.add_argument("--windows-skipped", action="store_true")
    parser.add_argument("--linux-trx", nargs="+", required=True)
    parser.add_argument("--linux-cobertura", nargs="+", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--generated-at", required=True)
    parser.add_argument("--output-path", type=Path, default=Path("TEST-REPORT.md"))
    arguments = parser.parse_args()
    try:
        generate_report(arguments)
    except (ValueError, OverflowError, OSError, ElementTree.ParseError) as error:
        parser.exit(1, f"{parser.prog}: {error}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
