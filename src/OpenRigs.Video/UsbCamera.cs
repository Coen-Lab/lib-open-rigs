using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using Bonsai;
using OpenCV.Net;
using OpenRigs.Shared;

namespace OpenRigs.Video
{
    /// <summary>
    /// A USB (DirectShow) camera plugged in for a while, such as a calibration's camera on the
    /// balance (Pip, 10 Nov 2026): found as the one camera that is not the rig's own, streamed as
    /// it comes, its exposure taken live while it streams, as TriggeredSpinnaker's is. Exposure is in
    /// microseconds, as the rig cameras' is, one setting for one meaning (Pip): DirectShow takes
    /// only powers of two of a second, so the nearest is used. No gain: a USB camera's has no unit
    /// common to its models, and exposure is what makes a display readable.
    /// </summary>
    [Description("A USB (DirectShow) camera that is not the rig's own: the first whose device path carries none of rig.yml's camera serial numbers and no FLIR or Basler vendor id (or the one DevicePath names), streamed free-running. ExposureTime is taken live, at the nearest power of two of a second.")]
    public class UsbCamera : Source<IplImage>
    {
        // Settings a PropertyMapping writes while the camera streams, applied by the thread that
        // reads it before its next frame: OpenCV's DirectShow capture is not safe across threads.
        readonly object gate = new object();
        double exposure = double.NaN;
        bool exposureChanged = true;

        /// <summary>Initializes a new instance of the <see cref="UsbCamera"/> class.</summary>
        public UsbCamera()
        {
            ConfigRoot = RigDevices.DefaultConfigRoot;
            FrameWidth = 1920;
            FrameHeight = 1080;
        }

        /// <summary>Gets or sets the root under which this machine's rig.yml lists the rig's own cameras.</summary>
        [Description("The root under which each machine has its own configuration directory, <ConfigRoot>\\<MACHINE-NAME>\\rig.yml: its cameras' serial numbers are the rig's own, never taken.")]
        public string ConfigRoot { get; set; }

        /// <summary>Gets or sets the device path of the camera to open; empty for the first that is not the rig's own.</summary>
        [Description("The DirectShow device path of the camera to open; empty for the first camera that is not the rig's own.")]
        public string DevicePath { get; set; }

        /// <summary>Gets or sets the frame width asked for; the camera takes its nearest.</summary>
        [Description("The frame width asked for when the camera opens; the camera takes its nearest size.")]
        public int FrameWidth { get; set; }

        /// <summary>Gets or sets the frame height asked for; the camera takes its nearest.</summary>
        [Description("The frame height asked for when the camera opens; the camera takes its nearest size.")]
        public int FrameHeight { get; set; }

        /// <summary>Gets or sets the exposure, in microseconds; NaN leaves the camera's automatic exposure.</summary>
        [Description("The exposure in microseconds, taken live at the nearest power of two of a second DirectShow allows (15625 is 1/64 s); NaN leaves the camera's automatic exposure.")]
        public double ExposureTime
        {
            get { lock (gate) return exposure; }
            set { lock (gate) { exposure = value; exposureChanged = true; } }
        }

        /// <summary>DirectShow's exposure step for an exposure in microseconds: log2 of seconds, rounded, -13 to 0.</summary>
        /// <param name="microseconds">The exposure, in microseconds.</param>
        /// <returns>The step, such as -6 for 15625 µs (1/64 s).</returns>
        public static int ExposureStep(double microseconds)
        {
            var step = (int)Math.Round(Math.Log(Math.Max(microseconds, 1) / 1e6, 2));
            return Math.Max(-13, Math.Min(0, step));
        }

        /// <summary>Opens the camera and streams its frames, each a copy, until unsubscribed.</summary>
        /// <returns>The camera's frames.</returns>
        public override IObservable<IplImage> Generate()
        {
            return Observable.Create<IplImage>((observer, cancel) => Task.Factory.StartNew(() =>
            {
                var index = Choose();
                using (var capture = Capture.CreateCameraCapture(DirectShowDomain + index))
                {
                    if (capture == null)
                        throw new InvalidOperationException("The calibration camera (DirectShow device " + index.ToString(CultureInfo.InvariantCulture) + ") could not be opened: is another program using it?");
                    if (FrameWidth > 0) capture.SetProperty(CaptureProperty.FrameWidth, FrameWidth);
                    if (FrameHeight > 0) capture.SetProperty(CaptureProperty.FrameHeight, FrameHeight);
                    lock (gate) exposureChanged = true;
                    var since = Stopwatch.StartNew();  // since the last frame
                    while (!cancel.IsCancellationRequested)
                    {
                        Apply(capture);
                        var frame = capture.QueryFrame();
                        if (frame == null)
                        {
                            // A long exposure or its change can miss a frame (GREEN-DOME-1's USB
                            // camera at 1 s, 10 Nov 2026): unplugged only once none comes for longer.
                            if (since.Elapsed.TotalSeconds > NoFrameSeconds())
                                throw new InvalidOperationException("The calibration camera stopped giving frames: was it unplugged?");
                            Thread.Sleep(10);  // not a busy loop if it answers null at once
                            continue;
                        }
                        since.Restart();
                        observer.OnNext(frame.Clone());
                    }
                }
            }, cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default));
        }

        const int DirectShowDomain = 700;  // OpenCV's CAP_DSHOW: an index in DirectShow's device order

        // How long with no frame before the camera is taken as gone: 5 s, or 4 exposures if longer.
        double NoFrameSeconds()
        {
            double e;
            lock (gate) e = exposure;
            return double.IsNaN(e) ? 5 : Math.Max(5, 4 * e / 1e6);
        }

