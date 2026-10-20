"""Compare maintained developer-tool documentation with on-disk scripts."""

from pathlib import Path

SUFFIXES = {".ps1", ".sh", ".py"}


def script_paths(root: Path) -> set[str]:
    return {
        path.relative_to(root).as_posix()
        for path in (root / "scripts").rglob("*")
        if path.is_file() and path.suffix.lower() in SUFFIXES
    }


def inventory_paths(document: Path) -> set[str]:
    entries = set()
    for line in document.read_text(encoding="utf-8").splitlines():
        if not line.startswith("| `scripts/"):
            continue
        cells = [cell.strip() for cell in line.strip("|").split("|")]
        if len(cells) != 4 or not all(cells):
            raise ValueError(f"incomplete maintained-tool row: {line}")
        name = cells[0].strip("`")
        if name in entries:
            raise ValueError(f"duplicate maintained tool: {name}")
        entries.add(name)
    return entries


def validate_inventory(root: Path) -> None:
    actual = script_paths(root)
    listed = inventory_paths(root / "docs/maintained-developer-tools.md")
    unlisted = actual - listed
    stale = listed - actual
    if unlisted or stale:
        raise ValueError(
            f"unlisted scripts: {sorted(unlisted)}; stale entries: {sorted(stale)}"
        )
