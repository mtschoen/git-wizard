"""Resolve POSIX bash from explicit PATH entries, excluding Windows launchers."""

import os
import sys
from collections.abc import Callable

_WINDOWS_LAUNCHER_DIRECTORY_NAMES = frozenset(
    {"system32", "syswow64", "sysnative", "windowsapps"}
)


def _is_windows_launcher_location(candidate: str) -> bool:
    parts = candidate.replace("\\", "/").lower().split("/")
    return any(part in _WINDOWS_LAUNCHER_DIRECTORY_NAMES for part in parts[:-1])


def _is_executable_file(candidate: str) -> bool:
    return os.path.isfile(candidate) and os.access(candidate, os.X_OK)


def resolve_bash(
    *,
    search_path: str | None = None,
    is_windows: bool | None = None,
    is_executable: Callable[[str], bool] = _is_executable_file,
) -> str:
    """Return absolute bash from PATH only, never implicitly searching cwd.

    Relative entries become absolute at lookup time. Windows searches bash.exe
    and skips System32, SysWOW64, Sysnative and WindowsApps launcher locations.
    Failure names every PATH entry, including skipped launcher directories.
    """
    windows = sys.platform == "win32" if is_windows is None else is_windows
    raw_path = os.environ.get("PATH", "") if search_path is None else search_path
    entries = [entry for entry in raw_path.split(os.pathsep) if entry]
    executable_name = "bash.exe" if windows else "bash"
    for entry in entries:
        candidate = os.path.abspath(os.path.join(entry, executable_name))
        if windows and _is_windows_launcher_location(candidate):
            continue
        if is_executable(candidate):
            return candidate
    searched = ", ".join(entries) if entries else "(empty PATH)"
    raise FileNotFoundError(f"no POSIX bash found; searched PATH entries: {searched}")
