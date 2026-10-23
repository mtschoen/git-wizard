"""Contracts for repository developer tools, inventory, and syntax checks."""

import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from developer_tools import script_paths, validate_inventory

ROOT = Path(__file__).resolve().parents[1]


class DeveloperToolsTests(unittest.TestCase):
    def test_repository_inventory(self):
        validate_inventory(ROOT)

    def test_inventory_rejects_drift(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "scripts/nested").mkdir(parents=True)
            (root / "docs").mkdir()
            (root / "scripts/nested/tool.py").write_text("pass\n")
            document = root / "docs/maintained-developer-tools.md"
            row = "| `scripts/nested/tool.py` | Purpose | Developer | Parse |\n"
            for body, message in (
                ("", "unlisted"),
                (row + row, "duplicate"),
                (
                    row + "| `scripts/gone.sh` | Purpose | Developer | Parse |\n",
                    "stale",
                ),
                ("| `scripts/nested/tool.py` | Purpose | | Parse |\n", "incomplete"),
            ):
                with self.subTest(message=message):
                    document.write_text(body)
                    with self.assertRaisesRegex(ValueError, message):
                        validate_inventory(root)
            document.write_text(row)
            validate_inventory(root)
            self.assertEqual({"scripts/nested/tool.py"}, script_paths(root))

    def test_shell_and_python_syntax(self):
        bash = shutil.which("bash") or "bash"
        with tempfile.TemporaryDirectory() as cache:
            for relative in sorted(script_paths(ROOT)):
                path = ROOT / relative
                if path.suffix.lower() == ".sh":
                    command = [bash, "-n", str(path)]
                elif path.suffix.lower() == ".py":
                    command = [
                        sys.executable,
                        "-X",
                        f"pycache_prefix={cache}",
                        "-m",
                        "py_compile",
                        str(path),
                    ]
                else:
                    continue
                with self.subTest(path=relative):
                    result = subprocess.run(
                        command, capture_output=True, text=True, check=False
                    )
                    self.assertEqual(
                        0, result.returncode, result.stdout + result.stderr
                    )

    def require_powershell(self):
        executable = shutil.which("pwsh")
        if not executable and os.name == "nt":
            self.fail("Windows contracts require PowerShell 7 on PATH")
        if not executable:
            self.skipTest(
                "PowerShell syntax and helper checks are enforced by Windows CI"
            )
        return executable

    def test_powershell_tools(self):
        executable = self.require_powershell()
        result = subprocess.run(
            [
                executable,
                "-NoProfile",
                "-NonInteractive",
                "-File",
                str(ROOT / "ci/check-developer-tools.ps1"),
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_powershell_gate_rejects_parse_and_self_test_failures(self):
        executable = self.require_powershell()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "scripts/nested").mkdir(parents=True)
            (root / "ci/script-tests").mkdir(parents=True)
            driver = root / "ci/check-developer-tools.ps1"
            shutil.copyfile(ROOT / "ci/check-developer-tools.ps1", driver)
            source = root / "scripts/nested/tool.ps1"
            check = root / "ci/script-tests/tool.tests.ps1"
            for tool, test, marker in (
                ("function Broken {", "throw 'must not execute'", "Parse failed"),
                ("$value = 1", "throw 'self-test sentinel'", "self-test sentinel"),
                ("$value = 1", "exit 7", "Self-test failed"),
                ("$value = 1", "function Broken {", "Parse failed"),
            ):
                with self.subTest(marker=marker, test=test):
                    source.write_text(tool)
                    check.write_text(test)
                    result = subprocess.run(
                        [
                            executable,
                            "-NoProfile",
                            "-NonInteractive",
                            "-File",
                            str(driver),
                        ],
                        capture_output=True,
                        text=True,
                        check=False,
                    )
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn(marker, result.stdout + result.stderr)
                    if tool == "function Broken {":
                        self.assertNotIn(
                            "must not execute", result.stdout + result.stderr
                        )

    def test_screenshot_workflows_use_bounded_fixture_wrapper(self):
        for name, output in (
            ("ci.yml", "artifacts/GitWizardUI-ci.png"),
            ("screenshot.yml", "Screenshots/GitWizardUI.png"),
        ):
            with self.subTest(workflow=name):
                source = (ROOT / ".gitea/workflows" / name).read_text()
                steps = source.split("      - name: ")[1:]
                captures = [
                    step
                    for step in steps
                    if "-File scripts/update-screenshots.ps1" in step
                ]
                self.assertEqual(1, len(captures))
                self.assertIn("timeout-minutes: 10", captures[0])
                self.assertIn("-TimeoutSeconds 180", captures[0])
                self.assertIn(f"-OutputPath {output}", captures[0])
                uploads = [
                    step
                    for step in steps
                    if f"path: {output}" in step and "gitea-upload-artifact@" in step
                ]
                self.assertEqual(1, len(uploads))
                self.assertIn("if-no-files-found: error", uploads[0])
                self.assertNotIn("always()", uploads[0])
                self.assertNotIn("continue-on-error: true", uploads[0])
                self.assertNotIn("dotnet run --project GitWizardUI.Screenshot", source)

    def test_maintained_tools_are_linked_from_agent_instructions(self):
        instructions = (ROOT / "AGENTS.md").read_text()
        self.assertIn("## Maintained developer tools", instructions)
        self.assertIn("docs/maintained-developer-tools.md", instructions)
        self.assertIn("Only redundant code is deleted.", instructions)
        manual = (ROOT / "docs/manual-test-plan.md").read_text()
        self.assertIn("maintained-developer-tools.md", manual)
