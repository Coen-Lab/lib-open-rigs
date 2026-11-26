using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
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
    /// "skipFrames", "backgrounds", and optionally "camera": {"exposureUs", "gainDb"}}, volts from lib_zapit's calibration plan):
    /// with a camera block the run's first step carries those settings for ZapitPylonCapture (auto exposure and gain off)
    /// and its last (FramesSaved, or a refusal) puts the camera's own back; per point it points the galvos with the laser
    /// on, skips skipFrames camera frames and saves the next as <c>point_NNNN.png</c>, then with the laser off saves
    /// backgrounds frames as <c>background_K.png</c> (lossless PNG, as lib_zapit's fit reads them), zeroes the lines and
    /// reports FramesSaved. A run with no camera frame within 2 s of Pointed (or of its last frame) is refused and zeroed. A still saves the next frame, laser off, as the PNG named. No maths: the volts are the agent's.
    /// </summary>
    [Combinator]
    [Description("Zapit's setup phase: each grid run (a pending folder holding grid.json) points the galvos at each grid point, saving one frame per point and laser-off backgrounds as PNGs; each still saves the next frame as the PNG named; each bank .json in pending is loaded (LoadBank); each aim (xVolts,yVolts,laserVolts) points the beam there. Inputs: grid run folders, still paths, bank paths, ZapitDaq's events, the camera's images, aims. Output: ZapitDaq commands and the stages to report as Zapit-Main.Setup.")]
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

        /// <summary>How long a grid run waits for a camera frame after Pointed (or after the last frame) before it is
        /// refused and the lines zeroed, so a stalled camera never holds the beam on one spot. Tests shorten it.</summary>
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

        /// <summary>The setup steps for the requests, as frames and ZapitDaq's events come. A bank request (its
        /// Settings_ZapitBank_&lt;DateTime&gt;.json) inside the pending folder becomes ZapitDaq's LoadBank (stage Loading);
        /// one outside it is refused, and one ZapitDaq answers Refused (over the ceiling) is reported Refused with its reason. Laser
        /// volts get ZapitDaq's 1e-4 V float slack. An aim ("xVolts,yVolts,laserVolts", the agent's volts through the scanner fit and laser
        /// curve) points the galvos with the laser on until the next aim, LaserOff or setup step; it is refused at ceilingMw 0,
        /// above LaserMaxVolts or LaserCeilingVolts, outside +/-10 V or during a grid run or still; a grid's laserVolts is held
        /// to the same rules. A still with no frame within FrameTimeout is refused. When the aims end (Go), a grid run or still
        /// in progress is refused and, as for a beam still aimed, the lines are zeroed. ZapitDaq's Zeroed not from a Zero of this
        /// node's own (Zapit-Main.LaserOff, or any other Zero) ends a grid run or still in progress (refused, the camera put back,
        /// the lines zeroed again past any Point that crossed it) and the aim (Off Laser Off): no Point follows until a new
        /// request. An aim not renewed within AimTimeout is turned off (Off, the lines zeroed).</summary>
        public IObservable<ZapitSetupStep> Process(IObservable<string> gridRuns, IObservable<string> stills, IObservable<string> banks,
            IObservable<ZapitDaqEvent> events, IObservable<IplImage> frames, IObservable<string> aims) => Observable.Create<ZapitSetupStep>(observer =>
        {
            var pending = Path.GetFullPath(PendingFolder);
            var gate = new object();
            Run? run = null;
            string? still = null;
            var aiming = false;
            var loading = false;   // a LoadBank awaits ZapitDaq's BankLoaded or Refused
            var ownZero = false;   // a Zero of this node's own is going out: its Zeroed (ZapitDaq answers at once) is not a Laser Off
            void Emit(ZapitSetupStep step)
            {
                if (step.Command?.Kind != ZapitDaqCommandKind.Zero) { observer.OnNext(step); return; }
                var outer = ownZero;
                ownZero = true;
                try { observer.OnNext(step); }
                finally { ownZero = outer; }
            }
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
                    Detail = $"no camera frame came within {FrameTimeout.TotalSeconds} s of Pointed, so the lines are zeroed and the run is refused: {folder}",
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
                        SkipFrames = (int?)json["skipFrames"] ?? 3,
                        Backgrounds = (int?)json["backgrounds"] ?? 4,
                    };
                    if (json["camera"] is JObject camera)
                    {
                        r.Camera = new ZapitCameraSettings { ExposureUs = (double)camera["exposureUs"]!, GainDb = (double)camera["gainDb"]! };
                        if (!(r.Camera.ExposureUs >= 10 && r.Camera.ExposureUs <= 1e6) || !(r.Camera.GainDb >= 0 && r.Camera.GainDb <= 48))
                            throw new InvalidDataException("its camera exposureUs must be 10 to 1000000 and gainDb 0 to 48");
                    }
                    if (r.Volts.Length == 0 || r.Volts.Any(p => p.Length != 2 || p.Any(v => !(Math.Abs(v) <= ZapitBank.RangeVolts))) || !(Math.Abs(r.LaserVolts) <= ZapitBank.RangeVolts) || r.SkipFrames < 0 || r.Backgrounds < 1)
                        throw new InvalidDataException("its volts must be [x, y] pairs within +/-10 V, with skipFrames from 0 and backgrounds from 1");
                    if (LaserProblem(r.LaserVolts) is string laser) throw new InvalidDataException(laser);
                    if (Directory.EnumerateFiles(folder!, "*.png").Any()) throw new InvalidDataException("the folder already holds frames");
                    run = r;
                    StopAiming();
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is Newtonsoft.Json.JsonException || error is NullReferenceException || error is FormatException || error is ArgumentException)
                {
                    Refuse(grid + " is refused: " + error.Message);
                    return;
                }
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

            void AimsEnded()
            {
                lock (gate)
                {
                    if (run != null || still != null)
                    {
                        var ended = run;
                        var what = run?.Folder ?? still;
                        run = null;
                        still = null;
                        watchdog.Disposable = Disposable.Empty;
                        Emit(new ZapitSetupStep
                        {
                            Stage = "Refused",
                            Detail = "Go came during a setup step, so the lines are zeroed and it is refused: " + what,
                            Command = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero },
                            Camera = ended == null ? null : Restore(ended),
                        });
                    }
                    if (!aiming) return;
                    StopAiming();
                    Emit(new ZapitSetupStep { Aim = true, Stage = "Off", Detail = "Go", Command = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero } });
                }
            }

            // Lines zeroed by a Zero not this node's (Zapit-Main.LaserOff): the grid run or still and the aim end, no Point after.
            void LaserOff()
            {
                if (run != null || still != null)
                {
                    var ended = run;
                    var what = run?.Folder ?? still;
                    run = null;
                    still = null;
                    watchdog.Disposable = Disposable.Empty;
                    Emit(new ZapitSetupStep
                    {
                        Stage = "Refused",
                        Detail = "Laser Off came during a setup step, so the lines are zeroed and it is refused: " + what,
                        Command = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero },   // again: past a Point that crossed the Laser Off
                        Camera = ended == null ? null : Restore(ended),
                    });
                }
                if (!aiming) return;
                StopAiming();
                Emit(new ZapitSetupStep { Aim = true, Stage = "Off", Detail = "Laser Off" });
            }

            void OnEvent(ZapitDaqEvent e)
            {
                if (e.Kind == ZapitDaqEventKind.Zeroed)
                {
                    if (!ownZero) LaserOff();
                    return;
                }
                if (loading && (e.Kind == ZapitDaqEventKind.BankLoaded || e.Kind == ZapitDaqEventKind.Refused))
                {
                    loading = false;
                    if (e.Kind == ZapitDaqEventKind.Refused) Refuse(e.Detail ?? "ZapitDaq refused the bank");   // over the ceiling: the session runs on
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
            // ZapitDaq answers a command holding its own lock, so a Zeroed from another thread (Laser Off) never waits here on a
            // thread that holds this lock and is sending ZapitDaq a Point: it is handled once this lock is free instead.
            var onEvent = Locked<ZapitDaqEvent>(OnEvent);
            void Event(ZapitDaqEvent e)
            {
                if (e.Kind != ZapitDaqEventKind.Zeroed) { onEvent(e); return; }
                if (!Monitor.TryEnter(gate)) { Scheduler.Default.Schedule(() => onEvent(e)); return; }
                try { onEvent(e); }
                finally { Monitor.Exit(gate); }
            }
            return new CompositeDisposable(
                watchdog,
                aimWatchdog,
                events.Subscribe(Event, observer.OnError),
                frames.Subscribe(Locked<IplImage>(OnFrame), observer.OnError),
                gridRuns.Subscribe(Locked<string>(Start), observer.OnError),
                stills.Subscribe(Locked<string>(Still), observer.OnError),
                banks.Subscribe(Locked<string>(Bank), observer.OnError),
                aims.Subscribe(Locked<string>(Aim), observer.OnError, AimsEnded));
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
