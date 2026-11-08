"""Contracts that keep the Linux CI job off the public internet (schoen-lab#3353)."""

import json
import os
import re
import shlex
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[1]
PROJECTS = ("GitWizard", "git-wizard", "GitWizardUI", "GitWizardUI.Screenshot", "GitWizardTests")
INSPECTION_CONSUMERS = ("GitWizardTests", "GitWizardUI.Screenshot")
INSPECTION_COMMAND = re.compile(r"jb inspectcode git-wizard\.slnx[^\r\n`]*")


def inspection_arguments(text):
    match = INSPECTION_COMMAND.search(text)
    if match is None:
        raise AssertionError("Missing InspectCode invocation")
    return shlex.split(match.group(0))


def inspection_restore_properties():
    gate = (ROOT / "ci/windows-gates.ps1").read_text(encoding="utf-8")
    return [
        "-p:" + argument.removeprefix("--properties:")
        for argument in inspection_arguments(gate)
        if argument.startswith("--properties:")
    ]


def copy_restore_fixture(scratch):
    shutil.copy2(ROOT / "Directory.Build.props", scratch / "Directory.Build.props")
    expected_locks = {}
    for project in PROJECTS:
        (scratch / project).mkdir()
        project_file = f"{project}.csproj"
        shutil.copy2(ROOT / project / project_file, scratch / project / project_file)
        shutil.copy2(ROOT / project / "packages.lock.json", scratch / project / "packages.lock.json")
        expected_locks[project] = (scratch / project / "packages.lock.json").read_bytes()
    return expected_locks


def restore_unix_project(scratch, project, properties):
    nuget_packages = os.environ.get("NUGET_PACKAGES") or str(Path.home() / ".nuget" / "packages")
    return subprocess.run(
        [
            "dotnet",
            "restore",
            str(scratch / project / f"{project}.csproj"),
            "-p:OS=Unix",
            "-p:NuGetAudit=false",
            *properties,
            "--source",
            nuget_packages,
        ],
        capture_output=True,
        text=True,
        check=False,
    )


def restored_libraries(scratch, project):
    assets = scratch / project / "obj/project.assets.json"
    return json.loads(assets.read_text(encoding="utf-8"))["libraries"]


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

    def assert_tracked_locks_unchanged(self, scratch, expected_locks):
        for project, expected in expected_locks.items():
            self.assertEqual(expected, (scratch / project / "packages.lock.json").read_bytes(), project)

    def test_inspection_commands_share_non_mutating_release_restore(self):
        paths = ("ci/windows-gates.ps1", "AGENTS.md", "docs/ci-and-release.md")
        expected = [
            "jb",
            "inspectcode",
            "git-wizard.slnx",
            "--settings=git-wizard.slnx.DotSettings",
            "--severity=WARNING",
            "--no-updates",
            "--properties:Configuration=Release",
            "--properties:NuGetLockFilePath=obj/packages.lock.json",
        ]
        for path in paths:
            with self.subTest(path=path):
                command = inspection_arguments((ROOT / path).read_text(encoding="utf-8"))
                actual = [argument for argument in command if not argument.startswith("-o=")]
                self.assertEqual(expected, actual)

    def test_inspection_release_restore_preserves_tracked_lock_files(self):
        with tempfile.TemporaryDirectory() as directory:
            scratch = Path(directory)
            expected_locks = copy_restore_fixture(scratch)
            release_properties = inspection_restore_properties()
            for project in INSPECTION_CONSUMERS:
                result = restore_unix_project(scratch, project, release_properties)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assert_tracked_locks_unchanged(scratch, expected_locks)

            for project in PROJECTS:
                self.assertTrue((scratch / project / "obj/packages.lock.json").is_file(), project)
            for project in ("GitWizardUI", *INSPECTION_CONSUMERS):
                self.assertNotIn("Avalonia.Diagnostics/11.2.8", restored_libraries(scratch, project), project)

            debug_properties = ["-p:Configuration=Debug", "-p:RestoreLockedMode=true"]
            for project in INSPECTION_CONSUMERS:
                result = restore_unix_project(scratch, project, debug_properties)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assert_tracked_locks_unchanged(scratch, expected_locks)

            for project in ("GitWizardUI", *INSPECTION_CONSUMERS):
                self.assertIn("Avalonia.Diagnostics/11.2.8", restored_libraries(scratch, project), project)

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
