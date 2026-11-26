"""The core stays free of the ``zapit`` extra, and the library stays free of Qt, sockets and the keyring."""
import ast
from pathlib import Path

import pytest

SRC = Path(__file__).parents[2] / "src" / "open_rigs"
ZAPIT = SRC / "zapit"
EXTRA = ("open_rigs.zapit", "scipy", "cv2")
BANNED_INSIDE = ("zmq", "PyQt6", "PySide6", "lab_keyring")


def imported(path: Path, root: Path = SRC) -> list[str]:
    """Every module a file imports, absolute: relative imports are resolved against the file's package."""
    package = list(path.relative_to(root.parent).with_suffix("").parts)
    if path.name != "__init__.py":
        package.pop()
    names = []
    for node in ast.walk(ast.parse(path.read_text(encoding="utf-8"))):
        if isinstance(node, ast.Import):
            names += [a.name for a in node.names]
        elif isinstance(node, ast.ImportFrom):
            base = package[: len(package) - (node.level - 1)] if node.level else []
            module = ".".join(base + ([node.module] if node.module else []))
            if module:
                names.append(module)
                names += [f"{module}.{a.name}" for a in node.names]   # ``from open_rigs import zapit``
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


def test_relative_imports_are_resolved(tmp_path):
    pkg = tmp_path / "open_rigs" / "components"
    pkg.mkdir(parents=True)
    (pkg / "x.py").write_text("from ..zapit import bank\nfrom .. import zapit\nfrom . import y\nfrom .y import z\n")
    names = imported(pkg / "x.py", tmp_path / "open_rigs")
    assert {"open_rigs.zapit", "open_rigs.zapit.bank", "open_rigs.components.y", "open_rigs.components.y.z"} <= set(names)
    assert any(under(n, EXTRA) for n in names)
