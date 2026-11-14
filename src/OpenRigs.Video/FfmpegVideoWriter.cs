using System;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.Harp;
using OpenRigs.Shared;

namespace OpenRigs.Video
{
    /// <summary>
    /// Records a camera's frames to a Matroska (.mkv) H.264 video through an ffmpeg.exe child
    /// process fed raw frames on its stdin, and a CSV beside it with one row per frame in the video.
    /// The codec and ffmpeg.exe are rig.yml's <c>videoEncoder</c>. Frames wait for ffmpeg in a
    /// bounded queue, so the camera's thread is never held; a frame that finds it full is dropped,
    /// counted and reported. When the frames end the file is closed before the recording ends,
    /// at most <see cref="StopTimeout"/> later.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Sink)]
    [Description("Records camera frames to an H.264 .mkv through ffmpeg.exe (rig.yml's videoEncoder: codec, ffmpegPath; else libx264 and the ffmpeg.exe on PATH), with a .csv beside it: harpTime, frameId, cameraClockNs, exposureUs, gainDb, one row per frame in the video. Stops the workflow at Start without ffmpeg.exe, and when ffmpeg fails. At the frames' end or Stop it closes the file, waiting at most 20 s, then kills ffmpeg; frames dropped (the queue of 250 full) or ffmpeg failing are an error, or at Stop are written to stderr and a .error.txt beside the video.")]
    public class FfmpegVideoWriter
    {
        /// <summary>How many frames may wait for ffmpeg: 5 s at 50 Hz, 390 MB of 1440 x 1080 Mono8 frames.</summary>
        internal const int DefaultQueueCapacity = 250;

        /// <summary>How long the file may take to close once the frames end: within the 30 s a rig's agent gives a stopped workflow.</summary>
        internal static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(20);

        /// <summary>Gets or sets the video's file name; the CSV is the same name with .csv.</summary>
        [Description("The video file to write, ending .mkv; the frame log is the same name with .csv. Neither may already exist.")]
        [Editor("Bonsai.Design.SaveFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string? FileName { get; set; }

        /// <summary>Gets or sets the frame rate written into the video.</summary>
        [Description("The frame rate written into the video, in frames a second: the camera trigger's. Each frame's own time is its harpTime in the CSV.")]
        public double FrameRate { get; set; } = 50;

        /// <summary>Gets or sets the root under which this machine's rig.yml names the encoder.</summary>
        [Description("The root under which each machine has its own configuration directory, <ConfigRoot>\\<MACHINE-NAME>\\rig.yml, whose videoEncoder names the codec and ffmpeg.exe.")]
        public string ConfigRoot { get; set; } = RigDevices.DefaultConfigRoot;

        /// <summary>How many frames may wait for ffmpeg; set by tests.</summary>
        internal int QueueCapacity { get; set; } = DefaultQueueCapacity;

        /// <summary>How long the file may take to close once the frames end; set by tests.</summary>
        internal TimeSpan StopTimeout { get; set; } = DefaultStopTimeout;

        /// <summary>
        /// Records each frame and passes it on. Completes once the file is closed, or fails naming
        /// what went wrong. Disposed before the frames end (a Stop, or the next hour's file), it
        /// returns at once and the file closes on its own, the process kept open until it has.
        /// A frame is a <see cref="VideoDataFrame"/> or any type with the same members (Image, and
        /// ChunkData with FrameID, Timestamp and optionally ExposureTime and Gain), as
        /// OpenRigs.Devices' TriggeredSpinnaker include gives.
        /// </summary>
        public IObservable<Timestamped<TFrame>> Process<TFrame>(IObservable<Timestamped<TFrame>> source)
        {
            var read = FrameReader<TFrame>.Read;  // throws here, naming a member the frame type lacks
            return Observable.Create<Timestamped<TFrame>>(observer =>
            {
                FfmpegRecording recording;
                try
                {
                    recording = new FfmpegRecording(FileName ?? "", FrameRate, VideoEncoder.ForThisRig(ConfigRoot), QueueCapacity, StopTimeout, Console.Error);
                }
                catch (Exception ex)
                {
                    observer.OnError(ex);
                    return Disposable.Empty;
                }
                var subscription = source.SubscribeSafe(System.Reactive.Observer.Create<Timestamped<TFrame>>(
                    frame =>
                    {
                        try { recording.Add(Timestamped.Create(read(frame.Value)!, frame.Seconds)); }
                        catch (Exception ex)
                        {
                            observer.OnError(ex);
                            return;
                        }
                        observer.OnNext(frame);
                    },
                    error =>
                    {
                        recording.End();
                        observer.OnError(error);
                    },
                    () =>
                    {
                        var problem = recording.Finish();
                        if (problem != null) observer.OnError(new InvalidOperationException(problem));
                        else observer.OnCompleted();
                    }));
                return Disposable.Create(() =>
                {
                    subscription.Dispose();
                    recording.End();
                });
            });
        }
    }
}
