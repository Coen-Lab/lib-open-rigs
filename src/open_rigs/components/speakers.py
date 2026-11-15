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

    @model_validator(mode="after")
    def _channels_fit(self):
        # The channel rules OpenRigs.Audio SpeakerSet.Load refuses at Start, said here first.
        count = self.device.channel_count
        speakers = sorted(self.speakers.items())
        problems = [f"speaker '{name}' channel {speaker.channel} is not one of the audio device's {count} channels (0 to {count - 1})"
                    for name, speaker in speakers if speaker.channel >= count]
        by_channel: dict[int, list[str]] = {}
        for name, speaker in speakers:
            by_channel.setdefault(speaker.channel, []).append(name)
        problems += [f"speakers {', '.join(names)} share channel {channel}: each speaker needs its own"
                     for channel, names in sorted(by_channel.items()) if len(names) > 1]
        if self.sync_channel is not None and self.sync_channel >= count:
            problems.append(f"syncChannel {self.sync_channel} is not one of the audio device's {count} channels (0 to {count - 1})")
        if self.penalty_sound_channel not in by_channel:
            problems.append(f"penaltySoundChannel {self.penalty_sound_channel} has no speaker on it, and the penalty sound is never "
                            "played unequalised: set it to a speaker's channel")
        if problems:
            raise ValueError(f"speakerArray: {'; '.join(problems)} (lab rig model, open_rigs.rigs.lab_rig).")
        return self
