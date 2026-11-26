"""The core stays free of the ``zapit`` extra, and the library stays free of Qt, sockets and the keyring."""
import ast
from pathlib import Path

import pytest

SRC = Path(__file__).parents[2] / "src" / "open_rigs"
ZAPIT = SRC / "zapit"
EXTRA = ("open_rigs.zapit", "scipy", "cv2")
BANNED_INSIDE = ("zmq", "PyQt6", "PySide6", "lab_keyring")


def imported(path: Path) -> list[str]:
    names = []
    for node in ast.walk(ast.parse(path.read_text(encoding="utf-8"))):
        if isinstance(node, ast.Import):
            names += [a.name for a in node.names]
        elif isinstance(node, ast.ImportFrom) and node.module and not node.level:
            names.append(node.module)
            if node.module == "open_rigs":
                names += [f"open_rigs.{a.name}" for a in node.names]
    return names


def under(name: str, roots: tuple[str, ...]) -> bool:
    return any(name == r or name.startswith(r + ".") for r in roots)


def files(inside: bool) -> list[Path]:
    return [p for p in SRC.rglob("*.py") if (ZAPIT in p.parents) == inside]


@pytest.mark.parametrize("path", files(inside=False), ids=lambda p: p.relative_to(SRC).as_posix())
def test_nothing_outside_the_folder_imports_the_extra(path):
    assert not [n for n in imported(path) if under(n, EXTRA)]


@pytest.mark.parametrize("path", files(inside=True), ids=lambda p: p.relative_to(SRC).as_posix())
def test_the_library_imports_no_qt_sockets_or_keyring(path):
    assert not [n for n in imported(path) if under(n, BANNED_INSIDE)]
