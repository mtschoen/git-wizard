"""Checkout decisions use git results, never guesses after a failed probe."""

import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import checkout


class CheckoutTests(unittest.TestCase):
    def test_cli_writes_lowercase_output_without_bom(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "output"
            with (
                patch("sys.argv", ["checkout.py", "pin-only"]),
                patch.dict(
                    os.environ,
                    {
                        "GITHUB_EVENT_NAME": "workflow_dispatch",
                        "GITHUB_OUTPUT": str(output),
                    },
                ),
            ):
                self.assertEqual(0, checkout.main())
            self.assertEqual(
                b"pin_only=false\n", output.read_bytes().replace(b"\r\n", b"\n")
            )

    def test_workflows_reuse_shared_checkout_and_native_recipes(self):
        root = Path(__file__).resolve().parents[1]
        workflow = (root / ".gitea/workflows/ci.yml").read_text(encoding="utf-8")
        self.assertEqual(2, workflow.count("ci/checkout.py pin-only"))
        self.assertEqual(2, workflow.count("ci/checkout.py clean"))
        self.assertNotRegex(workflow, r"(?m)^[ \t]+git clean -ffxd[ \t]*$")
        for path in ("release.yml", "preview.yml", "screenshot.yml"):
            source = (root / ".gitea/workflows" / path).read_text(encoding="utf-8")
            self.assertIn("bash ci/gates.sh native", source)
            self.assertNotIn("dotnet build external", source)
            self.assertNotIn("cmake --build external", source)
        self.assertIn(
            "bash ci/gates.sh native",
            (root / ".preview/up").read_text(encoding="utf-8"),
        )
        screenshot = (root / ".gitea/workflows/screenshot.yml").read_text(
            encoding="utf-8"
        )
        self.assertNotIn("-c Debug", screenshot)
        release = (root / ".gitea/workflows/release.yml").read_text(encoding="utf-8")
        self.assertEqual(2, release.count("-getProperty:Version"))

    def test_pin_only_requires_a_successful_exact_single_file_diff(self):
        for changed, status, expected in (
            (".aislop/fork-commit\n", 0, True),
            (".aislop/fork-commit\nREADME.md\n", 0, False),
            ("", 0, False),
            (".aislop/fork-commit\n", 1, False),
        ):
            with (
                self.subTest(changed=changed, status=status),
                patch.dict(
                    os.environ,
                    {"GITHUB_EVENT_NAME": "pull_request", "GITHUB_BASE_REF": "main"},
                ),
                patch.object(
                    checkout,
                    "git",
                    side_effect=[
                        subprocess.CompletedProcess([], 0, ""),
                        subprocess.CompletedProcess([], status, changed),
                    ],
                ),
            ):
                self.assertEqual(expected, checkout.pin_only())

    def test_fetch_failure_never_skips_job(self):
        with (
            patch.dict(
                os.environ,
                {
                    "GITHUB_EVENT_NAME": "push",
                    "EVENT_BEFORE": "a" * 40,
                    "EVENT_AFTER": "b" * 40,
                },
            ),
            patch.object(
                checkout, "git", return_value=subprocess.CompletedProcess([], 1, "")
            ),
        ):
            self.assertFalse(checkout.pin_only())

    def test_push_diff_and_first_push(self):
        with patch.dict(
            os.environ,
            {
                "GITHUB_EVENT_NAME": "push",
                "EVENT_BEFORE": "a" * 40,
                "EVENT_AFTER": "b" * 40,
            },
        ):
            with patch.object(
                checkout,
                "git",
                side_effect=[
                    subprocess.CompletedProcess([], 0, ""),
                    subprocess.CompletedProcess([], 0, ".aislop/fork-commit\n"),
                ],
            ):
                self.assertTrue(checkout.pin_only())
            with patch.dict(os.environ, {"EVENT_BEFORE": "0" * 40}):
                self.assertFalse(checkout.pin_only())

    def test_clean_requires_successful_clean_and_empty_successful_status(self):
        for clean_status, status_status, output, expected in (
            (0, 0, "", 0),
            (1, 0, "", 1),
            (0, 1, "", 1),
            (0, 0, " M tracked\n", 1),
        ):
            with (
                self.subTest(clean=clean_status, status=status_status, output=output),
                patch.object(
                    checkout,
                    "git",
                    side_effect=[
                        subprocess.CompletedProcess([], clean_status, "", ""),
                        subprocess.CompletedProcess([], status_status, output, ""),
                    ],
                ),
            ):
                self.assertEqual(expected, checkout.clean_tree())
