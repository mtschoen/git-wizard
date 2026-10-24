"""Process-level contracts for the preflight CLI, without invoking toolchains."""

import io
import json
import os
import subprocess
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import patch

import preflight
from bash_resolution import resolve_bash

ROOT = Path(__file__).resolve().parents[1]
try:
    BASH = resolve_bash()
except FileNotFoundError:
    BASH = None


def passing_report():
    return {"score": 100, "summary": {"files": 10}}


def write_scanner_cli(clone, report, exit_code=None):
    script = f"console.log(JSON.stringify({json.dumps(report)}));\n"
    if exit_code is not None:
        script += f"process.exit({exit_code});\n"
    (clone / "dist/cli.js").write_text(script)


@unittest.skipUnless(BASH, "bash is required")
class PreflightTests(unittest.TestCase):
    def setUp(self):
        scratch = ROOT / "artifacts"
        scratch.mkdir(exist_ok=True)
        directory = self.enterContext(tempfile.TemporaryDirectory(dir=scratch))
        self.directory = Path(directory).resolve()
        self.stamp = self.directory / "stamp.json"
        self.enterContext(
            patch.dict(
                os.environ,
                {
                    "CI_PREFLIGHT_STAMP_PATH": str(self.stamp),
                    "CI_RESULTS_DIRECTORY": str(self.directory / "results"),
                    "LOCALAPPDATA": str(self.directory / "state"),
                    "XDG_STATE_HOME": str(self.directory / "state"),
                },
            )
        )

    def test_entry_point_declares_protocol(self):
        source = (ROOT / "tools/ci-preflight.sh").read_text()
        self.assertIn("# pr-crew-preflight-protocol: 1", source)

    def test_invalid_selection_fails(self):
        result = subprocess.run(
            [BASH, "tools/ci-preflight.sh", "--only", "unknown"],
            cwd=ROOT,
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)

    def test_all_selected_gates_finish_after_failure(self):
        # A fake dotnet is the executable boundary; the actual dispatcher and
        # shared gate scripts must still run and aggregate their exit statuses.
        scratch = ROOT / "artifacts"
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as directory:
            executable = Path(directory) / "dotnet"
            executable.write_text(
                '#!/usr/bin/env bash\necho fake-dotnet "$@"\nexit 7\n'
            )
            executable.chmod(0o755)
            environment = dict(
                os.environ, PATH=f"{directory}{os.pathsep}{os.environ['PATH']}"
            )
            result = subprocess.run(
                [
                    BASH,
                    "tools/ci-preflight.sh",
                    "--only",
                    "format",
                    "--only",
                    "build",
                    "--only",
                    "test",
                ],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn("FAIL format", result.stdout)
        self.assertIn("FAIL build", result.stdout)
        self.assertIn("FAIL test", result.stdout)
        self.assertIn("trx;LogFileName=TestResults.trx", result.stdout)

    def test_unsupported_platform_is_refused(self):
        with patch.object(preflight.platform, "system", return_value="Darwin"):
            self.assertEqual(2, preflight.run(["--all"]))

    def test_gate_uses_environment_path_and_never_falls_back_to_bare_bash(self):
        for available in (True, False):
            with self.subTest(available=available):
                commands = []

                def resolve(*, search_path, available=available):
                    return resolve_bash(
                        search_path=search_path,
                        is_windows=True,
                        is_executable=lambda candidate: available,
                    )

                def execute(command, commands=commands, **arguments):
                    commands.append(command)
                    return subprocess.CompletedProcess(command, 0)

                with (
                    patch.dict(os.environ, {"PATH": "configured-bin"}),
                    patch.object(preflight.platform, "system", return_value="Windows"),
                    patch.object(preflight, "git", return_value="a" * 40),
                    patch.object(preflight, "resolve_bash", side_effect=resolve),
                    patch.object(preflight.subprocess, "run", side_effect=execute),
                ):
                    if available:
                        self.assertEqual(0, preflight.run(["--only", "format"]))
                    else:
                        with self.assertRaisesRegex(
                            FileNotFoundError, "configured-bin"
                        ):
                            preflight.run(["--only", "format"])
                self.assertEqual(
                    [
                        [
                            os.path.abspath("configured-bin/bash.exe"),
                            "ci/gates.sh",
                            "format",
                        ]
                    ]
                    if available
                    else [],
                    commands,
                )

    def test_failed_or_dirty_runs_never_stamp(self):
        for failure, dirty in ((True, False), (False, True)):
            with self.subTest(failure=failure, dirty=dirty):
                stamp = self.stamp
                stamp.write_text("stale")
                responses = {
                    ("rev-parse", "--git-path", "pr-crew/preflight-stamp.json"): str(
                        stamp
                    ),
                    ("rev-parse", "HEAD"): "a" * 40,
                    ("status", "--porcelain"): " M source.cs" if dirty else "",
                }
                with (
                    patch.object(preflight.platform, "system", return_value="Linux"),
                    patch.object(
                        preflight,
                        "git",
                        side_effect=lambda *arguments, responses=responses: responses[
                            arguments
                        ],
                    ),
                    patch.object(
                        preflight.subprocess,
                        "run",
                        return_value=subprocess.CompletedProcess([], int(failure)),
                    ),
                ):
                    self.assertEqual(int(failure), preflight.run(["--all"]))
                    self.assertFalse(stamp.exists())

    def test_host_contract_and_stamp_lifecycle(self):
        for host, skipped in (("Linux", "inspect"), ("Windows", "coverage")):
            with self.subTest(host=host):
                stamp = self.stamp
                stamp.write_text("stale")
                head = "a" * 40

                def inspect_git(*arguments, stamp=stamp, head=head):
                    if arguments[0] == "status":
                        return ""
                    if "--git-path" in arguments:
                        return str(stamp)
                    return head

                executed = []

                def execute(command, stamp=stamp, executed=executed, **_arguments):
                    self.assertFalse(stamp.exists())
                    self.assertEqual(BASH, command[0])
                    executed.append(command[-1])
                    return subprocess.CompletedProcess(command, 0)

                with (
                    patch.object(preflight.platform, "system", return_value=host),
                    patch.object(preflight, "git", side_effect=inspect_git),
                    patch.object(preflight.subprocess, "run", side_effect=execute),
                ):
                    self.assertEqual(0, preflight.run(["--changed-from", "main"]))
                    self.assertNotIn(skipped, executed)
                    self.assertEqual(set(preflight.GATES) - {skipped}, set(executed))
                    self.assertIn(head, stamp.read_text())
                    self.assertEqual(0, preflight.run(["--only", "format"]))
                    self.assertFalse(stamp.exists())

    def test_results_directory_is_reused_across_targeted_runs(self):
        results = self.directory / "results"
        observed = []

        def execute(command, **arguments):
            observed.append(Path(arguments["env"]["CI_RESULTS_DIRECTORY"]))
            return subprocess.CompletedProcess(command, 0)

        with (
            patch.object(preflight.platform, "system", return_value="Linux"),
            patch.object(
                preflight, "git", return_value=str(self.directory / "fallback-stamp")
            ),
            patch.object(preflight.subprocess, "run", side_effect=execute),
        ):
            self.assertEqual(0, preflight.run(["--only", "test"]))
            self.assertEqual(0, preflight.run(["--only", "coverage"]))
        self.assertEqual([results, results], observed)

        executables = self.directory / "executables"
        executables.mkdir()
        for name, source in {
            "uname": "#!/usr/bin/env bash\necho Linux\n",
            "dotnet": '#!/usr/bin/env bash\nwhile [[ "$1" != --results-directory ]]; do shift; done\nmkdir -p "$2"\nreport=$(mktemp -d "$2/run-XXXXXXXX")\nprintf \'<coverage><packages><package><classes><class filename="sample.cs"><lines><line number="1" hits="%s"/></lines></class></classes></package></packages></coverage>\' "$COVERAGE_HITS" > "$report/coverage.cobertura.xml"\n',
        }.items():
            executable = executables / name
            executable.write_text(source)
            executable.chmod(0o755)
        environment = dict(
            os.environ, PATH=f"{executables}{os.pathsep}{os.environ['PATH']}"
        )
        for hits in ("1", "0"):
            environment["COVERAGE_HITS"] = hits
            result = subprocess.run(
                [BASH, "ci/gates.sh", "test"],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            result = subprocess.run(
                [BASH, "ci/gates.sh", "coverage"],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(
                int(hits == "0"), result.returncode, result.stdout + result.stderr
            )

    def test_missing_coverage_after_failed_test_is_skipped(self):
        for coverage_exists in (False, True):
            with self.subTest(coverage_exists=coverage_exists):
                results = self.directory / "results"
                (results / "test-historical").mkdir(parents=True, exist_ok=True)
                (results / "test-historical/coverage.cobertura.xml").write_text(
                    "<coverage/>"
                )
                (results / "current-test-directory").write_text("test-current\n")
                if coverage_exists:
                    (results / "test-current").mkdir(parents=True)
                    (results / "test-current/coverage.cobertura.xml").write_text(
                        "<coverage/>"
                    )
                executed = []

                def execute(command, executed=executed, **arguments):
                    executed.append(command[-1])
                    return subprocess.CompletedProcess(
                        command, int(command[-1] == "test")
                    )

                output = io.StringIO()
                with (
                    patch.object(preflight.platform, "system", return_value="Linux"),
                    patch.object(
                        preflight,
                        "git",
                        return_value=str(self.directory / "fallback-stamp"),
                    ),
                    patch.object(preflight.subprocess, "run", side_effect=execute),
                    redirect_stdout(output),
                ):
                    self.assertEqual(
                        1, preflight.run(["--only", "test", "--only", "coverage"])
                    )
                self.assertEqual(
                    ["test", "coverage"] if coverage_exists else ["test"], executed
                )
                self.assertEqual(
                    not coverage_exists,
                    "SKIP coverage: test failed and coverage input is missing"
                    in output.getvalue(),
                )
                self.assertEqual(
                    coverage_exists,
                    preflight.has_current_coverage(results.relative_to(ROOT)),
                )

    def test_stamp_override_does_not_touch_git_resolved_stamp(self):
        fallback = self.directory / "git-stamp.json"
        fallback.write_text("preserve")
        self.stamp.write_text("invalidate")
        with (
            patch.object(preflight.platform, "system", return_value="Linux"),
            patch.object(preflight, "git", return_value=str(fallback)),
            patch.object(
                preflight.subprocess,
                "run",
                return_value=subprocess.CompletedProcess([], 0),
            ),
        ):
            self.assertEqual(0, preflight.run(["--only", "format"]))
        self.assertTrue(fallback.exists(), "Git-resolved stamp was deleted")
        self.assertEqual("preserve", fallback.read_text())
        self.assertFalse(self.stamp.exists())

    def test_aislop_prepare_uses_external_state_and_fetches_pin(self):
        commit = (ROOT / ".aislop/fork-commit").read_text().strip()
        executable_directory = self.directory / "executables"
        executable_directory.mkdir()
        log = self.directory / "commands.log"
        for name, source in {
            "git": '#!/usr/bin/env bash\n[[ "$1" == -C && "$2" -ef "$EXPECTED_CLONE" ]] || exit 9\nprintf "%s\\n" "${*:3}" >> "$COMMAND_LOG"\n',
            "npx": '#!/usr/bin/env bash\nprintf "npx %s\\n" "$*" >> "$COMMAND_LOG"\nmkdir -p dist\ntouch dist/cli.js\n',
        }.items():
            executable = executable_directory / name
            executable.write_text(source)
            executable.chmod(0o755)
        for state_variable in (
            "LOCALAPPDATA",
            "XDG_STATE_HOME",
            "HOME",
            "AISLOP_TOOL_DIRECTORY",
            "RUNNER_TEMP",
        ):
            with self.subTest(state_variable=state_variable):
                state = self.directory / state_variable
                base = state / ".local/state" if state_variable == "HOME" else state
                clone = base / "git-wizard/aislop"
                if state_variable == "AISLOP_TOOL_DIRECTORY":
                    clone = state
                elif state_variable == "RUNNER_TEMP":
                    clone = state / "aislop"
                (clone / ".git").mkdir(parents=True)
                (clone / "package.json").write_text(
                    '{"packageManager": "pnpm@12.14.0"}\n'
                )
                environment = dict(os.environ)
                for variable in (
                    "AISLOP_TOOL_DIRECTORY",
                    "RUNNER_TEMP",
                    "LOCALAPPDATA",
                    "XDG_STATE_HOME",
                ):
                    environment.pop(variable, None)
                environment.update(
                    {
                        state_variable: state.as_posix(),
                        "PATH": f"{executable_directory}{os.pathsep}{os.environ['PATH']}",
                        "COMMAND_LOG": str(log),
                        "EXPECTED_CLONE": clone.as_posix(),
                    }
                )
                result = subprocess.run(
                    [BASH, "ci/aislop-gate.sh", "prepare"],
                    cwd=ROOT,
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                )
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual(
                    commit, (clone / ".git/build-receipt").read_text().strip()
                )
                commands = log.read_text().splitlines()
                self.assertEqual(
                    [
                        f"fetch origin {commit}",
                        f"checkout {commit}",
                        "npx -y pnpm@12.14.0 install --frozen-lockfile",
                    ],
                    commands,
                )
                log.unlink()

    def run_scanner(self, environment, *arguments):
        # File handles avoid heartbeat descendants keeping capture pipes open.
        output = self.directory / "scanner-output.log"
        with output.open("w") as stream:
            result = subprocess.run(
                [BASH, "ci/aislop-gate.sh", *arguments],
                cwd=ROOT,
                env=environment,
                stdout=stream,
                stderr=subprocess.STDOUT,
                check=False,
            )
        return result.returncode, output.read_text()

    def scanner_environment(self):
        commit = (ROOT / ".aislop/fork-commit").read_text().strip()
        clone = self.directory / "scanner"
        (clone / ".git").mkdir(parents=True)
        (clone / "dist").mkdir()
        (clone / "package.json").write_text('{"packageManager": "pnpm@12.14.0"}\n')
        write_scanner_cli(clone, passing_report())
        (clone / ".git/build-receipt").write_text(commit + "\n")
        executables = self.directory / "executables"
        executables.mkdir()
        for name, source in {
            "git": f'#!/usr/bin/env bash\nif [[ "$3" == rev-parse ]]; then echo {commit}; fi\n',
            "npx": '#!/usr/bin/env bash\nexit "${PREPARE_EXIT:-0}"\n',
            # End the optional heartbeat without waiting on a real clock.
            "sleep": "#!/usr/bin/env bash\nexit 1\n",
        }.items():
            executable = executables / name
            executable.write_text(source)
            executable.chmod(0o755)
        return clone, dict(
            os.environ,
            AISLOP_TOOL_DIRECTORY=clone.as_posix(),
            PATH=f"{executables}{os.pathsep}{os.environ['PATH']}",
        )

    def test_failed_aislop_upgrade_refuses_stale_dist(self):
        clone, environment = self.scanner_environment()
        environment["PREPARE_EXIT"] = "7"
        for package_json, expected_exit in (
            ("{}\n", 1),
            ('{"packageManager": "npm@10.0.0"}\n', 1),
            ('{"packageManager": "pnpm@12.14"}\n', 1),
            ('{"packageManager": "pnpm@12.14.0"}\n', 7),
        ):
            with self.subTest(package_json=package_json):
                (clone / ".git/build-receipt").write_text("a" * 40 + "\n")
                (clone / "package.json").write_text(package_json)
                status, output = self.run_scanner(environment, "prepare")
                self.assertEqual(expected_exit, status, output)
                if expected_exit == 1:
                    self.assertIn("packageManager is missing or not pnpm@x.y.z", output)
                self.assertTrue((clone / "dist/cli.js").exists())
                status, output = self.run_scanner(environment)
                self.assertNotEqual(0, status, output)
                self.assertIn("Run: bash ci/aislop-gate.sh prepare", output)

    def test_aislop_reaps_heartbeat_child_before_exit(self):
        clone, environment = self.scanner_environment()
        process_file = self.directory / "heartbeat-process"
        environment["HEARTBEAT_PROCESS_FILE"] = process_file.as_posix()
        sleeper = self.directory / "executables/sleep"
        sleeper.write_text(
            '#!/usr/bin/env bash\n'
            'echo "$$" > "$HEARTBEAT_PROCESS_FILE"\n'
            'exec >/dev/null 2>&1\n'
            'exec node -e "setInterval(() => {}, 2147483647)"\n'
        )
        # The scanner finishes only after the heartbeat has started its child.
        scanner = clone / "dist/cli.js"
        scanner.write_text(
            'const fs = require("fs");\n'
            'function finish() {\n'
            '  if (!fs.existsSync(process.env.HEARTBEAT_PROCESS_FILE)) {\n'
            '    setImmediate(finish); return;\n'
            '  }\n'
            f'  console.log(JSON.stringify({json.dumps(passing_report())}));\n'
            '}\nfinish();\n'
        )
        status, output = self.run_scanner(environment)
        self.assertEqual(0, status, output)
        # A surviving child is killed here so a red regression run stays owned.
        result = subprocess.run(
            [
                BASH, "-c",
                'if kill -0 "$1" 2>/dev/null; then kill "$1"; exit 1; fi',
                "heartbeat-check", process_file.read_text().strip(),
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(0, result.returncode, "heartbeat child survived gate exit")

    def test_aislop_accepts_relative_results_directory(self):
        clone, environment = self.scanner_environment()
        environment["CI_RESULTS_DIRECTORY"] = (
            (self.directory / "results").relative_to(ROOT).as_posix()
        )
        for scanner_exit in (0, 1):
            with self.subTest(scanner_exit=scanner_exit):
                report = passing_report()
                report["diagnostics"] = [
                    {
                        "severity": "warning",
                        "rule": "example",
                        "filePath": "sample.cs",
                        "line": 1,
                        "message": "fixture diagnostic",
                    }
                ]
                write_scanner_cli(clone, report, scanner_exit)
                scanner = clone / "dist/cli.js"
                scanner.write_text(
                    'const assert = require("node:assert/strict");\n'
                    'for (const [name, value] of Object.entries({\n'
                    '  MSBUILDDISABLENODEREUSE: "1",\n'
                    '  DOTNET_CLI_USE_MSBUILD_SERVER: "0",\n'
                    '  MSBUILDUSESERVER: "0",\n'
                    '  UseSharedCompilation: "false"\n'
                    '})) assert.equal(process.env[name], value, name);\n'
                    + scanner.read_text()
                )
                status, output = self.run_scanner(environment)
                self.assertEqual(scanner_exit, status, output)
                self.assertIn("aislop score 100 over 10 files", output)
                if scanner_exit:
                    self.assertIn("fixture diagnostic", output)
                self.assertNotIn("MODULE_NOT_FOUND", output)

    def test_aislop_prints_analyzer_execution_summary(self):
        clone, environment = self.scanner_environment()
        projects = [
            "GitWizard/GitWizard.csproj",
            "GitWizardTests/GitWizardTests.csproj",
            "GitWizardUI.Screenshot/GitWizardUI.Screenshot.csproj",
            "GitWizardUI/GitWizardUI.csproj",
            "git-wizard/git-wizard.csproj",
        ]
        report = passing_report()
        report["engines"] = {
            "lint": {
                "issues": 0,
                "skipped": False,
                "elapsed": 103033,
                "executions": [
                    {
                        "tool": tool,
                        "status": "ran",
                        "targets": [target],
                        "analyzedProjects": projects,
                        "excludedProjects": [],
                        "elapsed": elapsed,
                    }
                    for tool, target, elapsed in (
                        ("roslynator", "GitWizard/GitWizard.csproj", 64738),
                        ("jb inspectcode", "git-wizard.slnx", 102703),
                    )
                ],
            }
        }
        report["diagnostics"] = [
            {"engine": "lint", "severity": "warning", "rule": "x", "filePath": "a.cs"}
        ]
        for scanner_exit in (0, 1):
            with self.subTest(scanner_exit=scanner_exit):
                write_scanner_cli(clone, report, scanner_exit)
                status, output = self.run_scanner(environment)
                self.assertEqual(scanner_exit, status, output)
                self.assertIn("aislop analyzer roslynator: ran, 64.7s", output)
                self.assertIn("aislop analyzer jb inspectcode: ran, 102.7s", output)
                self.assertIn("5 analyzed projects", output)
                for project in projects:
                    self.assertIn(f"aislop analyzer jb inspectcode analyzed: {project}", output)
                    self.assertIn(f"aislop analyzer roslynator analyzed: {project}", output)
                self.assertIn("aislop findings by engine/severity: lint/warning=1", output)


if __name__ == "__main__":
    unittest.main()
