"""A subject named Kind-Instance (BehaviorBoard-Main.Commands) is a rig.yml device's: the lab's names check holds every
such name to the lab's device names, so a piece's own subjects never take that form. LickSpoutStage-Main.* did on
16 Nov 2026, though the stage's rig.yml entry had been replaced by StepperDriver a and b on 5 Nov, and every workflow
with the stage was refused at launch. A reward valve is the one piece named from rig.yml's wiring (name: left is
RewardValve-Left), not from a device group."""

import re
from pathlib import Path

import yaml

SRC = Path(__file__).parents[2] / "src"
SUBJECT = re.compile(r"<(?:\w+:)?Name>([A-Z][A-Za-z0-9]*)-[A-Z][A-Za-z0-9]*(?:\.[A-Za-z]+)?</")


def test_every_kind_instance_subject_names_a_rig_yml_device_kind():
    groups = yaml.safe_load((SRC / "open_rigs" / "rigs" / "rig-template.yml").read_text(encoding="utf-8"))
    kinds = {key[0].upper() + key[1:] for key in groups} | {"RewardValve"}
    wrong = sorted({f"{path.relative_to(SRC)}: {kind}" for path in SRC.glob("OpenRigs.*/**/*.bonsai")
                    for kind in SUBJECT.findall(path.read_text(encoding="utf-8-sig")) if kind not in kinds})
    assert wrong == []
