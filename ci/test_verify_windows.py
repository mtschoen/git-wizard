"""Contracts for scripts/verify-windows.sh without reaching a remote host."""

import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "verify-windows.sh"
BASH = shutil.which("bash")


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


if __name__ == "__main__":
    unittest.main()
