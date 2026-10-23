"""Contracts for scripts/verify-windows.sh without reaching a remote host."""

import os
import re
import shutil
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

from bash_resolution import resolve_bash

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "verify-windows.sh"
try:
    BASH = resolve_bash()
except FileNotFoundError:
    BASH = None
PWSH = shutil.which("pwsh")


def write_stub(path, body):
    path.write_text("#!/usr/bin/env bash\n" + body, encoding="utf-8", newline="\n")
    path.chmod(path.stat().st_mode | stat.S_IEXEC)


@unittest.skipUnless(BASH, "bash is required")
class VerifyWindowsTests(unittest.TestCase):
    def test_script_parses(self):
        result = subprocess.run([BASH, "-n", str(SCRIPT)], capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_missing_reference_prints_usage_and_fails(self):
        result = subprocess.run([BASH, str(SCRIPT)], capture_output=True, text=True, check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("usage: verify-windows.sh <ref>", result.stderr)

    def test_unresolvable_reference_fails_before_any_remote_call(self):
        result = subprocess.run(
            [BASH, str(SCRIPT), "not-a-real-ref!"],
            capture_output=True,
            text=True,
            check=False,
            env={"PATH": "/usr/bin:/bin", "HOST": "invalid-host.invalid"},
        )
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)

    def test_script_has_no_encoded_or_base64_powershell(self):
        text = SCRIPT.read_text(encoding="utf-8")
        for forbidden in ("EncodedCommand", "FromBase64String", "base64"):
            self.assertNotIn(forbidden, text)

    def test_remote_step_is_one_ssh_call_with_script_on_stdin(self):
        with tempfile.TemporaryDirectory() as stub_directory:
            stub_path = Path(stub_directory)
            capture_path = stub_path / "captured.ps1"
            log_path = stub_path / "ssh.log"
            scp_log_path = stub_path / "scp.log"
            # scp stub: records any invocation; the script must never use it.
            write_stub(stub_path / "scp", f'echo scp "$@" >> "{scp_log_path.as_posix()}"\n')
            # ssh stub: log the remote command (last argument), save stdin, and fail the step.
            write_stub(
                stub_path / "ssh",
                f'printf "%s\\n" "${{@: -1}}" >> "{log_path.as_posix()}"\n'
                f'cat > "{capture_path.as_posix()}"\necho remote-failed >&2\nexit 1\n',
            )
            environment = {
                **os.environ,
                "PATH": stub_path.as_posix() + os.pathsep + os.environ["PATH"],
                "HOST": "operator@example",
                "REMOTE": "C:/it's/checkout",
                "MFTLIB_URL": "git@example:o'brien/MFTLib.git",
            }
            result = subprocess.run(
                [BASH, str(SCRIPT), "abcdef1"],
                capture_output=True,
                text=True,
                check=False,
                env=environment,
            )

            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertIn("RESULT: FAIL (checkout)", result.stdout)
            self.assertFalse(scp_log_path.exists(), "scp must not be used")
            commands = log_path.read_text(encoding="utf-8").splitlines()
            self.assertEqual(len(commands), 1, commands)
            command = commands[0]
            prefix = 'powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "'
            self.assertTrue(command.startswith(prefix) and command.endswith('"'), command)
            bootstrap = command[len(prefix) : -1]
            self.assertNotIn("$", bootstrap)
            self.assertNotIn('"', bootstrap)
            self.assertLess(len(bootstrap), 1000)
            for forbidden in ("EncodedCommand", "FromBase64String", "Invoke-Expression", "iex", "scriptblock"):
                self.assertNotIn(forbidden, command)
            token = re.search(r"'verify-windows-([^']*)\.ps1'", bootstrap).group(1)
            self.assertRegex(token, r"^[0-9a-f]{16}$")
            self.assertEqual(bootstrap.count(f"verify-windows-{token}.ps1"), 3, bootstrap)
            self.assertIn("[Console]::InputEncoding=[Text.UTF8Encoding]::new(0)", bootstrap)
            self.assertIn("[Console]::In.ReadToEnd()", bootstrap)
            self.assertIn("[Text.UTF8Encoding]::new(1)", bootstrap)
            self.assertIn("try { [IO.File]::WriteAllText((Join-Path", bootstrap)
            self.assertIn("catch { [Console]::Error.WriteLine((Get-Variable -Name _ -ValueOnly)); exit 1 }", bootstrap)
            self.assertIn("finally { Remove-Item -LiteralPath (Join-Path", bootstrap)
            self.assertIn("exit (Get-Variable LASTEXITCODE -ValueOnly -ErrorAction Ignore)", bootstrap)
            script = capture_path.read_bytes().decode("utf-8")
            self.assertIn("$remote = 'C:/it''s/checkout'", script)
            self.assertIn("$mftlibUrl = 'git@example:o''brien/MFTLib.git'", script)
            self.assertIn("$targetRef = 'abcdef1'", script)

    def capture_bootstrap_command(self):
        with tempfile.TemporaryDirectory() as stub_directory:
            stub_path = Path(stub_directory)
            log_path = stub_path / "ssh.log"
            write_stub(
                stub_path / "ssh", f'printf "%s\\n" "${{@: -1}}" >> "{log_path.as_posix()}"\ncat >/dev/null\nexit 1\n'
            )
            environment = {**os.environ, "PATH": stub_path.as_posix() + os.pathsep + os.environ["PATH"], "HOST": "o@e"}
            subprocess.run([BASH, str(SCRIPT), "abcdef1"], capture_output=True, text=True, check=False, env=environment)
            command = log_path.read_text(encoding="utf-8").splitlines()[0]
        prefix = 'powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "'
        return command[len(prefix) : -1]

    @unittest.skipUnless(shutil.which("powershell") or PWSH, "PowerShell is required")
    def test_bootstrap_reports_script_failures_as_nonzero(self):
        shell = shutil.which("powershell") or PWSH
        bootstrap = self.capture_bootstrap_command()
        cases = {"exit 7": 7, "Write-Output ok": 0, "this is invalid (": "nonzero", "throw 'boom'": "nonzero"}
        for script, expected in cases.items():
            with self.subTest(script=script):
                result = subprocess.run(
                    [
                        shell,
                        "-NoLogo",
                        "-NoProfile",
                        "-NonInteractive",
                        "-ExecutionPolicy",
                        "Bypass",
                        "-Command",
                        bootstrap,
                    ],
                    input=script.encode("utf-8"),
                    capture_output=True,
                    check=False,
                )
                if expected == "nonzero":
                    self.assertNotEqual(result.returncode, 0, result.stderr)
                else:
                    self.assertEqual(result.returncode, expected, result.stderr)

    def test_remote_value_single_quote_delimiters_are_doubled(self):
        quotes = ("'", "\u2018", "\u2019", "\u201a", "\u201b")
        for quote in quotes:
            with self.subTest(quote=quote):
                remote_value = f"safe{quote}; Write-Output INJECTED; #"
                doubled_quote = quote * 2
                expected_assignment = f"$remote = 'safe{doubled_quote}; Write-Output INJECTED; #'"
                with tempfile.TemporaryDirectory() as stub_directory:
                    stub_path = Path(stub_directory)
                    capture_path = stub_path / "captured.ps1"
                    write_stub(stub_path / "ssh", f'cat > "{capture_path.as_posix()}"\nexit 1\n')
                    environment = {
                        **os.environ,
                        "PATH": stub_path.as_posix() + os.pathsep + os.environ["PATH"],
                        "HOST": "operator@example",
                        "REMOTE": remote_value,
                    }
                    subprocess.run(
                        [BASH, str(SCRIPT), "abcdef1"],
                        capture_output=True,
                        text=True,
                        check=False,
                        env=environment,
                    )
                    script = capture_path.read_bytes().decode("utf-8")
                    self.assertIn(expected_assignment, script)

                    if PWSH:
                        eval_script = stub_path / "eval.ps1"
                        output_path = stub_path / "output.txt"
                        eval_script.write_text(
                            f"{expected_assignment}\n"
                            f"[System.IO.File]::WriteAllText($args[0], $remote, [System.Text.Encoding]::UTF8)\n",
                            encoding="utf-8-sig",
                        )
                        eval_result = subprocess.run(
                            [
                                PWSH,
                                "-NoProfile",
                                "-File",
                                str(eval_script),
                                str(output_path),
                            ],
                            capture_output=True,
                            text=True,
                            check=False,
                        )
                        self.assertEqual(eval_result.returncode, 0, eval_result.stderr)
                        self.assertNotIn("INJECTED", eval_result.stdout)
                        self.assertEqual(output_path.read_text(encoding="utf-8-sig"), remote_value)


if __name__ == "__main__":
    unittest.main()
