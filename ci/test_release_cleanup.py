"""Behavioral contracts for the PR smoke cleanup step in .gitea/workflows/release.yml."""

import os
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

from bash_resolution import resolve_bash

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / ".gitea" / "workflows" / "release.yml"
STEP_HEADER = "      - name: Smoke cleanup (PR only)\n"
RUN_MARKER = "        run: |\n"
REPO = "schoen/git-wizard"
API_ROOT = f"https://gitea.fleet.sticktoitive.net/api/v1/repos/{REPO}"
SECRET_SENTINEL = "secret-gitea-token-sentinel"
TAG = "smoke-pr499-abcdef1"

try:
    BASH = resolve_bash()
except FileNotFoundError:
    BASH = None

FAKE_CURL_PREAMBLE = """\
curl() {
  local method="GET" url="" write_out="" fail_on_error=false auth_header=""
  while [ "$#" -gt 0 ]; do
    case "$1" in
      -X) method="$2"; shift 2 ;;
      -H) [[ "$2" == Authorization:* ]] && auth_header="$2"; shift 2 ;;
      -o) shift 2 ;;
      -w) write_out="$2"; shift 2 ;;
      -*f*) fail_on_error=true; shift ;;
      -*) shift ;;
      *) url="$1"; shift ;;
    esac
  done
  printf '%s %s\\n' "$method" "$url" >> "$FAKE_CURL_LOG"
  if [ "$auth_header" != "Authorization: token $CI_GITEA_TOKEN" ]; then
    echo "fake curl: missing or mismatched Authorization header" >&2
    return 2
  fi
  local line_num=1 current=0 status="" exit_code="" s e
  [ -f "$FAKE_CURL_INDEX" ] && read -r line_num < "$FAKE_CURL_INDEX"
  while read -r s e; do
    current=$((current + 1))
    if [ "$current" -eq "$line_num" ]; then
      status="$s"
      exit_code="$e"
      break
    fi
  done < "$FAKE_CURL_RESPONSES"
  printf '%s\\n' "$((line_num + 1))" > "$FAKE_CURL_INDEX"
  if [ -z "$status" ]; then
    echo "fake curl: unexpected request $method $url" >&2
    return 99
  fi
  if [ "$exit_code" -ne 0 ]; then
    echo "curl: ($exit_code) scripted transport failure" >&2
    return "$exit_code"
  fi
  if [ "$fail_on_error" = true ] && [ "$status" -ge 400 ]; then
    echo "curl: (22) The requested URL returned error: $status" >&2
    return 22
  fi
  [ "$write_out" = "%{http_code}" ] && printf '%s' "$status"
  return 0
}
"""


def extract_cleanup_step() -> tuple[str, str]:
    source = WORKFLOW.read_text(encoding="utf-8")
    _, remainder = source.split(STEP_HEADER, maxsplit=1)
    step_text = remainder.split("\n      - name: ", maxsplit=1)[0]
    metadata, run_indented = step_text.split(RUN_MARKER, maxsplit=1)
    return metadata, textwrap.dedent(run_indented)


def expand_requests(paths: tuple[str, ...]) -> list[str]:
    result = []
    for item in paths:
        method, suffix = item.split(" ", maxsplit=1)
        result.append(f"{method} {API_ROOT}{suffix}")
    return result


def run_cleanup_block(
    *,
    release_id: str,
    tag: str,
    responses: tuple[tuple[str, int], ...],
) -> tuple[subprocess.CompletedProcess[str], list[str]]:
    _, cleanup_script = extract_cleanup_step()
    with tempfile.TemporaryDirectory() as temp_dir:
        temp_path = Path(temp_dir)
        log_path = temp_path / "requests.log"
        responses_path = temp_path / "responses.txt"
        index_path = temp_path / "index.txt"
        script_path = temp_path / "cleanup.sh"

        log_path.write_text("", encoding="utf-8")
        responses_text = "".join(f"{status} {code}\n" for status, code in responses)
        responses_path.write_text(responses_text, encoding="utf-8", newline="\n")
        script_path.write_text(
            FAKE_CURL_PREAMBLE + "\n" + cleanup_script,
            encoding="utf-8",
            newline="\n",
        )

        environment = {
            **os.environ,
            "GITHUB_REPOSITORY": REPO,
            "CI_GITEA_TOKEN": SECRET_SENTINEL,
            "RELEASE_ID": release_id,
            "TAG": tag,
            "FAKE_CURL_LOG": log_path.as_posix(),
            "FAKE_CURL_RESPONSES": responses_path.as_posix(),
            "FAKE_CURL_INDEX": index_path.as_posix(),
        }
        result = subprocess.run(
            [BASH, "-e", "-o", "pipefail", str(script_path)],
            capture_output=True,
            text=True,
            check=False,
            env=environment,
        )
        return result, log_path.read_text(encoding="utf-8").splitlines()


