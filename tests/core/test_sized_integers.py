"""A model's own bound on a sized integer wins; the C# type's range holds otherwise."""

from typing import Optional

import pydantic
import pytest
from pydantic import BaseModel, Field

from open_rigs.core.base import Int, UInt


class Task(BaseModel):
    licks: Int = Field(ge=1)
    repeats: Optional[Int] = Field(default=None, ge=0)
    anything: Int = 0
    count: UInt = Field(default=1, gt=0)


@pytest.mark.parametrize("bad", [{"licks": 0}, {"licks": 1, "repeats": -1}, {"licks": 1, "anything": 2**31}, {"licks": 1, "count": 0}])
def test_out_of_range_values_are_refused(bad):
    with pytest.raises(pydantic.ValidationError):
        Task(**bad)


def test_schema_carries_the_model_s_bound_and_the_type_s_range():
    properties = Task.model_json_schema()["properties"]
    assert (properties["licks"]["minimum"], properties["anything"]["minimum"]) == (1, -2147483648)
    assert properties["repeats"]["anyOf"][0]["minimum"] == 0
