from typing import Dict, List

from pydantic import Field
from swc.aeon.schema import BaseSchema

import open_rigs.core.base as data_types
from open_rigs.core.artefacts import ArtefactPath
from open_rigs.core.calibration import Calibration


class SphericalPosition(BaseSchema):
    """A position on a sphere centred on the subject."""

    azimuth: data_types.Double = Field(
        ge=-180, le=180, description="Azimuth, in degrees. Zero is straight ahead, positive to the right."
    )
    elevation: data_types.Double = Field(
        ge=-90, le=90, description="Elevation, in degrees. Zero is the horizontal plane."
    )
    radius: data_types.Double = Field(default=1.0, gt=0, description="Distance from the subject, in metres.")


class Speaker(BaseSchema):
    """A single speaker in a spatial audio array."""

    position: SphericalPosition = Field(description="Position of the speaker relative to the subject.")
    channel: data_types.Int = Field(ge=0, description="Mixer output channel driving this speaker.")
    gain: data_types.Double = Field(
        default=1.0, ge=0, description="Gain correction applied to this speaker's channel."
    )
    filter: ArtefactPath | None = Field(
        default=None,
        examples=["calibration/speaker-left.json"],
        description="Relative path of this speaker's SpeakerFilter artefact; none means the channel is not equalised.",
    )


class SpeakerFilter(Calibration):
    """An equalisation filter measured for one speaker, with its FIR taps inline."""

    taps: List[data_types.Double] = Field(min_length=1, description="FIR filter coefficients, in order.")
    sample_rate: data_types.Double = Field(
        gt=0, description="Sampling rate the taps were designed for, in Hz; must match the audio device."
    )


class AudioDevice(BaseSchema):
    """A multi-channel audio output device driven through the mixer."""

    device_name: str = Field(
        examples=["Speakers (XMOS xCORE-200 MC (UAC2.0))"], description="The name of the audio output device."
    )
    host_api: str = Field(
        default="Windows WDM-KS", description="The audio host API used to open the device."
    )
    sample_rate: data_types.Int = Field(default=48000, gt=0, description="Output sampling rate, in Hz.")
    channel_count: data_types.Int = Field(default=32, gt=0, description="Number of output channels.")
    suggested_latency: data_types.Double = Field(
        default=0.002, gt=0, description="Suggested output latency, in seconds."
    )
    boot_delay: data_types.Double = Field(
        default=3.0,
        ge=0,
        description="Delay before starting the mixer, in seconds. Guards against a driver fault on startup.",
    )


class SpeakerArray(BaseSchema):
    """A spatial speaker array driven by a single audio device."""

    device: AudioDevice = Field(description="The audio device driving the array.")
    speakers: Dict[str, Speaker] = Field(
        description="Speakers in the array, keyed by name.",
        examples=[
            {
                "left": {"position": {"azimuth": -90, "elevation": 15}, "channel": 0},
                "right": {"position": {"azimuth": 90, "elevation": 15}, "channel": 1},
                "centre": {"position": {"azimuth": 0, "elevation": 15}, "channel": 2},
            }
        ],
    )
    sync_channel: data_types.Int | None = Field(
        default=None, ge=0, description="Channel carrying a sync copy of each stimulus, for alignment with acquisition."
    )
    sync_gain: data_types.Double = Field(default=0.2, ge=0, description="Gain applied to the sync channel.")
    unused_channels: list[data_types.Int] = Field(
        default_factory=list, description="Output channels with nothing connected."
    )
