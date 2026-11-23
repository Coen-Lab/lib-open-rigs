using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
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
        /// <summary>The stage, as the Zapit.Setup metric reports it: Pointing, FramesSaved, StillSaved, Loading or Refused.</summary>
        public string Stage { get; set; } = "";

        /// <summary>The run folder or still file the stage is about, or why a request was refused.</summary>
        public string Detail { get; set; } = "";

        /// <summary>The command for ZapitDaq, or null.</summary>
        public ZapitDaqCommand? Command { get; set; }

        /// <summary>The step as the Zapit.Setup metric: the stage, a space, the detail.</summary>
        public override string ToString() => Stage + " " + Detail;
    }

    /// <summary>
    /// The per-session scanner grid and sample still of Zapit's setup phase (docs/zapit-setup-protocol.md). A grid run is a
    /// folder in the rig's pending folder holding the agent's <c>grid.json</c> ({"volts": [[x, y], ...], "laserVolts",
    /// "skipFrames", "backgrounds"}, volts from lib_zapit's calibration plan): per point it points the galvos with the laser
    /// on, skips skipFrames camera frames and saves the next as <c>point_NNNN.png</c>, then with the laser off saves
    /// backgrounds frames as <c>background_K.png</c> (lossless PNG, as lib_zapit's fit reads them), zeroes the lines and
    /// reports FramesSaved. A still saves the next frame, laser off, as the PNG named. No maths: the volts are the agent's.
    /// </summary>
    [Combinator]
    [Description("Zapit's setup phase: each grid run (a pending folder holding grid.json) points the galvos at each grid point, saving one frame per point and laser-off backgrounds as PNGs; each still saves the next frame as the PNG named; each bank .json in pending is loaded (LoadBank). Inputs: grid run folders, still paths, bank paths, ZapitDaq's events, the camera's images. Output: ZapitDaq commands and the stages to report as Zapit.Setup.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class ZapitSetupCapture
    {
        /// <summary>Gets or sets the folder every run, still and bank must be inside: the rig's pending folder.</summary>
        [Description("The folder every grid run, still and bank must be inside: RigConfigs\\<RIG>\\pending.")]
        public string PendingFolder { get; set; } = "";

        sealed class Run
        {
            public string Folder = "";
            public double[][] Volts = new double[0][];
            public double LaserVolts;
            public int SkipFrames;
            public int Backgrounds;
            public int Index;          // the point being saved; Volts.Length onwards the backgrounds
            public int Skip = -1;      // frames still to skip; -1 while waiting for Pointed
        }

        /// <summary>The setup steps for the requests, as frames and ZapitDaq's events come. A bank request (its
        /// Settings_ZapitBank_&lt;DateTime&gt;.json) inside the pending folder becomes ZapitDaq's LoadBank (stage Loading);
        /// one outside it is refused.</summary>
        public IObservable<ZapitSetupStep> Process(IObservable<string> gridRuns, IObservable<string> stills, IObservable<string> banks,
            IObservable<ZapitDaqEvent> events, IObservable<IplImage> frames) => Observable.Create<ZapitSetupStep>(observer =>
        {
            var pending = Path.GetFullPath(PendingFolder);
            var gate = new object();
            Run? run = null;
            string? still = null;
            void Emit(ZapitSetupStep step) => observer.OnNext(step);
            void Refuse(string why) => Emit(new ZapitSetupStep { Stage = "Refused", Detail = why });
            string? Inside(string path)
            {
                var full = Path.GetFullPath(path.Trim().Trim('"'));
                return full.StartsWith(pending.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? full : null;
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
                    if (r.Volts.Length == 0 || r.Volts.Any(p => p.Length != 2 || p.Any(v => !(Math.Abs(v) <= ZapitBank.RangeVolts))) || !(Math.Abs(r.LaserVolts) <= ZapitBank.RangeVolts) || r.SkipFrames < 0 || r.Backgrounds < 1)
                        throw new InvalidDataException("its volts must be [x, y] pairs within +/-10 V, with skipFrames from 0 and backgrounds from 1");
                    if (Directory.EnumerateFiles(folder!, "*.png").Any()) throw new InvalidDataException("the folder already holds frames");
                    run = r;
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is Newtonsoft.Json.JsonException || error is NullReferenceException || error is FormatException || error is ArgumentException)
                {
                    Refuse(grid + " is refused: " + error.Message);
                    return;
                }
                Emit(new ZapitSetupStep { Stage = "Pointing", Detail = run.Folder, Command = PointAt(run) });
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
                Emit(new ZapitSetupStep { Stage = "Loading", Detail = path, Command = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.LoadBank, Path = path } });
            }

            void OnEvent(ZapitDaqEvent e)
            {
                if (run != null && e.Kind == ZapitDaqEventKind.Pointed && run.Skip < 0) run.Skip = run.SkipFrames;
            }

            void OnFrame(IplImage image)
            {
                if (still != null)
                {
                    CV.SaveImage(still, image);
                    Emit(new ZapitSetupStep { Stage = "StillSaved", Detail = still });
                    still = null;
                    return;
                }
                if (run == null || run.Skip < 0) return;
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
                    var done = run.Folder;
                    run = null;
                    Emit(new ZapitSetupStep { Stage = "FramesSaved", Detail = done, Command = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero } });
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
                events.Subscribe(Locked<ZapitDaqEvent>(OnEvent), observer.OnError),
                frames.Subscribe(Locked<IplImage>(OnFrame), observer.OnError),
                gridRuns.Subscribe(Locked<string>(Start), observer.OnError),
                stills.Subscribe(Locked<string>(Still), observer.OnError),
                banks.Subscribe(Locked<string>(Bank), observer.OnError));
        });
    }

    /// <summary>A ZapitDaq command of one kind from each input: LoadBank takes the input as the bank's path.</summary>
    [Combinator]
    [Description("A ZapitDaq command of Kind for each input: LoadBank with the input as the bank's Path (Settings_ZapitBank_<DateTime>.json), Zero for any input.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class CreateZapitDaqCommand
    {
        /// <summary>Gets or sets the command's kind: LoadBank or Zero.</summary>
        [Description("The command: LoadBank (the input is the bank's path) or Zero.")]
        public ZapitDaqCommandKind Kind { get; set; } = ZapitDaqCommandKind.Zero;

        /// <summary>A command per input.</summary>
        public IObservable<ZapitDaqCommand> Process<TSource>(IObservable<TSource> source)
        {
            var kind = Kind;
            if (kind != ZapitDaqCommandKind.LoadBank && kind != ZapitDaqCommandKind.Zero)
                throw new InvalidOperationException("CreateZapitDaqCommand makes LoadBank or Zero, not " + kind + ".");
            return source.Select(input => new ZapitDaqCommand
            {
                Kind = kind,
                Path = kind == ZapitDaqCommandKind.LoadBank ? Convert.ToString(input, CultureInfo.InvariantCulture)?.Trim().Trim('"') : null,
            });
        }
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
            if (bank == null) throw new InvalidOperationException("No Zapit bank was loaded before Go: Zapit's setup (Zapit.LoadBank) must finish first.");
            bank.Verify(conditionsPath, null, null, null);
            foreach (var condition in bank.Conditions) bank.Variant(condition.Condition, true, laserPowerMw);
            return bank;
        }
    }
}
