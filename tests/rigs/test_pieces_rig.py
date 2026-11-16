"""The rig OpenRigs.Devices.Tests' PiecesSubscribeTests run every piece against: filled_rig's rig for the domes' rig list
(every piece's device), with every port COM97, which no machine has. Regenerate it after a template change with
`uv run python tests/rigs/test_pieces_rig.py`."""

import re
import sys
from pathlib import Path, PureWindowsPath

from open_rigs.rigs import workflow_lint as lint

ROOT = Path(__file__).parents[2]
FIXTURE = ROOT / "src" / "OpenRigs.Devices.Tests" / "Pieces" / "rig.yml"
RIG_LIST = ('<WorkflowBuilder Version="2.9.0" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns="https://bonsai-rx.org/2018/workflow"><Workflow><Nodes>'
        f'<Expression xsi:type="IncludeWorkflow" Path="{lint.READ_LAB_RIG}" />'
        f'<Expression xsi:type="IncludeWorkflow" Path="{lint.DEVICES}DomeHardware.bonsai" />'
        '</Nodes><Edges /></Workflow></WorkflowBuilder>')


def pieces_rig(tmp: Path) -> str:
    (tmp / "src").mkdir(parents=True, exist_ok=True)
    (tmp / "src" / "main.bonsai").write_text(RIG_LIST, encoding="utf-8")
    # A Windows path on every OS: CI's Linux runners write rootPath as the rigs do.
    text, _, _ = lint.filled_rig(lint.Workflow(tmp), PureWindowsPath("C:/nowhere"), "PIECES")
    return re.sub(r"portName: COM\d+", "portName: COM97", text)


def test_the_pieces_rig_is_filled_rig_s_with_no_port_that_exists(tmp_path):
    assert FIXTURE.read_text(encoding="utf-8") == pieces_rig(tmp_path), "regenerate: uv run python tests/rigs/test_pieces_rig.py"


if __name__ == "__main__":
    import tempfile

    with tempfile.TemporaryDirectory() as tmp:
        FIXTURE.parent.mkdir(parents=True, exist_ok=True)
        FIXTURE.write_text(pieces_rig(Path(tmp)), encoding="utf-8", newline="\n")
    sys.exit(0)
