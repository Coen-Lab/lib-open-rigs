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

        /// <summary>With SetCrop, the request's number (Zapit-Main.Crop #n), echoed in every answer; 0 for a request with no valid number.</summary>
        public int CropRequest { get; set; }

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
    /// One Zapit camera frame, three ways (a grid run's frames at Mono8's scale are nearly black in the window): View, what
    /// the window streams, the session's stretched picture cut to the crop (ZapitFrameDepth.Crop), which is for viewing only,
    /// so it can change before or after Go; Image, what the mp4 records, the same picture whole;
    /// Still, what Zapit's setup saves (stills, a grid run's point and background frames), the whole frame, the stretched
    /// picture too except during a grid run, when it is Mono8's scale (counts / 16), which the spot detection's 90 %-of-peak
    /// threshold and 500 px size limit were tuned on. A Mono8 camera's frame is all three, as it comes (View cropped).
    /// </summary>
    public class ZapitFrame
    {
        /// <summary>The frame the window streams: a Mono12 frame stretched, a Mono8 frame as it comes; cut to the crop.
        /// The live view takes it before Image (LiveViewPlan.ImagePath prefers a member named View).</summary>
        public OpenCV.Net.IplImage? View { get; set; }

        /// <summary>The frame the mp4 records: as View, but whole, never cropped.</summary>
        public OpenCV.Net.IplImage? Image { get; set; }

        /// <summary>The whole frame Zapit's setup saves: a Mono12 frame stretched, or at Mono8's scale during a grid run; a Mono8 frame as it comes.</summary>
        public OpenCV.Net.IplImage? Still { get; set; }

        /// <summary>The grab's ID (PylonCapture's GrabResult.ID), as the recording's frame log keeps it.</summary>
        public long FrameID { get; set; }

        /// <summary>The camera's timestamp of the grab (GrabResult.Timestamp).</summary>
        public long Timestamp { get; set; }

        /// <summary>The camera's answers to Zapit-Main.Crop requests, in order, on the first frame cut to them (null on every other frame):
        /// Cropped #n x,y,w,h, Full Frame #n, or Refused #n with the reason when the crop does not fit this frame (the camera's
        /// size is known only from a frame). A refused crop is dropped, the whole frame shown, never a clamped one;
        /// ZapitCropReport sends them in order to the metric Zapit-Main.Crop.</summary>
        public ZapitSetupStep[]? CropReports { get; set; }
    }

    /// <summary>The errors the Zapit camera's settings raise, said to the operator: the camera, its serial and what to do, with the
    /// camera's own words and exception kept inside.</summary>
    internal static class ZapitCameraRefusal
    {
        /// <summary>The error's own message as a sentence, so the next sentence does not run on from it (GenApi's carry no full stop).</summary>
        static string Said(Exception error)
        {
            var message = error.Message.TrimEnd();
            return message.EndsWith(".", StringComparison.Ordinal) ? message : message + ".";
        }

        /// <summary>The camera could not be opened (not found, or held by another program): any error before Configure, the
        /// .pfs being loaded only there (ZapitCameraOpening).</summary>
        public static InvalidOperationException Open(string? serialNumber, Exception error) => new InvalidOperationException(
            $"The Zapit camera (serial {serialNumber}) could not be opened: {Said(error)} " +
            "Check that it is plugged in and not held by another program (MATLAB Zapit, Pylon Viewer), or untick Zapit-Bonsai for this session.", error);

        /// <summary><paramref name="frames"/> with an error raised before the camera was opened (<paramref name="opened"/>
        /// still false) said as <see cref="Open"/>; later errors as they come.</summary>
        public static IObservable<T> NameOpenErrors<T>(IObservable<T> frames, Func<bool> opened, string? serialNumber) =>
            frames.Catch<T, Exception>(error => Observable.Throw<T>(opened() ? error : Open(serialNumber, error)));

        /// <summary>Throws, naming the file and where it should be, when <paramref name="parameterFile"/> is named but missing
        /// or cannot be read: not the camera refusing its values.</summary>
        public static void CheckFile(string? serialNumber, string? parameterFile)
        {
            if (string.IsNullOrEmpty(parameterFile)) return;
            var path = System.IO.Path.GetFullPath(parameterFile);
            if (!System.IO.File.Exists(path))
                throw new InvalidOperationException(
                    $"The settings file of the Zapit camera (serial {serialNumber}) is missing: it should be at {path} (rig.yml's zapit.camera.sessionPfsPath). " +
                    "Save the .pfs there from this camera, or untick Zapit-Bonsai for this session.");
            try { using (System.IO.File.OpenRead(path)) { } }
            catch (Exception error) when (error is System.IO.IOException || error is UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"The settings file {path} of the Zapit camera (serial {serialNumber}) cannot be read: {Said(error)} " +
                    "Close the program holding it or fix its permissions, or untick Zapit-Bonsai for this session.", error);
            }
        }

        /// <summary>The opened camera refused the .pfs's values as it was loaded (ZapitCameraOpening.Configure).</summary>
        public static InvalidOperationException Settings(string? serialNumber, string? parameterFile, Exception error) => new InvalidOperationException(
            $"The Zapit camera (serial {serialNumber}) refused its settings file {parameterFile}: {Said(error)} " +
            "The camera's settings do not match the file. A crop left on the camera by another program (such as MATLAB Zapit) limits its width: " +
            "power-cycle or reset the camera, or save the .pfs again from this camera.", error);

        /// <summary>The camera did not take the grid run's exposure and gain, or (<paramref name="restore"/>) did not take back its own.</summary>
        public static InvalidOperationException Exposure(string? serialNumber, Exception error, bool restore = false) => new InvalidOperationException(
            $"The Zapit camera (serial {serialNumber}) did not take " + (restore ? "back its own exposure and gain after the grid" : "the grid's exposure and gain") + $": {Said(error)} Run the grid again; if it repeats, power-cycle the camera.", error);
    }

    /// <summary>
    /// Opens the Zapit camera so each error is said for its cause. Bonsai.Pylon's PylonCapture.Generate (0.4.0) opens the camera,
    /// loads its ParameterFile, then calls Configure, so a .pfs the camera refuses would fail before Configure, looking like a
    /// camera that cannot be opened. So the file is checked before the camera is touched, the base is handed no ParameterFile
    /// (put back once it has read it), and the .pfs is loaded in Configure: an error before Configure is the camera's own.
    /// </summary>
    internal sealed class ZapitCameraOpening
    {
        readonly Func<string?> serialNumber;
        readonly Func<string?> getParameterFile;
        readonly Action<string?> setParameterFile;
        string? parameterFile;   // the .pfs this opening loads in Configure
        volatile bool opened;   // Configure reached: the camera was found and opened

        public ZapitCameraOpening(Func<string?> serialNumber, Func<string?> getParameterFile, Action<string?> setParameterFile)
        {
            this.serialNumber = serialNumber;
            this.getParameterFile = getParameterFile;
            this.setParameterFile = setParameterFile;
        }

        /// <summary><paramref name="grab"/> (PylonCapture's Generate) with no ParameterFile, after the file is checked; its errors
        /// before Configure said as the camera not opening.</summary>
        public IObservable<T> Open<T>(Func<IObservable<T>> grab) => Observable.Defer(() =>
        {
            var file = getParameterFile();
            ZapitCameraRefusal.CheckFile(serialNumber(), file);
            parameterFile = file;
            opened = false;
            setParameterFile(null);
            return ZapitCameraRefusal.NameOpenErrors(grab(), () => opened, serialNumber()).Finally(PutBack);
        });

        /// <summary>The camera is open: puts ParameterFile back and loads the .pfs with <paramref name="load"/>, a refusal said as one.</summary>
        public void Configure(Action<string> load)
        {
            opened = true;
            PutBack();
            var file = parameterFile;
            if (string.IsNullOrEmpty(file)) return;
            try { load(file!); }
            catch (Exception error) { throw ZapitCameraRefusal.Settings(serialNumber(), file, error); }
        }

        void PutBack()
        {
            if (getParameterFile() == null) setParameterFile(parameterFile);
        }
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
    /// setup phase sets for a grid run's frames while it grabs, and puts back after. PylonCapture has no exposure or gain
    /// property; its Configure hands over the opened camera, kept here, and loads the .pfs (ZapitCameraOpening).
    /// Every frame is 8-bit (ZapitFrameDepth), given three ways (ZapitFrame): View, the streamed picture, a Mono12 frame
    /// stretched to its StretchTop always, cut to the crop the setup sent (SetCrop; the whole frame until one comes);
    /// Image, the recorded picture, the same whole; Still, the whole frame Zapit's setup saves, stretched too except during
    /// a grid run, when it is Mono8's scale (counts over 16) for the spot detection. A Mono8 .pfs's frames pass through
    /// (View cropped).
    /// </summary>
    [Description("Grabs the Zapit camera as PylonCapture does (serial number, session .pfs), each frame 8-bit (Mono12: stretched to the frame's StretchTop; Mono8 as it comes) as Image, cut to the crop, and whole as Still (a grid run's Mono12 frames at Mono8's scale, counts over 16, there). Input: ZapitCameraSettings from Zapit's setup, each applied at once (exposure, gain, auto off), the camera's own put back (Restore), or the shown frame's crop (SetCrop).")]
    public class ZapitPylonCapture : PylonCapture
    {
        readonly object cameraLock = new object();
        CameraExposure? exposure;
        volatile bool gridApplied;   // a grid run's exposure is on the camera: Mono12 stills go to Mono8's scale
        readonly ZapitCropState crop = new ZapitCropState();   // the shown frame's crop (empty: the whole frame) and its pending answer
        readonly ZapitCameraOpening opening;

        public ZapitPylonCapture() => opening = new ZapitCameraOpening(() => SerialNumber, () => ParameterFile, file => ParameterFile = file);

        /// <summary>Loads the .pfs into the opened camera (the base was handed none: ZapitCameraOpening) and keeps the camera for the settings.</summary>
        protected override void Configure(Camera camera)
        {
            opening.Configure(file => camera.Parameters.Load(file, ParameterPath.CameraDevice));
            base.Configure(camera);
            lock (cameraLock) exposure = new CameraExposure(new BaslerFeatures(camera.Parameters));
        }

        /// <summary>The camera's frames, with each of <paramref name="settings"/> applied to it while it grabs: each frame
        /// shown cropped, a Mono12 frame stretched (Image), and whole for Zapit's setup (Still).</summary>
        public IObservable<ZapitFrame> Generate(IObservable<ZapitCameraSettings> settings) => Observable.Create<ZapitFrame>(observer =>
            new CompositeDisposable(
                opening.Open(() => base.Generate())
                    .Select(frame => ZapitFrameDepth.Frame(frame.Image, gridApplied, crop, frame.GrabResult?.ID ?? 0, frame.GrabResult?.Timestamp ?? 0))
                    .Finally(() => { lock (cameraLock) { exposure = null; gridApplied = false; } crop.Reset(); }).Subscribe(observer),
                settings.Subscribe(s =>
                {
                    lock (cameraLock)
                    {
                        try
                        {
                            if (crop.Apply(s)) return;   // the shown frame only: from the next frame, whatever the camera does
                            if (exposure == null) throw new InvalidOperationException("The Zapit camera is not open, so the grid's exposure cannot be set.");
                            try { exposure.Apply(s); }
                            catch (Exception error) { throw ZapitCameraRefusal.Exposure(SerialNumber, error, s.Restore); }
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
