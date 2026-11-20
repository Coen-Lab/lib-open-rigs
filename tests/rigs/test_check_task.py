import io
import json

import pytest
from pydantic import BaseModel, model_validator

from open_rigs.rigs import check_task


class Timing(BaseModel):
    iti: float
    consumption: float

    @model_validator(mode="after")
    def _fits(self):
        if self.consumption > self.iti:
            raise ValueError("consumption exceeds iti")
        return self


class Parameters(BaseModel):
    timing: Timing


def test_problems_word_the_models_refusals():
    assert check_task.problems(Parameters, {"timing": {"iti": 1, "consumption": 0.5}}) == []
    assert check_task.problems(Parameters, {"timing": {"iti": 1, "consumption": 2}}) == ["timing: consumption exceeds iti"]


def test_a_combinations_refusal_is_its_condition_and_the_models_words():
    message = ("trial_conditions[0] (visual) with {'target': 'left'}: 1 validation error for Task\n  Value error, consumption exceeds "
               "iti [type=value_error, input_value={}, input_type=dict]\n    For further information visit https://errors.pydantic.dev")
    inner = check_task._inner(message)  # pyright: ignore[reportPrivateUsage]
    assert inner == "trial_conditions[0] (visual): consumption exceeds iti"


def test_serves_one_answer_per_line(monkeypatch, capsys):
    monkeypatch.setattr(check_task, "model", lambda: Parameters)
    monkeypatch.setattr("sys.argv", ["check_task"])
    lines = [{"taskParameters": {"timing": {"iti": 1, "consumption": c}}} for c in (0.5, 2)]
    monkeypatch.setattr("sys.stdin", io.StringIO("".join(json.dumps(x) + "\n" for x in lines)))
    check_task.main()
    said = [json.loads(x) for x in capsys.readouterr().out.splitlines()]
    assert said == [{"model": f"{__name__}:Parameters"}, {"problems": []}, {"problems": ["timing: consumption exceeds iti"]}]


def test_no_model_says_why(monkeypatch, capsys):
    monkeypatch.setattr("sys.argv", ["check_task"])
    with pytest.raises(SystemExit):
        check_task.main()
    assert "open_rigs.task entry points" in json.loads(capsys.readouterr().out)["error"]


def test_the_zapit_file_s_largest_ramp_down_is_the_context_where_the_file_is(tmp_path):
    # ZapitTests.cs's CheckReturnsTheCountAndTheMaxRampDown file: 0.4 s.
    path = tmp_path / "conditions.yml"
    path.write_text("stimLocations01:\n  offRampDownDuration_ms: 250\nstimLocations02:\n  offRampDownDuration_ms: 400\n", encoding="utf-8")
    on = {"optogenetics": {"enabled": True, "zapitConditionsPath": str(path)}}
    assert check_task.context(on) == {"zapit_ramp_down_seconds": 0.4}
    assert check_task.context({"optogenetics": {**on["optogenetics"], "enabled": False}}) == {}
    assert check_task.context({"optogenetics": {"enabled": True, "zapitConditionsPath": str(tmp_path / "gone.yml")}}) == {}
    path.write_text("stimLocations01:\n  laserPowerInMW: 2\n", encoding="utf-8")  # the workflow refuses it at Go
    assert check_task.context(on) == {}
