"""The lab's speaker array: every speaker played equalised, and a channel for the lab's one penalty sound."""
from pydantic import Field, model_validator

from open_rigs.components.audio import SpeakerArray


class EqualisedSpeakerArray(SpeakerArray):
    __doc__ = SpeakerArray.__doc__
    penalty_sound_channel: int = Field(ge=0, description="Mixer output channel the penalty sound plays on.")

    @model_validator(mode="after")
    def _every_speaker_filtered(self):
        # Every speaker is played equalised; OpenRigs.Audio SpeakerSet.RequireFilters refuses at Start.
        missing = sorted(name for name, speaker in self.speakers.items() if speaker.filter is None)
        if missing:
            raise ValueError(
                f"speakerArray: speaker {', '.join(missing)} has no filter, and every speaker is played equalised "
                "(lab rig model, open_rigs.rigs.lab_rig). "
                f"Give each its SpeakerFilter artefact, as in `filter: calibration/speaker-{missing[0]}.json`."
            )
        return self
