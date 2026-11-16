from pathlib import Path

ROOT = Path(__file__).parents[1]


def test_no_doc_holds_a_control_character():
    # building-blocks.md once held a carriage return where its path should read \rig.yml.
    found = [f"{path.relative_to(ROOT)}:{n}" for path in sorted((ROOT / "docs").rglob("*.md"))
             for n, line in enumerate(path.read_bytes().split(b"\n"), 1)
             if any(c < 32 and c != 9 for c in line.rstrip(b"\r"))]
    assert found == []


def test_every_name_the_calibration_article_gives_is_in_the_source():
    # calibration.md had drifted from the code (its layout, its full-path rule). Each backticked CamelCase
    # name it gives (an operator, include or type) must be in src.
    import re

    text = (ROOT / "docs" / "articles" / "calibration.md").read_text(encoding="utf-8")
    names = sorted(set(re.findall(r"`([A-Z][a-z]+(?:[A-Z][a-z0-9]*)+)`", text)))
    files = [p for p in (ROOT / "src").rglob("*") if p.suffix in (".cs", ".bonsai", ".py") and "bin" not in p.parts and "obj" not in p.parts]
    source = "\n".join([p.stem for p in files] + [p.read_text(encoding="utf-8", errors="ignore") for p in files])
    assert names and [n for n in names if n not in source] == []
