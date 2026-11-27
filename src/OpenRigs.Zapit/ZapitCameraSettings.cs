using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Basler.Pylon;
using Bonsai;
using Bonsai.Pylon;

namespace OpenRigs.Zapit
{
    /// <summary>The Zapit camera's exposure and gain for a grid run (auto exposure and auto gain off, DigitalShift 0), or the camera's
    /// own settings put back once the run ends.</summary>
    public class ZapitCameraSettings
    {
        /// <summary>The exposure for the grid's frames, in microseconds.</summary>
        public double ExposureUs { get; set; }

        /// <summary>The gain for the grid's frames, in dB.</summary>
        public double GainDb { get; set; }

        /// <summary>True to put back the settings the camera had before the grid run (its session .pfs), not apply these.</summary>
        public bool Restore { get; set; }

        /// <summary>True for a crop of the shown frame (CropX, CropY, CropWidth, CropHeight), not an exposure: the window's
        /// and the mp4's picture only; the camera itself and the stills are left whole. A CropWidth of 0 is the whole frame.</summary>
        public bool SetCrop { get; set; }

        /// <summary>With SetCrop, why the request was no crop (not x,y,w,h or full): the camera keeps its crop and answers Refused
        /// from its next frame, saying which crop is in force. Null for a crop.</summary>
        public string? RejectReason { get; set; }

        /// <summary>The crop's left edge, in pixels of the camera's frame.</summary>
        public int CropX { get; set; }

        /// <summary>The crop's top edge, in pixels of the camera's frame.</summary>
        public int CropY { get; set; }

        /// <summary>The crop's width in pixels; 0 for the whole frame.</summary>
        public int CropWidth { get; set; }

        /// <summary>The crop's height in pixels; 0 for the whole frame.</summary>
        public int CropHeight { get; set; }

        /// <summary>The settings as text, for the Zapit-Main.Setup metric's readers.</summary>
        public override string ToString() => SetCrop
            ? (RejectReason != null ? "crop refused" : CropWidth > 0 ? $"crop {CropX},{CropY},{CropWidth},{CropHeight}" : "crop full frame")
            : Restore ? "restore" : $"exposure {ExposureUs} us, gain {GainDb} dB";
    }

    /// <summary>
    /// One Zapit camera frame, two ways (a grid run's frames were nearly black in the window, 27 Nov 2026): Image, what
    /// the window streams and the mp4 records, always the session's stretched picture, cut to the crop (ZapitFrameDepth.Crop);
    /// Still, what Zapit's setup saves (stills, a grid run's point and background frames), the whole frame, the stretched
    /// picture too except during a grid run, when it is Mono8's scale (counts / 16), which the spot detection's 90 %-of-peak
    /// threshold and 500 px size limit were tuned on. A Mono8 camera's frame is both, as it comes (Image cropped).
    /// </summary>
    public class ZapitFrame
    {
        /// <summary>The shown frame: stretched, cropped; streamed and recorded.</summary>
        public OpenCV.Net.IplImage? Image { get; set; }

        /// <summary>The whole frame Zapit's setup saves: stretched, or Mono8's scale during a grid run.</summary>
        public OpenCV.Net.IplImage? Still { get; set; }

        /// <summary>The grab's ID (PylonCapture's GrabResult.ID), as the recording's frame log keeps it.</summary>
        public long FrameID { get; set; }

        /// <summary>The camera's timestamp of the grab (GrabResult.Timestamp).</summary>
        public long Timestamp { get; set; }

        /// <summary>The camera's answer to a Zapit-Main.Crop, on the first frame cut to it (null on every other frame):
        /// Cropped x,y,w,h, Full Frame, or Refused with the reason when the crop does not fit this frame (the camera's
        /// size is known only from a frame). A refused crop is dropped, the whole frame shown, never a clamped one;
        /// ZapitCropReport sends it to the metric Zapit-Main.Crop.</summary>
        public ZapitSetupStep? CropReport { get; set; }
    }

    /// <summary>The camera features a grid run sets, by GenICam name: a Basler camera's, or a test's.</summary>
    internal interface ICameraFeatures
    {
        bool Has(string name);
        string GetEnum(string name);
        void SetEnum(string name, string value);
        double GetFloat(string name);
        void SetFloat(string name, double value);
        long GetInteger(string name);
        void SetInteger(string name, long value);
    }

