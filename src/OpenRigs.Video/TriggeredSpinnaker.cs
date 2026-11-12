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

        [TypeConverter(typeof(PixelFormatConverter))]
        [Description("The pixel format the camera is set to before acquisition starts. Leave empty to keep the format the camera is already in. The default Mono8 matches the 8-bit mono video writers.")]
        public string PixelFormat { get; set; }

        [Description("The duration of each individual exposure, in microseconds. In general, this should be 1 / frameRate - 1 millisecond to prepare for next trigger.")]
        public double ExposureTime { get; set; }

        [Description("The gain of the sensor.")]
        public double Gain { get; set; }

        [Description("The size of the binning area of the sensor, e.g. a binning size of 2 specifies a 2x2 binning region.")]
        public int Binning { get; set; }

        protected override void Configure(IManagedCamera camera)
        {
            try { camera.AcquisitionStop.Execute(); }
            catch { }
            // The camera keeps the format the last program left (SpinView, Zapit) until a power cycle.
            // Set it before binning because a format change can reset the size limits.
            ConfigurePixelFormat(camera);
            camera.BinningSelector.Value = BinningSelectorEnums.All.ToString();
            camera.BinningHorizontalMode.Value = BinningHorizontalModeEnums.Sum.ToString();
            camera.BinningVerticalMode.Value = BinningVerticalModeEnums.Sum.ToString();
            camera.BinningHorizontal.Value = Binning;
            camera.BinningVertical.Value = Binning;
            // Binning does not resize the region. A camera left cropped by a stored user set would
            // otherwise record that crop. The offsets go first because the maximums depend on them.
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
            base.Configure(camera);
        }

        void ConfigurePixelFormat(IManagedCamera camera)
        {
            var pixelFormat = PixelFormat;
            if (string.IsNullOrEmpty(pixelFormat) || camera.PixelFormat.Value.String == pixelFormat)
            {
                return;
            }

            if (!camera.PixelFormat.IsWritable)
            {
                throw new InvalidOperationException(string.Format(
                    "Camera {0} is in pixel format {1} and its PixelFormat node is not writable. It cannot be set to {2}.",
                    camera.DeviceSerialNumber.Value, camera.PixelFormat.Value.String, pixelFormat));
            }

            try { camera.PixelFormat.Value = pixelFormat; }
            catch (SpinnakerException ex)
            {
                throw new InvalidOperationException(string.Format(
                    "Camera {0} could not be set to pixel format {1}.",
                    camera.DeviceSerialNumber.Value, pixelFormat), ex);
            }
        }

        class PixelFormatConverter : StringConverter
        {
            public override bool GetStandardValuesSupported(ITypeDescriptorContext context)
            {
                return true;
            }

            public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
            {
                return new StandardValuesCollection(Enum.GetNames(typeof(PixelFormatEnums)));
            }
        }

        public IObservable<Timestamped<VideoDataFrame>> Generate<TPayload>(IObservable<Timestamped<TPayload>> source)
        {
            return Generate(source, Observable.Return(System.Reactive.Unit.Default));
        }

        // The camera subscribes to start once it is initialized and configured, then begins acquisition
        // as soon as start emits. A start sequence that reports readiness lets the workflow hold the
        // trigger output until every camera has reached that point.
        public IObservable<Timestamped<VideoDataFrame>> Generate<TPayload, TStart>(IObservable<Timestamped<TPayload>> source, IObservable<TStart> start)
        {
            var frames = base.Generate(start);
            // Copy the chunk fields while still inside the camera callback. Spinnaker releases the
            // native chunk data as soon as OnNext returns, and a frame waiting in the Zip below
            // would otherwise read freed memory.
            return frames
                .Select(frame => new VideoDataFrame(frame.Image, new VideoChunkData(
                    frame.ChunkData.FrameID,
                    frame.ChunkData.Timestamp,
                    frame.ChunkData.ExposureTime,
                    frame.ChunkData.Gain)))
                .FillGaps(frame => frame.ChunkData.FrameID, (previous, current) => (int)(current - previous - 1))
                .Zip(source, (frame, payload) => Timestamped.Create(frame, payload.Seconds))
                .Where(timestamped => timestamped.Value != null);
        }
    }
}