SUCCESS_CASES = (
    (
        "204-delete-and-404-verify",
        "1331",
        TAG,
        (("204", 0), ("404", 0), ("404", 0), ("404", 0)),
        (
            "DELETE /releases/1331",
            "GET /releases/1331",
            f"GET /releases/tags/{TAG}",
            f"GET /tags/{TAG}",
        ),
        "Deleted smoke release 1331 (HTTP 204).",
    ),
    (
        "already-absent-404-delete",
        "1331",
        TAG,
        (("404", 0), ("404", 0), ("404", 0), ("404", 0)),
        (
            "DELETE /releases/1331",
            "GET /releases/1331",
            f"GET /releases/tags/{TAG}",
            f"GET /tags/{TAG}",
        ),
        "Smoke release 1331 already absent (HTTP 404).",
    ),
    (
        "empty-release-id-verifies-tag-resources",
        "",
        TAG,
        (("404", 0), ("404", 0)),
        (f"GET /releases/tags/{TAG}", f"GET /tags/{TAG}"),
        "Verified smoke release/tag absence",
    ),
    (
        "null-release-id-verifies-tag-resources",
        "null",
        TAG,
        (("404", 0), ("404", 0)),
        (f"GET /releases/tags/{TAG}", f"GET /tags/{TAG}"),
        "Verified smoke release/tag absence",
    ),
    (
        "release-id-with-empty-tag",
        "1331",
        "",
        (("204", 0), ("404", 0)),
        ("DELETE /releases/1331", "GET /releases/1331"),
        "Verified smoke release/tag absence",
    ),
    (
        "release-id-with-null-tag",
        "1331",
        "null",
        (("204", 0), ("404", 0)),
        ("DELETE /releases/1331", "GET /releases/1331"),
        "Verified smoke release/tag absence",
    ),
    ("empty-release-id-and-empty-tag-skips", "", "", (), (), "Smoke cleanup skipped"),
    (
        "null-release-id-and-null-tag-skips",
        "null",
        "null",
        (),
        (),
        "Smoke cleanup skipped",
    ),
)

