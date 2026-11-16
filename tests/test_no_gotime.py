"""A trial's start is TrialStartTime: the old QuietWait name GoTime stays out of the Core and Devices source."""

from pathlib import Path

ROOT = Path(__file__).parents[1]


def test_no_gotime_in_core_source():
    found = [str(p.relative_to(ROOT)) for folder in ("OpenRigs.Core", "OpenRigs.Core.Tests", "OpenRigs.Devices")
             for p in (ROOT / "src" / folder).rglob("*.cs") if "GoTime" in p.read_text(encoding="utf-8-sig")]
    assert not found, f"GoTime (now QuietWait.TrialStartTime) remains in {found}"
