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


def test_the_bank_s_longest_ramp_down_is_the_context(tmp_path):
    # Two conditions of 4000-row cycles at 100 kS/s; the longest laser-on tail is 10 cycles: 0.4 s.
    bank = tmp_path / "Settings_ZapitBank_x.json"
    variant = {"laser_on": True, "tail_cycles": 5}
    conditions = [{"scan_cycle": {"offset": 0, "length": 4000}, "variants": [variant, {"laser_on": False, "tail_cycles": 50}]},
                  {"scan_cycle": {"offset": 0, "length": 4000}, "variants": [{**variant, "tail_cycles": 10}]}]
    bank.write_text(json.dumps({"version": 2, "sample_rate": 100000, "conditions": conditions}), encoding="utf-8")
    on = {"optogenetics": {"enabled": True, "zapitConditionsPath": "x.yml"}}
    assert check_task.context(on, str(bank)) == {"zapit_ramp_down_seconds": 0.4}
    assert check_task.context(on) == {}
    assert check_task.context({"optogenetics": {**on["optogenetics"], "enabled": False}}, str(bank)) == {}
    assert check_task.context(on, str(tmp_path / "gone.json")) == {}
    bank.write_text(json.dumps({"version": 1}), encoding="utf-8")  # the workflow refuses it at Go
    assert check_task.context(on, str(bank)) == {}
