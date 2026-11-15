from pathlib import Path

ROOT = Path(__file__).parents[1]


def test_no_doc_holds_a_control_character():
    # building-blocks.md once held a carriage return where its path should read \rig.yml.
    found = [f"{path.relative_to(ROOT)}:{n}" for path in sorted((ROOT / "docs").rglob("*.md"))
             for n, line in enumerate(path.read_bytes().split(b"\n"), 1)
             if any(c < 32 and c != 9 for c in line.rstrip(b"\r"))]
    assert found == []
