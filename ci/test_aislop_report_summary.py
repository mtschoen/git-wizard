"""Contracts for ci/aislop-report-summary.js, which makes a red aislop gate explain itself."""

import os
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "ci" / "aislop-report-summary.js"
MISSING_TOOLS_REPORT = ROOT / "ci" / "fixtures" / "aislop-report-missing-tools.json"


def run_summary(report, *arguments):
    return subprocess.run(
        ["node", str(SCRIPT), *arguments],
        capture_output=True,
        text=True,
        encoding="utf-8",
        env={**os.environ, "REPORT": str(report)},
        check=False,
    )


class AislopReportSummaryTests(unittest.TestCase):
    def test_lists_missing_tools_and_engine_failures(self):
        result = run_summary(MISSING_TOOLS_REPORT)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        lines = result.stdout.splitlines()
        self.assertIn("aislop missing tools: clang-format", lines)
        self.assertIn("aislop engine failure: ai-slop git ls-files .: exit 128", lines)
        self.assertIn("aislop findings by engine/severity: none", lines)

    def test_keeps_the_engine_line_format(self):
        result = run_summary(MISSING_TOOLS_REPORT)
        self.assertIn("aislop engine format: ran, 0 issues, 1.5s", result.stdout.splitlines())

    def test_exit_reason_names_missing_tools_at_a_perfect_score(self):
        result = run_summary(MISSING_TOOLS_REPORT, "--exit-reason", "100")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(
            result.stdout.strip(),
            "aislop gate: exited nonzero at score 100 (failBelow 100); missing tools: clang-format; "
            "engine failures: ai-slop git ls-files .: exit 128; failed engines: ai-slop",
        )

    def test_exit_reason_is_silent_when_score_is_below_fail_below(self):
        result = run_summary(MISSING_TOOLS_REPORT, "--exit-reason", "101")
        self.assertEqual(result.stdout.strip(), "")


if __name__ == "__main__":
    unittest.main()
