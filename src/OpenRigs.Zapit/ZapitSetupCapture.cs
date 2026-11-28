using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Bonsai;
using Newtonsoft.Json.Linq;
using OpenCV.Net;

namespace OpenRigs.Zapit
{
    /// <summary>One step of Zapit's setup phase: a command for ZapitDaq, or a stage to report, or both.</summary>
    public class ZapitSetupStep
    {
        /// <summary>The stage, as the Zapit-Main.Setup metric reports it: Pointing, FramesSaved, StillSaved, Loading or Refused.</summary>
        public string Stage { get; set; } = "";

        /// <summary>The run folder or still file the stage is about, or why a request was refused.</summary>
        public string Detail { get; set; } = "";

        /// <summary>The command for ZapitDaq, or null.</summary>
        public ZapitDaqCommand? Command { get; set; }

        /// <summary>The Zapit camera's exposure and gain for a grid run, or its own put back after one (Restore), or null.</summary>
        public ZapitCameraSettings? Camera { get; set; }

        /// <summary>Whether the step answers Zapit-Main.Aim, reported as the metric Zapit-Main.Aim (Aiming, Off or Refused) so the Setup metric keeps its stage.</summary>
        public bool Aim { get; set; }

        /// <summary>The step as the Zapit-Main.Setup metric: the stage, a space, the detail.</summary>
        public override string ToString() => Stage + " " + Detail;
    }

