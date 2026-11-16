"""The lab's cameras and the machine's video encoder."""
from typing import Literal, Optional

from pydantic import Field, field_validator
from swc.aeon.schema import BaseSchema

from open_rigs.components.live_view import LiveView
from open_rigs.video import SpinnakerCamera as _SpinnakerCamera


class SpinnakerCamera(_SpinnakerCamera):
    __doc__ = _SpinnakerCamera.__doc__
    live_view: Optional[LiveView] = Field(default=None, description="Live view settings for this camera (OpenRigs.LiveView).")
    # Only formats the cameras' logger writes: LogVideoFfmpeg's FfmpegVideoWriter takes 8-bit frames alone.
    pixel_format: Optional[Literal["Mono8"]] = Field(
        default=None,
        description="The pixel format the camera sends, a Spinnaker PixelFormat name the video logger can write: Mono8. Set each time the camera opens; left out, the camera keeps the format stored on it, as upstream's operator does. The lab's rig.yml files state it.",
    )


class VideoEncoder(BaseSchema):
    """How this machine encodes its cameras' video: OpenRigs.Video's FfmpegVideoWriter (OpenRigs.Logging's LogVideoFfmpeg)
    reads it at Start. The encoder is the machine's, so it is one setting for every camera."""

    # A string with a pattern, not a Literal: a Literal becomes a C# enum, and the workflow's YAML
    # reader takes only the enum's C# names (H264Nvenc), never h264_nvenc. FfmpegVideoWriter checks it.
    codec: str = Field(
        default="libx264",
        pattern=r"^(libx264|h264_nvenc|h264_qsv)$",
        description="The FFmpeg H.264 encoder: h264_nvenc on an NVIDIA GPU, h264_qsv on Intel Quick Sync, libx264 (software, the default) on any machine with the cores for it.",
    )
    ffmpeg_path: Optional[str] = Field(
        default=None,
        description="The full path of ffmpeg.exe. Left out: the first ffmpeg.exe on PATH. The workflow stops at Start when neither is there.",
    )

    @field_validator("codec", mode="before")
    @classmethod
    def _null_codec_is_libx264(cls, value: object) -> object:
        # rig.yml's codec: null is libx264, as OpenRigs.Video's VideoEncoder.Read takes it.
        return "libx264" if value is None else value
