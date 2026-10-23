"""Deterministic contracts for PATH-only POSIX bash resolution."""

import os
import unittest
from unittest.mock import patch

import bash_resolution


class BashResolutionTests(unittest.TestCase):
    def test_windows_skips_launcher_directories_before_git(self):
        for launcher in (
            "Windows/System32",
            "Windows/SysWOW64",
            "Windows/Sysnative",
            "Local/Microsoft/WindowsApps",
            "WINDOWS\\SYSTEM32\\nested",
        ):
            with self.subTest(launcher=launcher):
                checked = []

                def executable(candidate, checked=checked):
                    checked.append(candidate)
                    return True

                resolved = bash_resolution.resolve_bash(
                    search_path=os.pathsep.join((launcher, "Git/bin")),
                    is_windows=True,
                    is_executable=executable,
                )
                expected = os.path.abspath("Git/bin/bash.exe")
                self.assertEqual(expected, resolved)
                self.assertEqual([expected], checked)

    def test_current_directory_and_empty_entries_are_not_searched(self):
        checked = []
        current_bash = os.path.abspath("bash.exe")

        def executable(candidate):
            checked.append(candidate)
            return candidate == current_bash

        with self.assertRaises(FileNotFoundError):
            bash_resolution.resolve_bash(
                search_path=os.pathsep + "elsewhere" + os.pathsep,
                is_windows=True,
                is_executable=executable,
            )
        self.assertEqual([os.path.abspath("elsewhere/bash.exe")], checked)

    def test_relative_entry_returns_absolute_path(self):
        resolved = bash_resolution.resolve_bash(
            search_path="relative-bin",
            is_windows=False,
            is_executable=lambda candidate: True,
        )
        self.assertTrue(os.path.isabs(resolved))
        self.assertEqual(os.path.join(os.getcwd(), "relative-bin", "bash"), resolved)

    def test_non_windows_uses_bash_and_keeps_path_order(self):
        checked = []

        def executable(candidate):
            checked.append(candidate)
            return candidate != os.path.abspath("missing/bash")

        resolved = bash_resolution.resolve_bash(
            search_path=os.pathsep.join(("missing", "System32", "later")),
            is_windows=False,
            is_executable=executable,
        )
        self.assertEqual(os.path.abspath("System32/bash"), resolved)
        self.assertEqual([os.path.abspath("missing/bash"), resolved], checked)

    def test_no_hit_names_all_entries_including_skipped_launchers(self):
        entries = ("missing", "Windows/System32", "also-missing")
        with self.assertRaises(FileNotFoundError) as raised:
            bash_resolution.resolve_bash(
                search_path=os.pathsep.join(entries),
                is_windows=True,
                is_executable=lambda candidate: False,
            )
        for entry in entries:
            self.assertIn(entry, str(raised.exception))

    def test_empty_path_raises_without_probing(self):
        for search_path in ("", os.pathsep):
            with (
                self.subTest(search_path=search_path),
                self.assertRaisesRegex(FileNotFoundError, "empty PATH"),
            ):
                bash_resolution.resolve_bash(
                    search_path=search_path,
                    is_windows=False,
                    is_executable=lambda candidate: self.fail("unexpected probe"),
                )

    def test_defaults_use_environment_and_interpreter_platform(self):
        for platform, name in (("win32", "bash.exe"), ("linux", "bash")):
            with self.subTest(platform=platform):
                with (
                    patch.dict(os.environ, {"PATH": "configured-bin"}),
                    patch.object(bash_resolution.sys, "platform", platform),
                ):
                    resolved = bash_resolution.resolve_bash(
                        is_executable=lambda candidate: True
                    )
                self.assertEqual(os.path.abspath(f"configured-bin/{name}"), resolved)

    def test_default_probe_requires_executable_file(self):
        for is_file, has_access in ((False, True), (True, False), (True, True)):
            with (
                self.subTest(is_file=is_file, has_access=has_access),
                patch.object(bash_resolution.os.path, "isfile", return_value=is_file),
                patch.object(bash_resolution.os, "access", return_value=has_access),
            ):
                if is_file and has_access:
                    self.assertEqual(
                        os.path.abspath("bin/bash"),
                        bash_resolution.resolve_bash(
                            search_path="bin", is_windows=False
                        ),
                    )
                else:
                    with self.assertRaises(FileNotFoundError):
                        bash_resolution.resolve_bash(
                            search_path="bin", is_windows=False
                        )
