import contextlib
import importlib.util
import io
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
SPECIFICATION = importlib.util.spec_from_file_location(
    "post_coverage_status", ROOT / "ci/post-coverage-status.py"
)
STATUS = importlib.util.module_from_spec(SPECIFICATION)
SPECIFICATION.loader.exec_module(STATUS)

REPORT = (
    '<coverage branch-rate="0.5"><packages><package><classes>'
    '<class filename="sample.cs"><lines>'
    '<line number="1" hits="1"/><line number="2" hits="0"/>'
    "</lines></class></classes></package></packages></coverage>"
)


class PostCoverageStatusTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.directory = Path(directory.name)
        (self.directory / "coverage.cobertura.xml").write_text(REPORT)
        self.pattern = (self.directory / "**/coverage.cobertura.xml").as_posix()

    def run_main(self, *arguments):
        posts = []
        errors = io.StringIO()
        with (
            patch.object(STATUS, "_post", side_effect=lambda *call: posts.append(call)),
            contextlib.redirect_stderr(errors),
            contextlib.redirect_stdout(io.StringIO()),
        ):
            code = STATUS.main(["post-coverage-status.py", *arguments])
        return code, posts, errors.getvalue()

    def test_default_context_is_the_pr_crew_merge_gate_context(self):
        code, posts, _ = self.run_main("--cobertura", self.pattern)
        self.assertEqual(0, code)
        self.assertEqual(
            [("pr-crew/coverage", "success", "50.0% line coverage")], posts
        )

    def test_context_option_names_the_posted_status(self):
        code, posts, _ = self.run_main(
            "--cobertura", self.pattern, "--context", "pr-crew/coverage-windows"
        )
        self.assertEqual(0, code)
        self.assertEqual("pr-crew/coverage-windows", posts[0][0])

    def test_unreadable_report_posts_error_and_fails_the_gate_naming_the_glob(self):
        missing = (self.directory / "absent/**/coverage.cobertura.xml").as_posix()
        code, posts, errors = self.run_main(
            "--cobertura",
            missing,
            "--gate-line",
            "90",
            "--context",
            "pr-crew/coverage-windows",
        )
        self.assertEqual(1, code)
        self.assertEqual(
            [("pr-crew/coverage-windows", "error", "coverage measurement failed")],
            posts,
        )
        self.assertIn(missing, errors)

    def test_unreadable_report_without_a_gate_stays_nonfatal(self):
        code, posts, _ = self.run_main(
            "--cobertura", self.directory.as_posix() + "/none.xml"
        )
        self.assertEqual(0, code)
        self.assertEqual("error", posts[0][1])

    def test_gate_fails_below_the_threshold_and_passes_at_it(self):
        failing, _, _ = self.run_main(
            "--cobertura", self.pattern, "--gate-line", "50.01", "--skip-post"
        )
        passing, _, _ = self.run_main(
            "--cobertura", self.pattern, "--gate-line", "50", "--skip-post"
        )
        self.assertEqual((1, 0), (failing, passing))

    def test_sources_under_external_do_not_count_toward_the_percent(self):
        vendored = (
            '<coverage><packages><package><classes>'
            '<class filename="C:/work/external/MFTLib/Vendored.cs"><lines>'
            '<line number="1" hits="0"/><line number="2" hits="0"/>'
            '<line number="3" hits="0"/><line number="4" hits="0"/>'
            "</lines></class></classes></package></packages></coverage>"
        )
        other = self.directory / "vendored"
        other.mkdir()
        (other / "coverage.cobertura.xml").write_text(vendored)

        code, posts, _ = self.run_main("--cobertura", self.pattern)

        self.assertEqual(0, code)
        self.assertEqual(
            [("pr-crew/coverage", "success", "50.0% line coverage")], posts
        )

    def test_the_gates_recipe_enforces_ninety_percent(self):
        recipe = (ROOT / "ci/gates.sh").read_text()
        self.assertIn("--gate-line 90 ", recipe)


if __name__ == "__main__":
    unittest.main()
