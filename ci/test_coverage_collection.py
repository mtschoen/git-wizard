"""Behavioral contract: every coverage-collecting test gate extends vstest's testhost shutdown wait.

vstest.console kills the testhost 100 ms after ending the session and only then lets the
Coverlet collector read the hits files, so a hits file written at process exit can be cut
short (docs/testing.md, Coverage collection). The fake dotnet below is the executable
boundary: it records the environment and arguments the real gate recipe hands it.
"""

import os
import subprocess
import tempfile
import unittest
from pathlib import Path

from bash_resolution import resolve_bash

ROOT = Path(__file__).resolve().parents[1]
REQUIRED_TIMEOUT = "120000"
try:
    BASH = resolve_bash()
except FileNotFoundError:
    BASH = None

RECORDING_DOTNET = """#!/usr/bin/env bash
printf '%s\\n' "${VSTEST_TESTHOST_SHUTDOWN_TIMEOUT-unset}" > "$RECORD_DIRECTORY/timeout"
printf '%s\\n' "$@" > "$RECORD_DIRECTORY/arguments"
"""


@unittest.skipUnless(BASH, "bash is required")
class CoverageCollectionTests(unittest.TestCase):
    def test_test_gate_extends_shutdown_wait_over_caller_value(self):
        scratch = ROOT / "artifacts"
        scratch.mkdir(exist_ok=True)
        directory = Path(self.enterContext(tempfile.TemporaryDirectory(dir=scratch)))
        executables = directory / "executables"
        executables.mkdir()
        executable = executables / "dotnet"
        executable.write_text(RECORDING_DOTNET, newline="\n")
        executable.chmod(0o755)
        for caller_value in (None, "100"):
            with self.subTest(caller_value=caller_value):
                environment = dict(
                    os.environ,
                    PATH=f"{executables}{os.pathsep}{os.environ['PATH']}",
                    RECORD_DIRECTORY=str(directory),
                    CI_RESULTS_DIRECTORY=str(directory / "results"),
                )
                environment.pop("VSTEST_TESTHOST_SHUTDOWN_TIMEOUT", None)
                if caller_value is not None:
                    environment["VSTEST_TESTHOST_SHUTDOWN_TIMEOUT"] = caller_value
                result = subprocess.run(
                    [BASH, "ci/gates.sh", "test"],
                    cwd=ROOT,
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                )
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                arguments = (directory / "arguments").read_text().splitlines()
                self.assertIn("--collect:XPlat Code Coverage", arguments)
                self.assertEqual(
                    REQUIRED_TIMEOUT, (directory / "timeout").read_text().strip()
                )


if __name__ == "__main__":
    unittest.main()
