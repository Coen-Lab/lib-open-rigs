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

        /// <summary>The settings as text, for the Zapit-Main.Setup metric's readers.</summary>
        public override string ToString() => Restore ? "restore" : $"exposure {ExposureUs} us, gain {GainDb} dB";
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
    /// Every frame is 8-bit (ZapitFrameDepth): a Mono12 .pfs's frames are its raw counts saturating at 255, a grid run's
    /// over 16 as Mono8; a Mono8 .pfs's frames pass through.
    /// </summary>
    [Description("Grabs the Zapit camera as PylonCapture does (serial number, session .pfs), each frame 8-bit (Mono12: raw counts saturating at 255, a grid run's over 16; Mono8 as it comes). Input: ZapitCameraSettings from Zapit's setup, each applied at once (exposure, gain, auto off), or the camera's own put back (Restore).")]
    public class ZapitPylonCapture : PylonCapture
    {
        readonly object cameraLock = new object();
        CameraExposure? exposure;
        volatile bool gridApplied;   // a grid run's exposure is on the camera: Mono12 frames go to Mono8's scale

        /// <summary>Loads the .pfs as PylonCapture does and keeps the opened camera for the settings.</summary>
        protected override void Configure(Camera camera)
        {
            base.Configure(camera);
            lock (cameraLock) exposure = new CameraExposure(new BaslerFeatures(camera.Parameters));
        }

        /// <summary>The camera's frames as PylonCapture grabs them, each made 8-bit (ZapitFrameDepth).</summary>
        public override IObservable<PylonDataFrame> Generate() => base.Generate().Select(frame =>
        {
            if (frame.Image == null || frame.Image.Depth == OpenCV.Net.IplDepth.U8) return frame;
            var grid = gridApplied;
            var top = grid ? 0 : ZapitFrameDepth.StretchTop(frame.Image);
            return new PylonDataFrame(ZapitFrameDepth.ToEightBit(frame.Image, grid, top), frame.GrabResult);
        });

        /// <summary>The camera's frames, with each of <paramref name="settings"/> applied to it while it grabs.</summary>
        public IObservable<PylonDataFrame> Generate(IObservable<ZapitCameraSettings> settings) => Observable.Create<PylonDataFrame>(observer =>
            new CompositeDisposable(
                Generate().Finally(() => { lock (cameraLock) { exposure = null; gridApplied = false; } }).Subscribe(observer),
                settings.Subscribe(s =>
                {
                    lock (cameraLock)
                    {
                        try
                        {
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
