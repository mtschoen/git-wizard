import argparse
import importlib.util
import io
import os
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stderr
from pathlib import Path
from unittest.mock import patch
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[1]
GENERATOR = ROOT / "scripts/generate_test_report.py"
SPEC = importlib.util.spec_from_file_location("generate_test_report", GENERATOR)
REPORT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(REPORT)
COMMIT = "1a7721db4f2824a05203165c9cd4589bfbc15708"
TIMESTAMP = "2026-11-22T11:18:47Z"


def trx_text(passed, failed, skipped, skipped_counter=0, project="GitWizardTests"):
    outcomes = "".join(
        f'<UnitTestResult outcome="{outcome}"/>' * count
        for outcome, count in [
            ("Passed", passed),
            ("Failed", failed),
            ("NotExecuted", skipped),
        ]
    )
    return (
        '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
        '<TestDefinitions><UnitTest storage="other.dll">'
        f'<TestMethod codeBase="build\\fixtures\\{project}.dll"/></UnitTest></TestDefinitions>'
        f"<Results>{outcomes}</Results><ResultSummary>"
        f'<Counters total="{passed + failed + skipped}" passed="{passed}" failed="{failed}" '
        f'notExecuted="{skipped_counter}"/></ResultSummary></TestRun>'
    )


class GenerateTestReportTests(unittest.TestCase):
    def setUp(self):
        self.directory = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.windows = self.directory / "Windows.trx"
        self.linux = self.directory / "Linux.trx"
        self.coverage = self.directory / "coverage.cobertura.xml"
        self.output = self.directory / "TEST-REPORT.md"
        self.windows.write_text(trx_text(9, 1, 2, skipped_counter=2), encoding="utf-8")
        self.linux.write_text(trx_text(9, 1, 2), encoding="utf-8")
        self.coverage.write_text(
            '<coverage line-rate="0.899" branch-rate="0.8504"/>', encoding="utf-8"
        )

    def arguments(self, **overrides):
        values = {
            "windows_trx": [str(self.windows)],
            "windows_skipped": False,
            "linux_trx": [str(self.linux)],
            "linux_cobertura": [str(self.coverage)],
            "commit": COMMIT,
            "run_id": "60794",
            "generated_at": TIMESTAMP,
            "output_path": self.output,
        } | overrides
        return argparse.Namespace(**values)

    def command_arguments(self, **overrides):
        arguments = {
            "windows-trx": [self.windows],
            "linux-trx": [self.linux],
            "linux-cobertura": [self.coverage],
            "commit": [COMMIT],
            "run-id": ["60794"],
            "generated-at": [TIMESTAMP],
            "output-path": [self.output],
        } | overrides
        command = [str(GENERATOR)]
        for option, values in arguments.items():
            if values is not None:
                command.extend([f"--{option}", *map(str, values)])
        return command

    def run_main(self, **overrides):
        stderr = io.StringIO()
        with (
            patch.object(sys, "argv", self.command_arguments(**overrides)),
            redirect_stderr(stderr),
        ):
            try:
                returncode = REPORT.main()
            except SystemExit as exit_error:
                returncode = exit_error.code
        return returncode, stderr.getvalue()

    def cli_report(self, **overrides):
        if self.output.exists():
            self.output.unlink()
        result = subprocess.run(
            [sys.executable, *self.command_arguments(**overrides)],
            cwd=self.directory,
            capture_output=True,
            text=True,
            check=False,
            env=os.environ | {"LC_ALL": "C"},
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        return self.output.read_bytes()

    def report(self, **overrides):
        if self.output.exists():
            self.output.unlink()
        REPORT.generate_report(self.arguments(**overrides))
        return self.output.read_bytes()

    def test_deterministic_report_uses_outcomes_and_invariant_percentages(self):
        first = self.report(generated_at="2026-11-22T13:18:47.12345+02:00")
        self.assertEqual(first, self.cli_report())
        self.assertEqual(first.count(b"| GitWizardTests | 12 | 9 | 1 | 2 |"), 2)
        self.assertIn(b"| coverage.cobertura.xml | 89.90% | 85.04% |", first)
        self.assertIn(f"Source commit: {COMMIT}\nWorkflow run: 60794\n".encode(), first)
        self.assertIn(
            f"Generated at: {TIMESTAMP} (workflow run creation time).".encode(), first
        )
        self.assertIn(
            b"generated on main by CI; must not be edited in a pull request", first
        )
        self.assertIn(b"## Windows\n", first)
        self.assertIn(b"## Linux\n", first)
        self.assertNotIn(b"\r", first)
        self.assertNotIn("\u2014".encode(), first)
        self.assertNotRegex(first, rb"https?://|(?i:build[\\/]fixtures)")
        self.assertLess(len(first), 4096)

    def test_windows_skip_is_explicit_and_leaves_linux_tables(self):
        report = self.report(windows_trx=None, windows_skipped=True)
        self.assertIn(b"## Windows\n\nWindows CI did not run.\n\n## Linux\n", report)
        self.assertEqual(report.count(b"| GitWizardTests | 12 | 9 | 1 | 2 |"), 1)
        self.assertIn(b"| coverage.cobertura.xml | 89.90% | 85.04% |", report)

    def test_rejects_missing_duplicate_and_conflicting_inputs(self):
        for overrides in (
            {"windows-trx": None},
            {"windows-trx": [self.windows, self.windows]},
            {"windows-skipped": []},
            {"linux-trx": None},
            {"linux-cobertura": None},
        ):
            with self.subTest(overrides=overrides):
                returncode, stderr = self.run_main(**overrides)
                self.assertNotEqual(returncode, 0)
                self.assertTrue(stderr)
                self.assertFalse(self.output.exists())
        for overrides in (
            {"windows_trx": None},
            {"linux_trx": None},
            {"linux_cobertura": None},
        ):
            with self.subTest(direct_overrides=overrides):
                with self.assertRaises(ValueError):
                    REPORT.generate_report(self.arguments(**overrides))
                self.assertFalse(self.output.exists())

    def test_rejects_bad_provenance(self):
        for option, value in [
            ("commit", "invalid"),
            ("run_id", "run-42"),
            ("generated_at", "2026-11-22T11:18:47"),
        ]:
            with self.subTest(option=option):
                with self.assertRaises(ValueError):
                    REPORT.generate_report(self.arguments(**{option: value}))
                self.assertFalse(self.output.exists())

    def test_rejects_bad_coverage(self):
        for text in (
            "<broken",
            "<coverage/>",
            '<coverage line-rate="1.01" branch-rate="0.5"/>',
            '<coverage line-rate="NaN" branch-rate="0.5"/>',
        ):
            with self.subTest(text=text):
                self.coverage.write_text(text, encoding="utf-8")
                with self.assertRaises((ValueError, ElementTree.ParseError)):
                    REPORT.generate_report(self.arguments())
                self.assertFalse(self.output.exists())

    def test_rejects_bad_trx(self):
        valid = trx_text(9, 1, 2)
        for text in (
            "<TestRun/>",
            valid.replace('total="12"', 'total="bad"'),
            valid.replace('total="12"', 'total="13"'),
            valid.replace('passed="9"', 'passed="8"'),
            valid.replace('failed="1"', 'failed="0"'),
            valid.replace('outcome="Failed"', 'outcome="Timeout"'),
        ):
            with self.subTest(text=text[-180:]):
                self.linux.write_text(text, encoding="utf-8")
                with self.assertRaises(ValueError):
                    REPORT.generate_report(self.arguments())
                self.assertFalse(self.output.exists())

    def test_observed_trx_shapes_with_zero_not_executed_counter(self):
        for passed, skipped in ((1725, 80), (1778, 27)):
            with self.subTest(passed=passed, skipped=skipped):
                self.linux.write_text(trx_text(passed, 0, skipped), encoding="utf-8")
                self.assertIn(
                    f"| GitWizardTests | 1805 | {passed} | 0 | {skipped} |".encode(),
                    self.report(),
                )

    def test_escapes_markdown_cells_and_sorts_report_input_paths(self):
        second = self.directory / "another.trx"
        second.write_text(
            trx_text(1, 0, 0, project="&amp;&lt;&gt;|&#10;Test"), encoding="utf-8"
        )
        report = self.report(windows_trx=[str(self.windows), str(second)])
        self.assertIn(
            b"| &amp;&lt;&gt;&#124; Test | 1 | 1 | 0 | 0 |\n| GitWizardTests |", report
        )

    def test_rejects_oversized_reports_and_em_dashes(self):
        for project in ("X" * 4096, "X\u2014Y"):
            with self.subTest(length=len(project)):
                self.linux.write_text(
                    trx_text(1, 0, 0, project=project), encoding="utf-8"
                )
                with self.assertRaises(ValueError):
                    REPORT.generate_report(self.arguments())
                self.assertFalse(self.output.exists())
