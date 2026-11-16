from dataclasses import dataclass
from enum import StrEnum
from typing import Annotated, Generic, TypeVar, Any, Literal
from pydantic import Field, GetCoreSchemaHandler
from pydantic_core import core_schema
from pydantic.json_schema import JsonSchemaValue
from swc.aeon.schema import BaseSchema

__all__ = ["TimestampSource", "Vector2", "Vector3", "SoftwareEvent", "bind_typename"]


_SCALAR_TYPES = frozenset({"string", "integer", "number", "boolean"})


def bind_typename(schema: JsonSchemaValue, typename: str) -> JsonSchemaValue:
    """Applies the `x-sgen-typename` tag, binding a definition to an existing type.

    Bonsai.Sgen 0.9.0 only honours the tag where it emits a named type. i.e an object, array or 
    enum. A bare scalar is inlined as a primitive, so it is dropped with no warning. 
    Wrapping the scalar in `allOf` allows the binding.

    The wrap must stay conditional, as an object carries through to C# and errors.

    Replace with the `swc.aeon.schema` helper once aeon_api#97 lands.
    """
    if schema.get("type") in _SCALAR_TYPES and "enum" not in schema and "allOf" not in schema:
        schema["allOf"] = [{"type": schema.pop("type")}]
    schema["x-sgen-typename"] = typename
    return schema

@dataclass(frozen=True)
class _Range:
    """The C# type's range, as the integer schema's ge and le where a model's own Field sets none: a model's bound wins.
    As a Field in the alias (Field(ge=...)) it replaced the model's own ge and le instead (DM's thresholdLicks took 0)."""

    low: int
    high: int

    def __get_pydantic_core_schema__(self, source: Any, handler: GetCoreSchemaHandler) -> core_schema.CoreSchema:
        schema = handler(source)
        if schema["type"] != "int":
            raise TypeError(f"_Range bounds an int, not a {schema['type']}")
        schema.setdefault("ge", self.low)
        schema.setdefault("le", self.high)
        return schema


SByte = Annotated[int, _Range(-128, 127)]
Byte = Annotated[int, _Range(0, 255)]
Short = Annotated[int, _Range(-32768, 32767)]
UShort = Annotated[int, _Range(0, 65535)]
Int = Annotated[int, _Range(-2147483648, 2147483647)]
UInt = Annotated[int, _Range(0, 4294967295)]
Long = Annotated[int, _Range(-9223372036854775808, 9223372036854775807)]
ULong = Annotated[int, _Range(0, 18446744073709551615)]

Float = float
Double = float
String = str
Bool = bool


class TimestampSource(StrEnum):
    NULL = "null"
    HARP = "harp"
    RENDER = "render"
    ARDUINO = "arduino"


class Vector2(BaseSchema):
    x: float = Field(description="X coordinate of the point.")
    y: float = Field(description="Y coordinate of the point.")


class Vector3(Vector2):
    z: float = Field(description="Z coordinate of the point.")


TData = TypeVar("TData", bound=Any)


class SoftwareEvent(BaseSchema, Generic[TData]):
    """
    A software event is a generic event that can be used to track any event that occurs in the software.
    """

    name: str = Field(..., description="The name of the event.")
    timestamp: float | None = Field(default=None, description="The timestamp of the event.")
    timestamp_source: TimestampSource = Field(
        default=TimestampSource.NULL,
        description="The source of the timestamp. Typically either a harp device or on the visual render loop",
    )
    frame_index: int | None = Field(default=None, ge=0, description="The frame index of the event.")
    frame_timestamp: float | None = Field(default=None, description="The timestamp of the frame.")
    data: TData | None = Field(default=None, description="The data payload of the event.")


class DiscriminatorTypeMixin:
    """Mixin to set `discriminator_type` to the subclass name for types participating in a discriminated union."""

    def __init_subclass__(cls, **kwargs):
        """Injects `discriminator_type` as a Literal of the subclass name."""
        super().__init_subclass__(**kwargs)
        name = cls.__name__
        cls.__annotations__["discriminator_type"] = Literal[name]
        cls.discriminator_type = name
