from open_rigs.devices import StageAxisMapping

# OpenRigs.Devices' LickSpoutDrivers.cs: StepperDriver-A motor 0 base transverse, 1 left radial, 2 right radial;
# StepperDriver-B motor 0 left elevation, 1 right elevation.
EXPECTED = {
    "base_transverse": ("a", 0),
    "left_radial": ("a", 1),
    "right_radial": ("a", 2),
    "left_elevation": ("b", 0),
    "right_elevation": ("b", 1),
}


def test_the_default_mapping_is_the_one_the_stage_drives():
    mapping = StageAxisMapping()
    assert {axis: (getattr(mapping, axis).board, getattr(mapping, axis).motor) for axis in EXPECTED} == EXPECTED