    /// <summary>Applies a grid run's exposure and gain, auto exposure and auto gain off and DigitalShift 0 (so a session .pfs
    /// DigitalShift does not move the grid's frames off the camera's plain 8-bit range the spot-size threshold assumes), and
    /// puts back what the camera had.</summary>
    internal sealed class CameraExposure
    {
        readonly ICameraFeatures camera;
        Dictionary<string, object>? saved;

        public CameraExposure(ICameraFeatures camera) => this.camera = camera;

        /// <summary>True while a grid run's exposure and gain are on the camera (applied, not yet put back).</summary>
        public bool GridApplied => saved != null;

        /// <summary>The first of the names the camera has, writable or not: an auto mode left on makes the feature read-only
        /// (USB3 cameras name it ExposureTime, older GigE ones ExposureTimeAbs).</summary>
        string Feature(params string[] names)
        {
            foreach (var name in names) if (camera.Has(name)) return name;
            throw new InvalidOperationException("The Zapit camera has none of " + string.Join(", ", names) + ", so the grid's exposure cannot be set.");
        }

        public void Apply(ZapitCameraSettings settings)
        {
            if (settings.Restore) { Restore(); return; }
            if (saved == null)
            {
                saved = new Dictionary<string, object>();
                foreach (var auto in new[] { "ExposureAuto", "GainAuto" })
                    if (camera.Has(auto)) saved[auto] = camera.GetEnum(auto);
            }
            foreach (var auto in new[] { "ExposureAuto", "GainAuto" })
                if (camera.Has(auto)) camera.SetEnum(auto, "Off");   // an auto mode left on overrides a fixed value and makes it read-only
            var exposure = Feature("ExposureTime", "ExposureTimeAbs");
            var gain = Feature("Gain", "GainAbs");
            if (!saved.ContainsKey(exposure)) saved[exposure] = camera.GetFloat(exposure);
            if (!saved.ContainsKey(gain)) saved[gain] = camera.GetFloat(gain);
            camera.SetFloat(exposure, settings.ExposureUs);
            camera.SetFloat(gain, settings.GainDb);
            if (camera.Has("DigitalShift"))
            {
                if (!saved.ContainsKey("DigitalShift")) saved["DigitalShift"] = camera.GetInteger("DigitalShift");
                camera.SetInteger("DigitalShift", 0);
            }
        }

        void Restore()
        {
            if (saved == null) return;
            foreach (var kv in saved) if (kv.Value is double value) camera.SetFloat(kv.Key, value);
            foreach (var kv in saved) if (kv.Value is long shift) camera.SetInteger(kv.Key, shift);
            foreach (var kv in saved) if (kv.Value is string mode) camera.SetEnum(kv.Key, mode);   // the auto modes last
            saved = null;
        }
    }

    /// <summary>
    /// The Zapit camera: Bonsai.Pylon's PylonCapture (its serial number and session .pfs), whose exposure and gain the
    /// setup phase sets for a grid run's frames while it grabs, and puts back after. PylonCapture loads its .pfs only as
    /// the camera opens and has no exposure or gain property; its Configure hands over the opened camera, kept here.
    /// Every frame is 8-bit (ZapitFrameDepth), given two ways (ZapitFrame): Image, the shown and recorded picture, a Mono12
    /// frame stretched to its StretchTop always, cut to the crop the setup sent (SetCrop; the whole frame until one comes);
    /// Still, the whole frame Zapit's setup saves, stretched too except during a grid run, when it is Mono8's scale
    /// (counts over 16) for the spot detection. A Mono8 .pfs's frames pass through (Image cropped).
    /// </summary>
    [Description("Grabs the Zapit camera as PylonCapture does (serial number, session .pfs), each frame 8-bit (Mono12: stretched to the frame's StretchTop; Mono8 as it comes) as Image, cut to the crop, and whole as Still (a grid run's Mono12 frames at Mono8's scale, counts over 16, there). Input: ZapitCameraSettings from Zapit's setup, each applied at once (exposure, gain, auto off), the camera's own put back (Restore), or the shown frame's crop (SetCrop).")]
    public class ZapitPylonCapture : PylonCapture
    {
        readonly object cameraLock = new object();
        CameraExposure? exposure;
        volatile bool gridApplied;   // a grid run's exposure is on the camera: Mono12 stills go to Mono8's scale
        readonly ZapitCropState crop = new ZapitCropState();   // the shown frame's crop (empty: the whole frame) and its pending answer