        void Apply(Capture capture)
        {
            double e;
            bool setExposure;
            lock (gate)
            {
                setExposure = exposureChanged;
                e = exposure;
                exposureChanged = false;
            }
            if (setExposure && !double.IsNaN(e)) capture.SetProperty(CaptureProperty.Exposure, ExposureStep(e));
        }

        // The camera's index in DirectShow's order, which OpenCV's DirectShow capture counts in.
        int Choose()
        {
            var cameras = DirectShowCameras.List();
            if (!string.IsNullOrWhiteSpace(DevicePath))
            {
                var named = cameras.FindIndex(c => string.Equals(c.Path, DevicePath.Trim(), StringComparison.OrdinalIgnoreCase));
                if (named < 0) throw new InvalidOperationException("No camera is plugged in at " + DevicePath + ".");
                return named;
            }
            string problem;
            var devices = RigDevices.Read(RigDevices.PathFor(ConfigRoot ?? RigDevices.DefaultConfigRoot), out problem) ?? new List<RigDevice>();
            var serials = RigSerials(devices);
            for (var i = 0; i < cameras.Count; i++)
            {
                if (!IsRigOwn(cameras[i].Path, serials)) return i;
            }
            throw new InvalidOperationException("No calibration camera: plug in a USB camera that is not the rig's own" +
                (cameras.Count > 0 ? " (seen: " + string.Join(", ", cameras.Select(c => c.Name)) + ", each the rig's own or with no device path)." : "."));
        }

        // The vendor ids of cameras that belong to rigs and are never a calibration camera: FLIR
        // (Spinnaker) and Basler (BLUE-OPTO's Zapit camera, which no rig.yml lists), as app-rig-control's
        // still_camera.py has them.
        static readonly string[] RigVendors = { "vid_1e10", "vid_2676" };

        /// <summary>Whether a camera's device path is one of the rig's own: none, a rig vendor's, or carrying a rig.yml serial.</summary>
        internal static bool IsRigOwn(string path, ICollection<string> serials)
        {
            if (string.IsNullOrEmpty(path)) return true;  // a virtual camera: never the calibration's
            var lower = path.ToLowerInvariant();
            return RigVendors.Any(lower.Contains) || serials.Any(s => s.Length > 0 && lower.Contains(s));
        }

        /// <summary>rig.yml's camera serial numbers, each as written and as the 8 hex digits a USB instance id carries.</summary>
        internal static List<string> RigSerials(IEnumerable<RigDevice> devices)
        {
            var serials = new List<string>();
            foreach (var device in devices.Where(d => d.Kind == "camera"))
            {
                YamlDotNet.RepresentationModel.YamlNode node;
                if (device.Settings == null || !device.Settings.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode("serialNumber"), out node)) continue;
                var serial = (RigDevices.Scalar(node) ?? "").Trim().ToLowerInvariant();
                if (serial.Length == 0) continue;
                serials.Add(serial);
                long number;
                if (serial.All(char.IsDigit) && long.TryParse(serial, NumberStyles.None, CultureInfo.InvariantCulture, out number))
                    serials.Add(number.ToString("x8", CultureInfo.InvariantCulture));
            }
            return serials;
        }
    }

    /// <summary>A DirectShow video input device, in the system device enumerator's order.</summary>
    internal class DirectShowCamera
    {
        public string Name;
        public string Path;  // empty for a virtual camera, which has none
    }

    /// <summary>DirectShow's video input devices, in the order OpenCV's DirectShow capture counts them.</summary>
    internal static class DirectShowCameras
    {
        static readonly Guid SystemDeviceEnum = new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
        static readonly Guid VideoInputDeviceCategory = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
        static readonly Guid PropertyBagId = new Guid("55272A00-42CB-11CE-8135-00AA004BB851");

        [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ICreateDevEnum
        {
            [PreserveSig]
            int CreateClassEnumerator([In] ref Guid category, out IEnumMoniker enumerator, int flags);
        }

        [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyBag
        {
            [PreserveSig]
            int Read([MarshalAs(UnmanagedType.LPWStr)] string name, ref object value, IntPtr errorLog);

            [PreserveSig]
            int Write([MarshalAs(UnmanagedType.LPWStr)] string name, ref object value);
        }

        /// <summary>Every video input device, virtual ones included, as DirectShow lists them.</summary>
        public static List<DirectShowCamera> List()
        {
            var found = new List<DirectShowCamera>();
            var type = Type.GetTypeFromCLSID(SystemDeviceEnum);
            var devices = (ICreateDevEnum)Activator.CreateInstance(type);
            try
            {
                IEnumMoniker monikers;
                var category = VideoInputDeviceCategory;
                if (devices.CreateClassEnumerator(ref category, out monikers, 0) != 0 || monikers == null) return found;  // S_FALSE: none
                try
                {
                    var one = new IMoniker[1];
                    while (monikers.Next(1, one, IntPtr.Zero) == 0)
                    {
                        try
                        {
                            object bag;
                            var id = PropertyBagId;
                            one[0].BindToStorage(null, null, ref id, out bag);
                            found.Add(new DirectShowCamera { Name = Read((IPropertyBag)bag, "FriendlyName"), Path = Read((IPropertyBag)bag, "DevicePath") });
                            Marshal.ReleaseComObject(bag);
                        }
                        finally { Marshal.ReleaseComObject(one[0]); }
                    }
                }
                finally { Marshal.ReleaseComObject(monikers); }
            }
            finally { Marshal.ReleaseComObject(devices); }
            return found;
        }

        static string Read(IPropertyBag bag, string name)
        {
            object value = null;
            return bag.Read(name, ref value, IntPtr.Zero) == 0 ? value as string ?? "" : "";
        }
    }
}
