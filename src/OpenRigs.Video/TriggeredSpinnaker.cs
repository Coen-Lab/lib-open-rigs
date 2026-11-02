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
        }

        // The open camera, so a new exposure or gain (a PropertyMapping while ready) is written
        // to it straight away; null until Configure, and when the camera is closed.
        readonly object gate = new object();
        IManagedCamera openCamera;
        double exposureTime;
        double gain;

        [Description("The duration of each individual exposure, in microseconds. In general, this should be 1 / frameRate - 1 millisecond to prepare for next trigger. Taken live by an open camera.")]
        public double ExposureTime
        {
            get { return exposureTime; }
            set
            {
                lock (gate)
                {
                    exposureTime = value;
                    if (IsOpen()) openCamera.ExposureTime.Value = value;
                }
            }
        }

        [Description("The gain of the sensor. Taken live by an open camera.")]
        public double Gain
        {
            get { return gain; }
            set
            {
                lock (gate)
                {
                    gain = value;
                    if (IsOpen()) openCamera.Gain.Value = value;
                }
            }
        }

        [Description("The size of the binning area of the sensor, e.g. a binning size of 2 specifies a 2x2 binning region. Taken only when the camera opens.")]
        public int Binning { get; set; }

        bool IsOpen()
        {
            return openCamera != null && openCamera.IsInitialized();
        }

        protected override void Configure(IManagedCamera camera)
        {
            lock (gate)
            {
                ConfigureCamera(camera);
                openCamera = camera;
            }
            base.Configure(camera);
        }

        void ConfigureCamera(IManagedCamera camera)
        {
            try { camera.AcquisitionStop.Execute(); }
            catch { }
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
            var frames = Generate().Finally(() => { lock (gate) openCamera = null; });
            return frames
                .Select(frame => new VideoDataFrame(frame.Image, frame.ChunkData.FrameID, frame.ChunkData.Timestamp))
                .FillGaps(frame => frame.ChunkData.FrameID, (previous, current) => (int)(current - previous - 1))
                .Zip(source, (frame, payload) => Timestamped.Create(frame, payload.Seconds))
                .Where(timestamped => timestamped.Value != null);
        }
    }
}
