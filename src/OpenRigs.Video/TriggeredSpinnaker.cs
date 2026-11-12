using Bonsai;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using Bonsai.Harp;
using SpinnakerNET;

namespace OpenRigs.Video
{
    [Description("Configures and initializes a Spinnaker camera for triggered acquisition.")]
    public class TriggeredSpinnaker : Bonsai.Spinnaker.SpinnakerCapture
    {
        public TriggeredSpinnaker()
        {
            ExposureTime = 1e6 / 50 - 1000;
            Binning = 1;
            PixelFormat = PixelFormatEnums.Mono8.ToString();
        }

        // SpinnakerCapture opens and configures the camera on a MemberwiseClone of this node, while
        // a PropertyMapping sets this node itself. Exposure, gain and the open camera live in one
        // object both share, so a new exposure or gain while ready is written to the camera
        // straight away, and one set while the camera opens is the one it opens with.
        class LiveSettings
        {
            public double ExposureTime;
            public double Gain;
            // Null until Configure, and when the camera is closed.
            public IManagedCamera Camera;
        }

        readonly LiveSettings live = new LiveSettings();

        [Description("The duration of each individual exposure, in microseconds. In general, this should be 1 / frameRate - 1 millisecond to prepare for next trigger. Taken live by an open camera.")]
        public double ExposureTime
        {
            get { return live.ExposureTime; }
            set
            {
                lock (live)
                {
                    live.ExposureTime = value;
                    if (IsOpen()) live.Camera.ExposureTime.Value = value;
                }
            }
        }

        [Description("The gain of the sensor. Taken live by an open camera.")]
        public double Gain
        {
            get { return live.Gain; }
            set
            {
                lock (live)
                {
                    live.Gain = value;
                    if (IsOpen()) live.Camera.Gain.Value = value;
                }
            }
        }

        [Description("The size of the binning area of the sensor, e.g. a binning size of 2 specifies a 2x2 binning region. Taken only when the camera opens.")]
        public int Binning { get; set; }

        // A string, not PixelFormatEnums, so rig.yml's pixelFormat maps onto it; the camera refuses an
        // unknown name when it opens.
        [Description("The pixel format the camera sends, a Spinnaker PixelFormat name LogVideo can write, Mono8 (rig.yml's pixelFormat; blank is Mono8). Set every time the camera opens, so a format stored on the camera is never used. Taken only when the camera opens.")]
        public string PixelFormat { get; set; }

        bool IsOpen()
        {
            return live.Camera != null && live.Camera.IsInitialized();
        }

        protected override void Configure(IManagedCamera camera)
        {
            lock (live)
            {
                ConfigureCamera(camera);
                live.Camera = camera;
            }
            base.Configure(camera);
        }

        void ConfigureCamera(IManagedCamera camera)
        {
            try { camera.AcquisitionStop.Execute(); }
            catch { }
            // Before binning and the region: a format change can change their limits. Blank means Mono8,
            // as coen-qc-preflight-rig reads it (audit, 11 Nov 2026).
            camera.PixelFormat.Value = string.IsNullOrWhiteSpace(PixelFormat) ? "Mono8" : PixelFormat.Trim();
            camera.BinningSelector.Value = BinningSelectorEnums.All.ToString();
            camera.BinningHorizontalMode.Value = BinningHorizontalModeEnums.Sum.ToString();
            camera.BinningVerticalMode.Value = BinningVerticalModeEnums.Sum.ToString();
            camera.BinningHorizontal.Value = Binning;
            camera.BinningVertical.Value = Binning;
            // Binning does not resize the region; reset it to the full sensor (offsets first)
            camera.OffsetX.Value = 0;
            camera.OffsetY.Value = 0;
            camera.Width.Value = camera.Width.Max;
            camera.Height.Value = camera.Height.Max;
            camera.AcquisitionFrameRateEnable.Value = false;
            camera.TriggerMode.Value = TriggerModeEnums.On.ToString();
            camera.TriggerSelector.Value = TriggerSelectorEnums.FrameStart.ToString();
            camera.TriggerSource.Value = TriggerSourceEnums.Line0.ToString();
            camera.TriggerOverlap.Value = TriggerOverlapEnums.ReadOut.ToString();
            camera.TriggerActivation.Value = TriggerActivationEnums.RisingEdge.ToString();
            camera.ExposureAuto.Value = ExposureAutoEnums.Off.ToString();
            camera.ExposureMode.Value = ExposureModeEnums.Timed.ToString();
            camera.ExposureTime.Value = ExposureTime;
            camera.DeviceLinkThroughputLimit.Value = camera.DeviceLinkThroughputLimit.Max;
            camera.GainAuto.Value = GainAutoEnums.Off.ToString();
            camera.Gain.Value = Gain;
        }

        public IObservable<Timestamped<VideoDataFrame>> Generate<TPayload>(IObservable<Timestamped<TPayload>> source)
        {
            var frames = Generate().Finally(() => { lock (live) live.Camera = null; });
            return frames
                // In the camera's callback, before Spinnaker releases the chunk data: the exposure
                // and gain each frame was taken with, as the camera reports them.
                .Select(frame => new VideoDataFrame(frame.Image, new VideoChunkData(
                    frame.ChunkData.FrameID, frame.ChunkData.Timestamp, frame.ChunkData.ExposureTime, frame.ChunkData.Gain)))
                .FillGaps(frame => frame.ChunkData.FrameID, (previous, current) => (int)(current - previous - 1))
                .Zip(source, (frame, payload) => Timestamped.Create(frame, payload.Seconds))
                .Where(timestamped => timestamped.Value != null);
        }
    }
}
