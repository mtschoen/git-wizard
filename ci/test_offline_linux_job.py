"""Contracts that keep the Linux CI job off the public internet (schoen-lab#3353)."""

import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[1]


def linux_job_text():
    text = (ROOT / ".gitea/workflows/ci.yml").read_text(encoding="utf-8")
    start = text.index("  test-linux:")
    end = text.index("  test-windows:")
    return text[start:end]


class OfflineLinuxJobTests(unittest.TestCase):
    def test_linux_job_downloads_no_toolchain(self):
        job = linux_job_text()
        self.assertNotIn("setup-dotnet", job)
        self.assertNotIn("apt-get", job)

    def test_posix_publish_job_downloads_no_toolchain(self):
        text = (ROOT / ".gitea/workflows/release.yml").read_text(encoding="utf-8")
        job = text[text.index("  publish-posix:") : text.index("  publish-windows:")]
        self.assertNotIn("setup-dotnet", job)
        self.assertNotIn("apt-get", job)

    def test_linux_restores_run_in_locked_mode(self):
        gates = (ROOT / "ci/gates.sh").read_text(encoding="utf-8")
        self.assertIn("-p:RestoreLockedMode=true", gates)

    def test_lock_files_exist_for_every_linux_project(self):
        gates = (ROOT / "ci/gates.sh").read_text(encoding="utf-8")
        projects = re.search(r"^projects=\((.*)\)$", gates, re.MULTILINE).group(1).split()
        for project in projects:
            self.assertTrue((ROOT / Path(project).parent / "packages.lock.json").is_file(), project)

    def test_package_references_use_exact_versions_for_offline_publish(self):
        for project_file in ROOT.glob("*/*.csproj"):
            tree = ElementTree.parse(project_file)
            for reference in tree.iter("PackageReference"):
                self.assertNotIn("*", reference.attrib.get("Version", ""), project_file.name)

    def test_windows_release_restore_leaves_tracked_lock_files_unchanged(self):
        with tempfile.TemporaryDirectory() as directory:
            scratch = Path(directory)
            shutil.copy2(ROOT / "Directory.Build.props", scratch / "Directory.Build.props")
            for project in ("GitWizard", "GitWizardUI"):
                (scratch / project).mkdir()
                shutil.copy2(ROOT / project / f"{project}.csproj", scratch / project / f"{project}.csproj")
                shutil.copy2(ROOT / project / "packages.lock.json", scratch / project / "packages.lock.json")
            nuget_packages = os.environ.get("NUGET_PACKAGES") or str(Path.home() / ".nuget" / "packages")
            result = subprocess.run(
                [
                    "dotnet",
                    "restore",
                    str(scratch / "GitWizardUI" / "GitWizardUI.csproj"),
                    "-p:OS=Windows_NT",
                    "-p:Configuration=Release",
                    "-p:NuGetAudit=false",
                    "--source",
                    nuget_packages,
                ],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            for project in ("GitWizard", "GitWizardUI"):
                self.assertEqual(
                    (ROOT / project / "packages.lock.json").read_bytes(),
                    (scratch / project / "packages.lock.json").read_bytes(),
                    project,
                )


if __name__ == "__main__":
    unittest.main()
