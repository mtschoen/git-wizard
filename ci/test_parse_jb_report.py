"""Contracts for ci/parse-jb-report.py, the manual tool that locates jb inspectcode findings."""

import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT / "ci" / "parse-jb-report.py"
FIXTURE = ROOT / "ci" / "fixtures" / "jb-report-two-findings.sarif.json"


def run_tool(*arguments):
    return subprocess.run(
        [sys.executable, str(TOOL), *arguments],
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )


class ParseJbReportTests(unittest.TestCase):
    def test_prints_each_finding_as_uri_and_line(self):
        result = run_tool(str(FIXTURE), "--max", "2")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        lines = [line.strip() for line in result.stdout.splitlines()]
        self.assertIn("GitWizard/Alpha.cs:12", lines)
        self.assertIn("GitWizardUI/Beta.cs:34", lines)
        self.assertIn("jb inspectcode: 2 finding(s)", lines)

    def test_default_maximum_of_zero_fails_on_findings(self):
        result = run_tool(str(FIXTURE))
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)

    def test_maximum_below_count_fails(self):
        result = run_tool(str(FIXTURE), "--max", "1")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)

    def test_missing_report_exits_two(self):
        result = run_tool(str(FIXTURE.with_name("absent.json")))
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