    /// <summary>
    /// The per-session scanner grid and sample still of Zapit's setup phase (docs/zapit-setup-protocol.md). A grid run is a
    /// folder in the rig's pending folder holding the agent's <c>grid.json</c> ({"volts": [[x, y], ...], "laserVolts",
    /// "skipFrames", "backgrounds", and optionally "camera": {"exposureUs", "gainDb"}}, volts from open_rigs.zapit's calibration plan):
    /// with a camera block the run's first step carries those settings for ZapitPylonCapture (auto exposure and gain off)
    /// and its last (FramesSaved, or a refusal) puts the camera's own back; per point it points the galvos with the laser
    /// on, skips skipFrames camera frames and saves the next as <c>point_NNNN.png</c>, then with the laser off saves
    /// backgrounds frames as <c>background_K.png</c> (lossless PNG, as open_rigs.zapit's fit reads them), zeroes the lines and
    /// reports FramesSaved. A run with no Pointed within 2 s of its start, or no camera frame within 2 s of Pointed (or of its
    /// last frame), is refused and zeroed, and so is one in progress at Laser Off. A still saves the next frame, laser off, as the PNG named. No maths: the volts are the agent's.
    /// </summary>
    [Combinator]
    [Description("Zapit's setup phase: each grid run (a pending folder holding grid.json) points the galvos at each grid point, saving one frame per point and laser-off backgrounds as PNGs; each still saves the next frame as the PNG named; each bank .json in pending is loaded (LoadBank); each aim (xVolts,yVolts,laserVolts) points the beam there. Inputs: grid run folders, still paths, bank paths, ZapitDaq's events, the camera's images, aims, Laser Off (ends a grid run or still). Output: ZapitDaq commands and the stages to report as Zapit-Main.Setup.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class ZapitSetupCapture
    {
        /// <summary>Gets or sets the folder every run, still and bank must be inside: the rig's pending folder.</summary>
        [Description("The folder every grid run, still and bank must be inside: RigConfigs\\<RIG>\\pending.")]
        public string PendingFolder { get; set; } = "";

        /// <summary>Gets or sets rig.yml's zapit.ceilingMw: at 0 (the laser disabled) every aim is refused.</summary>
        [Description("rig.yml's zapit.ceilingMw: at 0, the laser disabled, every aim is refused.")]
        public double CeilingMw { get; set; }

        /// <summary>Gets or sets the highest laser control volts (rig.yml's zapit.laserMinMaxControlVolts[1]): an aim above it is refused.</summary>
        [Description("The highest laser control volts (rig.yml's zapit.laserMinMaxControlVolts[1]): an aim above it is refused.")]
        public double LaserMaxVolts { get; set; }

        /// <summary>Gets or sets the laser ceiling as control volts (ceilingMw through the laser curve, passed at launch, as
        /// ZapitDaq's): an aim or grid above it is refused; the default, 0, refuses any laser volts above 0.</summary>
        [Description("The laser ceiling as control volts (ceilingMw through the laser curve, from the launch): an aim or grid above it is refused; 0 refuses any above 0 V.")]
        public double LaserCeilingVolts { get; set; }

        /// <summary>How long a grid run waits for its first Pointed, or for a camera frame after Pointed (or after the last
        /// frame), before it is refused and the lines zeroed, so a stalled camera never holds the beam on one spot and a
        /// refused Point never holds the run. Tests shorten it.</summary>
        internal TimeSpan FrameTimeout { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>How long an aim holds the beam unless a new aim comes: then the lines are zeroed (Off), so a controller
        /// that dies never leaves the laser on. The agent re-sends its aim every 1 s and turns off one unrenewed for 5 s
        /// itself; this backstop is twice that. Tests shorten it.</summary>
        internal TimeSpan AimTimeout { get; set; } = TimeSpan.FromSeconds(10);

        sealed class Run
        {
            public string Folder = "";
            public double[][] Volts = new double[0][];
            public double LaserVolts;
            public int SkipFrames;
            public int Backgrounds;
            public int Index;          // the point being saved; Volts.Length onwards the backgrounds
            public int Skip = -1;      // frames still to skip; -1 while waiting for Pointed
            public ZapitCameraSettings? Camera;   // the grid's exposure and gain, or null to keep the session's
        }

        /// <summary>The camera put back after a run that set it, else null.</summary>
        static ZapitCameraSettings? Restore(Run r) => r.Camera == null ? null : new ZapitCameraSettings { Restore = true };

        /// <summary>The most frames a grid.json may skip per point, and the most backgrounds it may save.</summary>
        internal const int MostFrames = 100;

        /// <summary>grid.json's whole-number <paramref name="field"/> from <paramref name="least"/> to <paramref name="most"/>,
        /// or <paramref name="fallback"/> when it is absent; anything else is refused naming the field.</summary>
        static int Count(JObject json, string field, int fallback, int least, int most)
        {
            var token = json[field];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            double value;
            try { value = token.Type == JTokenType.Integer || token.Type == JTokenType.Float ? token.Value<double>() : double.NaN; }
            catch (Exception error) when (error is OverflowException || error is InvalidCastException || error is FormatException) { value = double.NaN; }
            if (!(value >= least && value <= most) || value != Math.Floor(value))
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "its {0} must be a whole number from {1} to {2}, not {3}", field, least, most, token.ToString(Newtonsoft.Json.Formatting.None)));
            return (int)value;
        }

        /// <summary>The setup steps for the requests, as frames and ZapitDaq's events come. A bank request (its
        /// Settings_ZapitBank_&lt;DateTime&gt;.json) inside the pending folder becomes ZapitDaq's LoadBank (stage Loading);
        /// one outside it is refused, and one ZapitDaq answers Refused (over the ceiling) is reported Refused with its reason. Laser
        /// volts get ZapitDaq's 1e-4 V float slack. An aim ("xVolts,yVolts,laserVolts", the agent's volts through the scanner fit and laser
        /// curve) points the galvos with the laser on until the next aim, LaserOff or setup step; it is refused at ceilingMw 0,
        /// above LaserMaxVolts or LaserCeilingVolts, outside +/-10 V or during a grid run or still; a grid's laserVolts is held
        /// to the same rules. A still with no frame within FrameTimeout is refused. When the aims end (Go), or at each Laser Off
        /// (laserOffs, Zapit-Main.LaserOff), a grid run or still in progress is refused and, as for a beam still aimed, the
        /// lines are zeroed; Laser Off with no step running still sends a Zero of its own (Aim "Off Laser Off"), and after it
        /// no aim is held. An aim whose Point ZapitDaq refuses (a play armed) is reported Refused with ZapitDaq's reason. An aim
        /// not renewed within AimTimeout is turned off (Off, the lines zeroed).</summary>
        public IObservable<ZapitSetupStep> Process(IObservable<string> gridRuns, IObservable<string> stills, IObservable<string> banks,
            IObservable<ZapitDaqEvent> events, IObservable<IplImage> frames, IObservable<string> aims, IObservable<string> laserOffs) => Observable.Create<ZapitSetupStep>(observer =>
        {
            var pending = Path.GetFullPath(PendingFolder);
            var gate = new object();
            Run? run = null;
            string? still = null;
            var aiming = false;
            var loading = false;   // a LoadBank awaits ZapitDaq's BankLoaded or Refused
            var aimSent = false;   // an aim's Point awaits ZapitDaq's Pointed or Refused
            void Emit(ZapitSetupStep step) => observer.OnNext(step);
            void Refuse(string why) => Emit(new ZapitSetupStep { Stage = "Refused", Detail = why });
            var watchdog = new SerialDisposable();
            var watch = 0;
            var aimWatchdog = new SerialDisposable();
            var aimWatch = 0;
            void StopAiming()
            {
                aiming = false;
                aimWatch++;
                aimWatchdog.Disposable = Disposable.Empty;
            }
            void AimTimedOut(int id)
            {
                if (id != aimWatch || !aiming) return;
                StopAiming();
                Emit(new ZapitSetupStep
                {
                    Aim = true,
                    Stage = "Off",
                    Detail = string.Format(CultureInfo.InvariantCulture, "no aim for {0} s", AimTimeout.TotalSeconds),
                    Command = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero },
                });
            }
            string? Inside(string path)
            {
                string full;
                try { full = Path.GetFullPath(path.Trim().Trim('"')); }
                catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException) { return null; }
                return full.StartsWith(pending.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            void Watch()
            {
                var id = ++watch;
                watchdog.Disposable = Scheduler.Default.Schedule(FrameTimeout, () => Locked<int>(TimedOut)(id));
            }
            void TimedOut(int id)
            {
                if (id != watch) return;
                if (still != null)
                {
                    var png = still;
                    still = null;
                    Refuse($"no camera frame came within {FrameTimeout.TotalSeconds} s, so the still is refused: {png}");
                    return;
                }
                if (run == null) return;
                var folder = run.Folder;
                var run0 = run;
                run = null;
                Emit(new ZapitSetupStep
                {
                    Stage = "Refused",
                    Detail = run0.Skip < 0
                        ? $"ZapitDaq did not point the galvos (Pointed) within {FrameTimeout.TotalSeconds} s (it refuses a Point while Play Condition is armed), so the lines are zeroed and the run is refused: {folder}; press Laser Off and run the grid again"
                        : $"no camera frame came within {FrameTimeout.TotalSeconds} s of Pointed, so the lines are zeroed and the run is refused: {folder}",
                    Command = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero },
                    Camera = Restore(run0),
                });
            }
            string? LaserProblem(double volts)
            {
                if (volts == 0) return null;
                if (!(CeilingMw > 0)) return "rig.yml's zapit.ceilingMw is 0 (laser disabled), so the laser volts must be 0";
                // ZapitDaq's float slack, so the setup refuses nothing the DAQ would write (the launch rounds LaserCeilingVolts).
                if (!(volts >= 0 && volts <= LaserMaxVolts + ZapitDaq.FloatSlackVolts))
                    return string.Format(CultureInfo.InvariantCulture, "the laser volts must be 0 to {0} V (rig.yml's zapit.laserMinMaxControlVolts[1])", LaserMaxVolts);
                if (!(volts <= LaserCeilingVolts + ZapitDaq.FloatSlackVolts))
                    return string.Format(CultureInfo.InvariantCulture, "the laser volts must be at most the ceiling's {0} V (LaserCeilingVolts)", LaserCeilingVolts);
                return null;
            }
            ZapitDaqCommand PointAt(Run r) => r.Index < r.Volts.Length
                ? new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, GalvoXVolts = r.Volts[r.Index][0], GalvoYVolts = r.Volts[r.Index][1], LaserVolts = r.LaserVolts }
                : new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, GalvoXVolts = r.Volts[r.Volts.Length - 1][0], GalvoYVolts = r.Volts[r.Volts.Length - 1][1], LaserVolts = 0 };

            void Start(string request)
            {
                if (run != null || still != null) { Refuse("a setup step is in progress: " + (run?.Folder ?? still)); return; }
                var folder = Inside(request);
                var grid = folder == null ? null : Path.Combine(folder, "grid.json");
                if (grid == null || !File.Exists(grid)) { Refuse("no grid.json in a folder inside " + pending + ": " + request); return; }
                try
                {
                    var json = JObject.Parse(File.ReadAllText(grid));
                    var r = new Run
                    {
                        Folder = folder!,
                        Volts = json["volts"]!.Select(p => p.Select(v => (double)v).ToArray()).ToArray(),
                        LaserVolts = (double)json["laserVolts"]!,
                        SkipFrames = Count(json, "skipFrames", 3, 0, MostFrames),
                        Backgrounds = Count(json, "backgrounds", 4, 1, MostFrames),
                    };
                    if (json["camera"] is JObject camera)
                    {
                        r.Camera = new ZapitCameraSettings { ExposureUs = (double)camera["exposureUs"]!, GainDb = (double)camera["gainDb"]! };
                        if (!(r.Camera.ExposureUs >= 10 && r.Camera.ExposureUs <= 1e6) || !(r.Camera.GainDb >= 0 && r.Camera.GainDb <= 48))
                            throw new InvalidDataException("its camera exposureUs must be 10 to 1000000 and gainDb 0 to 48");
                    }
                    if (r.Volts.Length == 0 || r.Volts.Any(p => p.Length != 2 || p.Any(v => !(Math.Abs(v) <= ZapitBank.RangeVolts))) || !(Math.Abs(r.LaserVolts) <= ZapitBank.RangeVolts))
                        throw new InvalidDataException("its volts must be [x, y] pairs within +/-10 V");
                    if (LaserProblem(r.LaserVolts) is string laser) throw new InvalidDataException(laser);
                    if (Directory.EnumerateFiles(folder!, "*.png").Any()) throw new InvalidDataException("the folder already holds frames");
                    run = r;
                    StopAiming();
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is Newtonsoft.Json.JsonException || error is NullReferenceException || error is FormatException || error is ArgumentException || error is OverflowException || error is InvalidCastException)
                {
                    Refuse(grid + " is refused: " + error.Message);
                    return;
                }
                Watch();   // from the start: a Point ZapitDaq refuses never gets its Pointed
                Emit(new ZapitSetupStep { Stage = "Pointing", Detail = run.Folder, Command = PointAt(run), Camera = run.Camera });
            }

            void Still(string request)
            {
                if (run != null || still != null) { Refuse("a setup step is in progress: " + (run?.Folder ?? still)); return; }
                var path = Inside(request);
                if (path == null || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || File.Exists(path) || !Directory.Exists(Path.GetDirectoryName(path)))
                {
                    Refuse("a still is a new .png in an existing folder inside " + pending + ": " + request);
                    return;
                }
                still = path;
                StopAiming();
                Watch();
                Emit(new ZapitSetupStep { Stage = "Pointing", Detail = still, Command = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero } });
            }

