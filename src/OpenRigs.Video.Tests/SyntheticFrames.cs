using System;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using Bonsai;
using Bonsai.Harp;
using OpenCV.Net;

namespace OpenRigs.Video.Tests
{
    /// <summary>
    /// Count Mono8 or BGR frames as a camera gives them, as fast as they can be made, on a
    /// background thread of their own (as a camera driver's), then completes. For headless runs.
    /// </summary>
    [Description("Count synthetic frames as a camera gives them, as fast as they can be made, then completes.")]
    public class SyntheticFrames : Source<Timestamped<VideoDataFrame>>
    {
        /// <summary>How many frames to give.</summary>
        public int Count { get; set; } = 3;

        /// <summary>The frames' width.</summary>
        public int Width { get; set; } = 320;

        /// <summary>The frames' height.</summary>
        public int Height { get; set; } = 240;

        /// <summary>1 (Mono8) or 3 (BGR).</summary>
        public int Channels { get; set; } = 1;

        /// <summary>
        /// How long to wait before the first frame, in milliseconds: LogVideoFfmpeg's file name is
        /// made from PathPrefix, which a rig's LogController publishes long before a camera's
        /// first frame.
        /// </summary>
        public int StartDelayMs { get; set; } = 500;

        /// <summary>
        /// The first frame's Harp time; frame n's is n / 50 s later. The default leaves 2400 s of
        /// its hour, so GroupByTime gives the frames one file; a time just before the hour ends
        /// shortens the hour, and the frames after it go to the next hour's file.
        /// </summary>
        public double StartSeconds { get; set; } = 3900000000.0;

        /// <summary>Milliseconds between frames, as a triggered camera paces them; 0 makes them as fast as it can.</summary>
        public int IntervalMs { get; set; }

        /// <summary>Gives the frames.</summary>
        public override IObservable<Timestamped<VideoDataFrame>> Generate()
        {
            var count = Count;
            var size = new Size(Width, Height);
            var channels = Channels;
            var startDelay = StartDelayMs;
            var start = StartSeconds;
            var interval = IntervalMs;
            return Observable.Create<Timestamped<VideoDataFrame>>(observer =>
            {
                var cancel = new CancellationTokenSource();
                var thread = new Thread(() =>
                {
                    if (cancel.Token.WaitHandle.WaitOne(startDelay)) return;
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    for (var id = 0; id < count && !cancel.IsCancellationRequested; id++)
                    {
                        if (interval > 0)
                        {
                            var wait = (int)(id * (long)interval - clock.ElapsedMilliseconds);
                            if (wait > 0 && cancel.Token.WaitHandle.WaitOne(wait)) return;
                        }
                        // A frame of its own each time: the writer copies it later, on its own thread.
                        var image = new IplImage(size, IplDepth.U8, channels);
                        image.Set(Scalar.All(id * 7 % 256));
                        var chunk = new VideoChunkData(id, 1000000L + id * 20000000L, 19000.5, 0);
                        observer.OnNext(Timestamped.Create(new VideoDataFrame(image, chunk), start + id / 50.0));
                    }
                    observer.OnCompleted();
                })
                { IsBackground = true, Name = "SyntheticFrames" };
                thread.Start();
                return Disposable.Create(cancel.Cancel);
            });
        }
    }
}
