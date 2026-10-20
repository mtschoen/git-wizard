import importlib.util
import io
import subprocess
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("format_on_save", ROOT / "scripts/format-on-save.py")
HOOK = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HOOK)


class FormatOnSaveTests(unittest.TestCase):
    def test_payload_paths(self):
        cases = [
            (None, None),
            ({"tool_input": 1, "tool_response": []}, None),
            ({"tool_input": {"file_path": "a.cs", "TargetFile": "b.cs"}}, "a.cs"),
            ({"tool_input": {"TargetFile": "b.cs"}}, "b.cs"),
            ({"tool_input": {"targetFile": "c.cs"}}, "c.cs"),
            ({"tool_response": {"filePath": "d.cs"}}, "d.cs"),
        ]
        for payload, expected in cases:
            with self.subTest(payload=payload):
                self.assertEqual(expected, HOOK.extract_file_path(payload))

    def test_formatter_outcomes(self):
        source = str(ROOT / "GitWizard/Example.cs")
        for status, expected_exit in ((0, 0), (7, 2)):
            with self.subTest(status=status):
                output, errors = io.StringIO(), io.StringIO()
                with (
                    patch.object(HOOK, "find_repository_root", return_value=str(ROOT)),
                    patch.object(HOOK.os.path, "isfile", return_value=True),
                    patch.object(HOOK.shutil, "which", return_value="dotnet"),
                    patch.object(HOOK.subprocess, "run", return_value=subprocess.CompletedProcess([], status, "details", "")) as execute,
                    redirect_stdout(output), redirect_stderr(errors),
                    self.assertRaises(SystemExit) as raised,
                ):
                    HOOK.run_dotnet_format(source)
                self.assertEqual(expected_exit, raised.exception.code)
                self.assertEqual([
                    "dotnet", "format", "whitespace", str(ROOT / "git-wizard.slnx"),
                    "--include", source, "--no-restore",
                ], execute.call_args.args[0])
                self.assertIn("applied" if status == 0 else "FAILED", output.getvalue() + errors.getvalue())
                if status:
                    self.assertEqual("", output.getvalue())
                    self.assertIn("details", errors.getvalue())