            void Bank(string request)
            {
                var path = request.Trim().Trim('"');
                if (Inside(path) == null || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    Refuse("a bank is a .json inside " + pending + ": " + request);
                    return;
                }
                loading = true;
                Emit(new ZapitSetupStep { Stage = "Loading", Detail = path, Command = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.LoadBank, Path = path } });
            }

            void Aim(string request)
            {
                void No(string why) => Emit(new ZapitSetupStep { Aim = true, Stage = "Refused", Detail = why });
                if (!(CeilingMw > 0)) { No("rig.yml's zapit.ceilingMw is 0 (laser disabled), so there is no beam to aim"); return; }
                if (run != null || still != null) { No("a setup step is in progress: " + (run?.Folder ?? still)); return; }
                var parts = request.Trim().Trim('"').Split(',');
                var volts = new double[3];
                if (parts.Length != 3 || Enumerable.Range(0, 3).Any(i => !double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out volts[i])))
                {
                    No("an aim is xVolts,yVolts,laserVolts: " + request);
                    return;
                }
                if (!(Math.Abs(volts[0]) <= ZapitBank.RangeVolts) || !(Math.Abs(volts[1]) <= ZapitBank.RangeVolts)) { No("the galvo volts must be within +/-10 V: " + request); return; }
                if (LaserProblem(volts[2]) is string laser) { No(laser + ": " + request); return; }
                aiming = true;
                aimSent = true;
                var id = ++aimWatch;   // the aim watchdog: a new aim restarts it
                aimWatchdog.Disposable = Scheduler.Default.Schedule(AimTimeout, () => Locked<int>(AimTimedOut)(id));
                Emit(new ZapitSetupStep
                {
                    Aim = true,
                    Stage = "Aiming",
                    Detail = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", volts[0], volts[1], volts[2]),
                    Command = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, GalvoXVolts = volts[0], GalvoYVolts = volts[1], LaserVolts = volts[2] },
                });
            }

            // Ends a grid run or still in progress, refused, the lines zeroed and the camera's own settings put back.
            void EndStep(string by)
            {
                if (run == null && still == null) return;
                var ended = run;
                var what = run?.Folder ?? still;
                run = null;
                still = null;
                watchdog.Disposable = Disposable.Empty;
                Emit(new ZapitSetupStep
                {
                    Stage = "Refused",
                    Detail = by + " came during a setup step: the lines are zeroed and nothing more is saved for " + what,
                    Command = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero },
                    Camera = ended == null ? null : Restore(ended),
                });
            }

            // Laser Off: the beam stays off, so a grid run does not point it again at its next point, nor an aim stay on.
            // Always with a Zero of this node's own, sent under the gate: the workflow's direct Zero can reach ZapitDaq
            // before a Point this node was sending at that moment (a grid's next point, an aim), and this one comes after it.
            void LaserOff(string request)
            {
                var stepping = run != null || still != null;
                EndStep("Laser Off");   // its Refused step carries the Zero
                StopAiming();
                if (!stepping) Emit(new ZapitSetupStep { Aim = true, Stage = "Off", Detail = "Laser Off", Command = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero } });
            }

            void AimsEnded()
            {
                lock (gate)
                {
                    EndStep("Go");
                    if (!aiming) return;
                    StopAiming();
                    Emit(new ZapitSetupStep { Aim = true, Stage = "Off", Detail = "Go", Command = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero } });
                }
            }

            void OnEvent(ZapitDaqEvent e)
            {
                if (loading && (e.Kind == ZapitDaqEventKind.BankLoaded || e.Kind == ZapitDaqEventKind.Refused))
                {
                    loading = false;
                    if (e.Kind == ZapitDaqEventKind.Refused) Refuse(e.Detail ?? "ZapitDaq refused the bank");   // over the ceiling: the session runs on
                    return;
                }
                if (aimSent && (e.Kind == ZapitDaqEventKind.Pointed || e.Kind == ZapitDaqEventKind.Refused))
                {
                    aimSent = false;
                    if (e.Kind == ZapitDaqEventKind.Refused && aiming)   // a play armed: no beam, so the metric does not read Aiming
                    {
                        StopAiming();
                        Emit(new ZapitSetupStep { Aim = true, Stage = "Refused", Detail = e.Detail ?? "ZapitDaq refused the aim" });
                    }
                    return;
                }
                if (run != null && e.Kind == ZapitDaqEventKind.Pointed && run.Skip < 0)
                {
                    run.Skip = run.SkipFrames;
                    Watch();
                }
            }

            void OnFrame(IplImage image)
            {
                if (still != null)
                {
                    CV.SaveImage(still, image);
                    watchdog.Disposable = Disposable.Empty;
                    var saved = still;
                    still = null;
                    Emit(new ZapitSetupStep { Stage = "StillSaved", Detail = saved });
                    return;
                }
                if (run == null || run.Skip < 0) return;
                Watch();   // a frame came: the next must come within FrameTimeout too
                if (run.Skip-- > 0) return;
                var background = run.Index - run.Volts.Length;
                var name = background < 0 ? string.Format(CultureInfo.InvariantCulture, "point_{0:0000}.png", run.Index) : "background_" + background + ".png";
                CV.SaveImage(Path.Combine(run.Folder, name), image);
                run.Index++;
                if (run.Index < run.Volts.Length + 1)
                {
                    run.Skip = -1;   // wait for the next Pointed
                    Emit(new ZapitSetupStep { Stage = "Pointing", Detail = run.Folder, Command = PointAt(run) });
                }
                else if (run.Index < run.Volts.Length + run.Backgrounds)
                {
                    run.Skip = 0;    // backgrounds: one frame after another, the laser already off
                }
                else
                {
                    var done = run;
                    run = null;
                    watchdog.Disposable = Disposable.Empty;
                    Emit(new ZapitSetupStep { Stage = "FramesSaved", Detail = done.Folder, Command = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero }, Camera = Restore(done) });
                }
            }

            Action<T> Locked<T>(Action<T> action) => value =>
            {
                lock (gate)
                {
                    try { action(value); }
                    catch (Exception error) { observer.OnError(error); }
                }
            };
            return new CompositeDisposable(
                watchdog,
                aimWatchdog,
                events.Subscribe(Locked<ZapitDaqEvent>(OnEvent), observer.OnError),
                frames.Subscribe(Locked<IplImage>(OnFrame), observer.OnError),
                gridRuns.Subscribe(Locked<string>(Start), observer.OnError),
                stills.Subscribe(Locked<string>(Still), observer.OnError),
                banks.Subscribe(Locked<string>(Bank), observer.OnError),
                aims.Subscribe(Locked<string>(Aim), observer.OnError, AimsEnded),
                laserOffs.Subscribe(Locked<string>(LaserOff), observer.OnError));
        });
    }

    /// <summary>ZapitDaq's Zero command for each input. Only Zero: a bank loads through ZapitSetupCapture, which checks its path.</summary>
    [Combinator]
    [Description("ZapitDaq's Zero command (every line to 0 V) for any input. A bank loads only through ZapitSetupCapture.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class CreateZapitDaqCommand
    {
        /// <summary>A Zero command per input.</summary>
        public IObservable<ZapitDaqCommand> Process<TSource>(IObservable<TSource> source) =>
            source.Select(_ => new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero });
    }

    /// <summary>
    /// Zapit-Main.Crop's args as the Zapit camera's crop (the window and the mp4 cut to the brain, the
    /// agent's crop round the grid area), each with its request number, "#n x,y,w,h" or "#n full" (n a whole number from 1),
    /// echoed in every answer so the agent matches answers to requests: "x,y,w,h" in pixels of the camera's frame (whole numbers, x and y from 0, w and
    /// h even and from 16) is Stage Cropping with that crop on Camera; "#n full" is Stage Cropping (Detail "#n Full Frame") with the
    /// whole frame; anything else, a request with no number (Refused #0) included, is a request with no crop (Camera.RejectReason). Reported as the metric Zapit-Main.Crop.
    /// A taken request is only Cropping (Detail x,y,w,h, Full Frame, or the request's text), not yet answered: the camera
    /// alone knows its frame's size and the crop in force, and answers from its next frame, Cropped, Full Frame or Refused
    /// (a crop outside the frame is dropped, never clamped), each Refused saying which crop is now in force ("; Now Full
    /// Frame" or "; Now Cropped x,y,w,h"), through ZapitCropReport. Taken until Go only (ZapitSetup): the mp4
    /// starts at Go, so its frame size never changes.
    /// </summary>
    [Combinator]
    [Description("Zapit-Main.Crop's args (#n x,y,w,h in pixels of the camera's frame, or #n full for the whole frame; n a whole number from 1) as the Zapit camera's crop for the shown and recorded frame: Cropping (the crop, or why it is none, on Camera; the camera answers).")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ZapitCropRequest
    {
        /// <summary>The smallest crop side, in pixels.</summary>
        public const int LeastSide = 16;

        /// <summary>Each request as a Cropping #n step for the camera to answer.</summary>
        public IObservable<ZapitSetupStep> Process(IObservable<string> source) => source.Select(Parse);

        /// <summary>A request that is no crop, as a step the camera answers (it alone knows the crop in force).</summary>
        static ZapitSetupStep Rejected(int number, string echo, string reason) =>
            new ZapitSetupStep { Stage = "Cropping", Detail = $"#{number} {echo}", Camera = new ZapitCameraSettings { SetCrop = true, CropRequest = number, RejectReason = reason } };

        /// <summary>One request as a setup step (Process): "#n x,y,w,h" or "#n full", n a whole number from 1. A request
        /// without a valid number is number 0.</summary>
        public static ZapitSetupStep Parse(string request)
        {
            var text = (request ?? "").Trim().Trim('"').Trim();
            var number = 0;
            var rest = text;
            if (text.StartsWith("#", StringComparison.Ordinal))
            {
                var space = text.IndexOfAny(new[] { ' ', '\t' });
                var head = space < 0 ? text.Substring(1) : text.Substring(1, space - 1);
                if (int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
                {
                    number = n;
                    rest = space < 0 ? "" : text.Substring(space).Trim();
                }
            }
            if (number == 0)
                return Rejected(0, text, "a crop request is #<n> x,y,w,h or #<n> full, n a whole number from 1: " + request);
            if (rest.Equals("full", StringComparison.OrdinalIgnoreCase))
                return new ZapitSetupStep { Stage = "Cropping", Detail = $"#{number} Full Frame", Camera = new ZapitCameraSettings { SetCrop = true, CropRequest = number } };
            var parts = rest.Split(',');
            var v = new int[4];
            if (parts.Length != 4 || Enumerable.Range(0, 4).Any(i => !int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i])))
                return Rejected(number, rest, "a crop is x,y,w,h in whole pixels, or full: " + request);
            if (v[0] < 0 || v[1] < 0 || v[2] < LeastSide || v[3] < LeastSide || v[2] % 2 != 0 || v[3] % 2 != 0)
                return Rejected(number, rest, $"a crop's x and y are from 0, its w and h even and from {LeastSide}: " + request);
            var detail = string.Join(",", v.Select(n => n.ToString(CultureInfo.InvariantCulture)));
            return new ZapitSetupStep
            {
                Stage = "Cropping", Detail = $"#{number} {detail}",
                Camera = new ZapitCameraSettings { SetCrop = true, CropRequest = number, CropX = v[0], CropY = v[1], CropWidth = v[2], CropHeight = v[3] },
            };
        }
    }

    /// <summary>The Zapit camera's answer to a crop (ZapitFrame.CropReports: Cropped, Full Frame or Refused, in order), for the metric
    /// Zapit-Main.Crop; the frames that carry none are passed over.</summary>
    [Combinator]
    [Description("The Zapit camera's answer to a crop (Cropped, Full Frame or Refused), for the metric Zapit-Main.Crop. Input: each frame's CropReports (null is passed over).")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ZapitCropReport
    {
        /// <summary>Each frame's answers, in order; a frame without any is passed over.</summary>
        public IObservable<ZapitSetupStep> Process(IObservable<ZapitSetupStep[]> source) => source.Where(steps => steps != null).SelectMany(steps => steps);
    }

    /// <summary>The loaded bank, checked at Go against the task's conditions file and the trials' rules.</summary>
    [Combinator]
    [Description("Checks the bank ZapitDaq loaded before Go against the task: built from the task's conditions file (its SHA-256), " +
        "and holding laserPowerMw for every condition. Input: (bank, conditions file path, laserPowerMw). Stops the workflow naming what differs; passes the bank on.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ZapitBankForTask
    {
        /// <summary>Checks each (bank, conditions path, power).</summary>
        public IObservable<ZapitBank> Process(IObservable<Tuple<ZapitBank, string, double>> source)
        {
            return source.Select(input => Check(input.Item1, input.Item2, input.Item3));
        }

        /// <summary>The bank, after checking it was built from <paramref name="conditionsPath"/> and holds <paramref name="laserPowerMw"/> for every condition.</summary>
        public static ZapitBank Check(ZapitBank bank, string conditionsPath, double laserPowerMw)
        {
            if (bank == null) throw new InvalidOperationException("No Zapit bank was loaded before Go: Zapit's setup (Zapit-Main.LoadBank) must finish first.");
            bank.Verify(conditionsPath, null, null, null);
            foreach (var condition in bank.Conditions) bank.Variant(condition.Condition, true, laserPowerMw);
            return bank;
        }
    }
}