        /// <summary>Loads the .pfs as PylonCapture does and keeps the opened camera for the settings.</summary>
        protected override void Configure(Camera camera)
        {
            base.Configure(camera);
            lock (cameraLock) exposure = new CameraExposure(new BaslerFeatures(camera.Parameters));
        }

        /// <summary>The camera's frames as PylonCapture grabs them, each made 8-bit and stretched (ZapitFrameDepth), whole.</summary>
        public override IObservable<PylonDataFrame> Generate() => base.Generate().Select(frame =>
        {
            if (frame.Image == null || frame.Image.Depth == OpenCV.Net.IplDepth.U8) return frame;
            return new PylonDataFrame(ZapitFrameDepth.ToEightBit(frame.Image, false, ZapitFrameDepth.StretchTop(frame.Image)), frame.GrabResult);
        });

        /// <summary>A grabbed frame as the shown image (stretched, cropped) and the still Zapit's setup saves (whole; Mono8's
        /// scale in a grid run), each null for a null frame.</summary>
        internal static void Split(OpenCV.Net.IplImage? image, bool grid, OpenCV.Net.Rect crop, out OpenCV.Net.IplImage? shown, out OpenCV.Net.IplImage? still, out string? cropRefusal)
        {
            shown = still = image;
            cropRefusal = null;
            if (image == null) return;
            if (image.Depth != OpenCV.Net.IplDepth.U8)
            {
                shown = ZapitFrameDepth.ToEightBit(image, false, ZapitFrameDepth.StretchTop(image));
                still = grid ? ZapitFrameDepth.ToEightBit(image, true, 0) : shown;
            }
            shown = ZapitFrameDepth.Crop(shown!, crop, out cropRefusal);
        }

        /// <summary>The camera's frames, with each of <paramref name="settings"/> applied to it while it grabs: each frame
        /// shown stretched and cropped (Image), and whole for Zapit's setup (Still).</summary>
        public IObservable<ZapitFrame> Generate(IObservable<ZapitCameraSettings> settings) => Observable.Create<ZapitFrame>(observer =>
            new CompositeDisposable(
                base.Generate().Select(frame =>
                {
                    var cut = crop.Current;
                    Split(frame.Image, gridApplied, cut, out var shown, out var still, out var refusal);
                    return new ZapitFrame { Image = shown, Still = still, CropReport = crop.Finish(cut, refusal), FrameID = frame.GrabResult?.ID ?? 0, Timestamp = frame.GrabResult?.Timestamp ?? 0 };
                }).Finally(() => { lock (cameraLock) { exposure = null; gridApplied = false; } crop.Reset(); }).Subscribe(observer),
                settings.Subscribe(s =>
                {
                    lock (cameraLock)
                    {
                        try
                        {
                            if (s.SetCrop)   // the shown frame only: from the next frame, whatever the camera does
                            {
                                if (s.RejectReason != null) { crop.Reject(s.RejectReason); return; }
                                crop.Set(s.CropWidth > 0 && s.CropHeight > 0 ? new OpenCV.Net.Rect(s.CropX, s.CropY, s.CropWidth, s.CropHeight) : default);
                                return;
                            }
                            if (exposure == null) throw new InvalidOperationException("The Zapit camera is not open, so the grid's exposure cannot be set.");
                            exposure.Apply(s);
                            gridApplied = exposure.GridApplied;
                        }
                        catch (Exception error) { observer.OnError(error); }   // stops the workflow naming it
                    }
                }, observer.OnError)));

        sealed class BaslerFeatures : ICameraFeatures
        {
            readonly IParameterCollection parameters;
            public BaslerFeatures(IParameterCollection parameters) => this.parameters = parameters;
            public bool Has(string name) => parameters.Contains(name);
            public string GetEnum(string name) => parameters[new EnumName(name)].GetValue();
            public void SetEnum(string name, string value) => parameters[new EnumName(name)].SetValue(value);
            public double GetFloat(string name) => parameters[new FloatName(name)].GetValue();
            public void SetFloat(string name, double value) => parameters[new FloatName(name)].SetValue(value);
            public long GetInteger(string name) => parameters[new IntegerName(name)].GetValue();
            public void SetInteger(string name, long value) => parameters[new IntegerName(name)].SetValue(value);
        }
    }
}