FAILURE_CASES = (
    (
        "delete-401-issue-499",
        "1331",
        TAG,
        (("401", 0),),
        ("DELETE /releases/1331",),
        "/releases/1331",
        "HTTP 401",
    ),
    (
        "delete-403-issue-499",
        "1331",
        TAG,
        (("403", 0),),
        ("DELETE /releases/1331",),
        "/releases/1331",
        "HTTP 403",
    ),
    (
        "delete-405-issue-499",
        "1331",
        TAG,
        (("405", 0),),
        ("DELETE /releases/1331",),
        "/releases/1331",
        "HTTP 405",
    ),
    (
        "delete-500-issue-499",
        "1331",
        TAG,
        (("500", 0),),
        ("DELETE /releases/1331",),
        "/releases/1331",
        "HTTP 500",
    ),
    (
        "delete-transport-failure",
        "1331",
        TAG,
        (("000", 28),),
        ("DELETE /releases/1331",),
        "DELETE /releases/1331",
        "transport failure",
    ),
    (
        "release-still-present-after-204-delete",
        "1331",
        TAG,
        (("204", 0), ("200", 0)),
        ("DELETE /releases/1331", "GET /releases/1331"),
        "/releases/1331",
        "HTTP 200",
    ),
    (
        "unrecorded-release-detected-by-tag-lookup",
        "",
        TAG,
        (("200", 0),),
        (f"GET /releases/tags/{TAG}",),
        f"/releases/tags/{TAG}",
        "HTTP 200",
    ),
    (
        "tag-present-after-cleanup",
        "1331",
        TAG,
        (("204", 0), ("404", 0), ("404", 0), ("200", 0)),
        (
            "DELETE /releases/1331",
            "GET /releases/1331",
            f"GET /releases/tags/{TAG}",
            f"GET /tags/{TAG}",
        ),
        f"/tags/{TAG}",
        "HTTP 200",
    ),
    (
        "verify-release-auth-401",
        "1331",
        TAG,
        (("204", 0), ("401", 0)),
        ("DELETE /releases/1331", "GET /releases/1331"),
        "/releases/1331",
        "HTTP 401",
    ),
    (
        "verify-release-tag-server-500",
        "1331",
        TAG,
        (("204", 0), ("404", 0), ("500", 0)),
        ("DELETE /releases/1331", "GET /releases/1331", f"GET /releases/tags/{TAG}"),
        f"/releases/tags/{TAG}",
        "HTTP 500",
    ),
    (
        "verify-tag-transport-failure-with-404-payload",
        "1331",
        TAG,
        (("204", 0), ("404", 0), ("404", 0), ("404", 7)),
        (
            "DELETE /releases/1331",
            "GET /releases/1331",
            f"GET /releases/tags/{TAG}",
            f"GET /tags/{TAG}",
        ),
        f"GET /tags/{TAG}",
        "transport failure",
    ),
    (
        "verify-non-http-000-response",
        "1331",
        TAG,
        (("204", 0), ("000", 0)),
        ("DELETE /releases/1331", "GET /releases/1331"),
        "/releases/1331",
        "HTTP 000",
    ),
)


@unittest.skipUnless(BASH, "POSIX bash is required")
class ReleaseCleanupTests(unittest.TestCase):
    def test_workflow_cleanup_step_contract(self):
        metadata, cleanup_script = extract_cleanup_step()
        self.assertIn(
            "if: ${{ always() && github.event_name == 'pull_request' }}",
            metadata,
        )
        self.assertIn("CI_GITEA_TOKEN: ${{ secrets.CI_GITEA_TOKEN }}", metadata)
        self.assertIn("RELEASE_ID: ${{ steps.create.outputs.release_id }}", metadata)
        self.assertIn("TAG: ${{ steps.ver.outputs.tag }}", metadata)
        self.assertNotIn("/git/refs/tags/", cleanup_script)
        result = subprocess.run(
            [BASH, "-n"],
            input=cleanup_script,
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(0, result.returncode, result.stderr)

    def test_cleanup_succeeds_and_verifies_postconditions(self):
        for (
            name,
            release_id,
            case_tag,
            responses,
            expected_paths,
            marker,
        ) in SUCCESS_CASES:
            with self.subTest(case=name):
                result, requests = run_cleanup_block(
                    release_id=release_id,
                    tag=case_tag,
                    responses=responses,
                )
                combined = result.stdout + result.stderr
                self.assertEqual(0, result.returncode, combined)
                self.assertEqual(expand_requests(expected_paths), requests)
                self.assertIn(marker, result.stdout)
                self.assertNotIn(SECRET_SENTINEL, combined)
                for request in requests:
                    self.assertNotIn("/git/refs/tags/", request)
                    self.assertNotIn("/null", request)
                    self.assertFalse(request.endswith("/"))

    def test_cleanup_fails_on_delete_or_verification_errors(self):
        for (
            name,
            release_id,
            case_tag,
            responses,
            expected_paths,
            expected_resource,
            expected_reason,
        ) in FAILURE_CASES:
            with self.subTest(case=name):
                result, requests = run_cleanup_block(
                    release_id=release_id,
                    tag=case_tag,
                    responses=responses,
                )
                combined = result.stdout + result.stderr
                self.assertNotEqual(0, result.returncode, combined)
                self.assertEqual(expand_requests(expected_paths), requests)
                self.assertIn(expected_resource, result.stderr)
                self.assertIn(expected_reason, result.stderr)
                self.assertNotIn("Verified smoke release/tag absence", combined)
                self.assertNotIn(SECRET_SENTINEL, combined)
                for request in requests:
                    self.assertNotIn("/git/refs/tags/", request)


if __name__ == "__main__":
    unittest.main()
