using Bonsai;
using OpenRigs.Devices.Rigs;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// A preflight's check, shared by every rig kind's preflight (PreflightChecks.bonsai): the rows are rig.yml's devices,
// counted from the device pieces' own data (PreflightTap), so a device rig.yml has that sends nothing still
// has a row, its error. Bonsai measures: nothing here judges a device.
namespace OpenRigs.Devices
{
    /// <summary>One device of rig.yml a preflight covers: a device (kind: entry: settings) or a single section.</summary>
    public class PreflightDevice
    {
        /// <summary>The rig.yml key, such as camera.</summary>
        public string Kind { get; set; }
        /// <summary>The device's name in the data, such as Camera-Body; SpeakerArray for a single section.</summary>
        public string Name { get; set; }
        /// <summary>What its count is of: messages (a Harp board), frames (a camera, a projector), sounds (a speaker array).</summary>
        public string Measure { get; set; }
    }

    /// <summary>One item a device piece sent.</summary>
    public class DeviceTick
    {
        /// <summary>The device's name.</summary>
        public string Device { get; set; }
    }

    /// <summary>How many test sounds the mixer has been sent for each rig.yml speaker, in rig.yml's order.</summary>
    public class SpeakerSounds
    {
        /// <summary>Each speaker's rig.yml name, such as left.</summary>
        public string[] Names { get; set; }
        /// <summary>The test sounds sent to the mixer on each speaker's channel, by Names.</summary>
        public int[] Counts { get; set; }

        /// <summary>The fewest sent to any one speaker: every speaker has been sent at least this many; 0 with no speakers.</summary>
        public int Fewest
        {
            get { return Counts == null || Counts.Length == 0 ? 0 : Counts.Min(); }
        }
    }

    /// <summary>One raw measurement, a row of the preflight's CSV: device,measure,value,detail.</summary>
    public class PreflightRow
    {
        /// <summary>The device's name.</summary>
        public string device { get; set; }
        /// <summary>What was measured: messages, frames, seconds, sounds:&lt;speaker&gt;, person, error.</summary>
        public string measure { get; set; }
        /// <summary>The number measured.</summary>
        public string value { get; set; }
        /// <summary>An error's message, with no commas, semicolons or line breaks.</summary>
        public string detail { get; set; }
    }

    /// <summary>What a device sent in one interval of about a second, or why it has no count.</summary>
    public class DeviceReport
    {
        /// <summary>The device's name.</summary>
        public string Device { get; set; }
        /// <summary>What is counted: messages, frames.</summary>
        public string Measure { get; set; }
        /// <summary>Items sent in the interval.</summary>
        public long Count { get; set; }
        /// <summary>The interval's length, by a stopwatch: intervals follow one another with no gap.</summary>
        public double Seconds { get; set; }
        /// <summary>Whether the device has sent its first item, or has its error.</summary>
        public bool Settled { get; set; }
        /// <summary>Why the device has no count; null once it has sent its first item.</summary>
        public string Error { get; set; }
    }

    /// <summary>A rig kind's check, its task file: how long each device is counted and what a person is asked, by rig.yml kind.</summary>
    public class PreflightTask
    {
        /// <summary>Seconds each device is counted after Go, 1 to 60.</summary>
        public double CountSeconds { get; set; }
        /// <summary>What a person is asked about each device of a kind, by rig.yml key, such as speakerArray.</summary>
        public Dictionary<string, string> Questions { get; set; }

        /// <summary>The task file's text read, or an exception naming the field it refuses: an unknown key, a count outside 1 to 60 s, a question for a kind no preflight lists.</summary>
        public static PreflightTask Parse(string text)
        {
            var task = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build()
                .Deserialize<PreflightTask>(text ?? string.Empty);
            if (task == null) throw new InvalidDataException("The preflight's task file is empty: give countSeconds and questions.");
            if (!(task.CountSeconds >= 1 && task.CountSeconds <= 60))
                throw new InvalidDataException("The preflight's task file has countSeconds " + task.CountSeconds.ToString(CultureInfo.InvariantCulture) + ": give 1 to 60.");
            task.Questions = task.Questions ?? new Dictionary<string, string>();
            foreach (var kind in task.Questions.Keys)
            {
                if (!PreflightDevices.AllKinds.Contains(kind))
                    throw new InvalidDataException("The preflight's task file asks about " + kind + ", which is not a rig.yml kind a preflight lists (" + string.Join(", ", PreflightDevices.AllKinds) + ").");
            }
            return task;
        }
    }

    /// <summary>Where the check is after Go: a row to write, the step and prompt metrics, and whether it is done.</summary>
    public class CheckState
    {
        /// <summary>A raw row, or null.</summary>
        public PreflightRow Row { get; set; }
        /// <summary>The step metric: n/total Device while a device is counted, else empty.</summary>
        public string Step { get; set; }
        /// <summary>The prompt metric: Device: question while a person's answer is awaited, else empty.</summary>
        public string Prompt { get; set; }
        /// <summary>True once, on the last state: every row has been given.</summary>
        public bool Done { get; set; }
    }

    static class Preflight
    {
        public static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static HashSet<string> Kinds(string kinds)
        {
            return new HashSet<string>((kinds ?? string.Empty).Split(',').Select(k => k.Trim()).Where(k => k.Length > 0));
        }

        // An error's message for a CSV field and the devices metric: no commas, semicolons, quotes or line breaks.
        public static string Clean(string message)
        {
            return Regex.Replace((message ?? string.Empty).Replace("\"", "'"), "[,;\\s]+", " ").Trim();
        }

        // A Confirm's text, <Device> yes or <Device> no; false when it is neither.
        public static bool ParseAnswer(string text, out string device, out bool yes)
        {
            device = null;
            yes = false;
            var parts = (text ?? string.Empty).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;
            var word = parts[1].ToLowerInvariant();
            if (word != "yes" && word != "no") return false;
            device = parts[0];
            yes = word == "yes";
            return true;
        }
    }

    /// <summary>The rig.yml devices a preflight covers, in the order of Kinds and each kind's entries in rig.yml's order.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("The devices of the kinds named in rig.yml (the input: ReadLabRig's LabRig), kind by kind in the order of Kinds and each kind's entries in rig.yml's order, as one array: the preflight's rows.")]
    public class PreflightDevices
    {
        /// <summary>Every kind a preflight lists, in its order.</summary>
        public static readonly string[] AllKinds = { "behaviorBoard", "timestampGeneratorGen3", "stepperDriver", "licketySplit", "camera", "speakerArray", "projector" };

        /// <summary>The rig.yml keys to list, separated by commas.</summary>
        [Description("The rig.yml keys to list, separated by commas.")]
        public string Kinds { get; set; } = string.Join(",", AllKinds);

        /// <summary>The devices of each rig.</summary>
        public IObservable<PreflightDevice[]> Process(IObservable<LabRig> source)
        {
            return source.Select(rig => List(rig, Kinds).ToArray());
        }

        /// <summary>The rig's devices of the kinds named.</summary>
        public static List<PreflightDevice> List(LabRig rig, string kinds)
        {
            var devices = new List<PreflightDevice>();
            foreach (var kind in Preflight.Kinds(kinds))
            {
                switch (kind)
                {
                    case "behaviorBoard": devices.AddRange(Each(kind, rig.BehaviorBoard, "messages")); break;
                    case "timestampGeneratorGen3": devices.AddRange(Each(kind, rig.TimestampGeneratorGen3, "messages")); break;
                    case "stepperDriver": devices.AddRange(Each(kind, rig.StepperDriver, "messages")); break;
                    case "licketySplit": devices.AddRange(Each(kind, rig.LicketySplit, "messages")); break;
                    case "camera": devices.AddRange(Each(kind, rig.Camera, "frames")); break;
                    case "speakerArray": if (rig.SpeakerArray != null) devices.Add(Device(kind, Pascal(kind), "sounds")); break;
                    case "projector": if (rig.Projector != null) devices.Add(Device(kind, Pascal(kind), "frames")); break;
                    default: throw new InvalidOperationException("PreflightDevices' Kinds names '" + kind + "', which is not one of " + string.Join(", ", AllKinds) + ".");
                }
            }
            return devices;
        }

        static IEnumerable<PreflightDevice> Each<T>(string kind, Dictionary<string, T> entries, string measure)
        {
            if (entries == null) yield break;
            foreach (var entry in entries) yield return Device(kind, Pascal(kind) + "-" + Pascal(entry.Key), measure);
        }

        static PreflightDevice Device(string kind, string name, string measure)
        {
            return new PreflightDevice { Kind = kind, Name = name, Measure = measure };
        }

        static string Pascal(string name)
        {
            return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);
        }
    }

    /// <summary>
    /// A device piece's data as the preflight's ticks, one per item: a device is known to work from its first item, never
    /// from being tapped. Into DeviceTicks, which PreflightChecks declares.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("A device's data (its Name.Data) as the preflight's ticks, one per item. Multicast into DeviceTicks.")]
    public class PreflightTap
    {
        /// <summary>The device's name, as rig.yml's entry gives it, such as Camera-Face.</summary>
        [Description("The device's name, as rig.yml's entry gives it, such as Camera-Face.")]
        public string Device { get; set; }

        /// <summary>The ticks of the device's data.</summary>
        public IObservable<DeviceTick> Process<TSource>(IObservable<TSource> source)
        {
            return Observable.Defer(() =>
            {
                var tick = new DeviceTick { Device = Device };
                return source.Select(_ => tick);
            });
        }
    }

    /// <summary>
    /// Each counted device's report about once a second, from the ticks: the items it sent in that interval, or, for a
    /// device rig.yml has that has sent nothing within OpenSeconds, its error, until its first item.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    [Description("Each counted device's report once a second from the ticks: items sent in the interval, or an error for a device rig.yml has that has sent nothing yet.")]
    public class DeviceReports
    {
        /// <summary>Kinds with nothing to count, only a person's answer.</summary>
        [Description("Kinds with nothing to count, only a person's answer.")]
        public string NotCounted { get; set; } = "speakerArray";

        /// <summary>Seconds after which a device that has sent nothing is reported with its error.</summary>
        [Description("Seconds after which a device that has sent nothing is reported with its error.")]
        public double OpenSeconds { get; set; } = 5;

        /// <summary>The reports.</summary>
        public IObservable<DeviceReport> Process(IObservable<PreflightDevice[]> devices, IObservable<DeviceTick> ticks)
        {
            var skip = Preflight.Kinds(NotCounted);
            var open = TimeSpan.FromSeconds(OpenSeconds);
            return Observable.Create<DeviceReport>(observer =>
            {
                var gate = new object();
                var clock = Stopwatch.StartNew();
                var last = TimeSpan.Zero;
                PreflightDevice[] list = null;
                var counts = new Dictionary<string, long>();
                var seen = new HashSet<string>();
                var listed = devices.Take(1).Subscribe(l => { lock (gate) list = l.Where(d => !skip.Contains(d.Kind)).ToArray(); }, observer.OnError);
                var heard = ticks.Subscribe(t =>
                {
                    lock (gate)
                    {
                        seen.Add(t.Device);
                        long n;
                        counts.TryGetValue(t.Device, out n);
                        counts[t.Device] = n + 1;
                    }
                }, observer.OnError);
                var reported = Observable.Interval(TimeSpan.FromSeconds(1)).Subscribe(_ =>
                {
                    var reports = new List<DeviceReport>();
                    lock (gate)
                    {
                        if (list == null) return;
                        var now = clock.Elapsed;
                        foreach (var device in list)
                        {
                            long count;
                            counts.TryGetValue(device.Name, out count);
                            counts[device.Name] = 0;
                            var report = new DeviceReport { Device = device.Name, Measure = device.Measure, Count = count, Seconds = (now - last).TotalSeconds };
                            if (seen.Contains(device.Name)) report.Settled = true;
                            else if (now >= open)
                            {
                                report.Settled = true;
                                report.Error = Preflight.Clean("rig.yml has " + device.Name + " but nothing has come from it in " +
                                    open.TotalSeconds.ToString(Preflight.Invariant) + " s: the rig list has no such device or it sends nothing");
                            }
                            reports.Add(report);
                        }
                        last = now;
                    }
                    foreach (var report in reports) observer.OnNext(report);
                });
                return new CompositeDisposable(listed, heard, reported);
            });
        }
    }

    /// <summary>
    /// The devices metric, once a second once every device has sent or has its error: each device in list order as
    /// Device measure rate (per second over the last second) or Device error detail, joined by ;. A speaker array is
    /// SpeakerArray sounds n: every rig.yml speaker has been sent at least n test sounds through the mixer.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    [Description("Each device's rate over the last second, or its error, once a second once every device has sent or has its error; a speaker array as the test sounds each speaker has been sent.")]
    public class DeviceRates
    {
        /// <summary>Kinds with nothing to count: a speaker array is listed as SpeakerArray sounds n, any other as Device person 0.</summary>
        [Description("Kinds with nothing to count: a speaker array is listed as SpeakerArray sounds n, any other as Device person 0.")]
        public string NotCounted { get; set; } = "speakerArray";

        /// <summary>The devices metric's text.</summary>
        public IObservable<string> Process(IObservable<PreflightDevice[]> devices, IObservable<DeviceReport> reports, IObservable<SpeakerSounds> sounds)
        {
            var skip = Preflight.Kinds(NotCounted);
            return Observable.Create<string>(observer =>
            {
                var gate = new object();
                PreflightDevice[] list = null;
                SpeakerSounds sent = null;
                var latest = new Dictionary<string, DeviceReport>();
                var listed = devices.Take(1).Subscribe(l => { lock (gate) list = l; }, observer.OnError);
                var heard = reports.Subscribe(r => { lock (gate) latest[r.Device] = r; }, observer.OnError);
                var played = sounds.Subscribe(s => { lock (gate) sent = s; }, observer.OnError);
                var ticks = Observable.Interval(TimeSpan.FromSeconds(1)).Subscribe(_ =>
                {
                    string text;
                    lock (gate)
                    {
                        DeviceReport report;
                        if (list == null || !list.All(d => skip.Contains(d.Kind) || (latest.TryGetValue(d.Name, out report) && report.Settled))) return;
                        text = string.Join(";", list.Select(d => skip.Contains(d.Kind) ? Unopened(d, sent) : Entry(d, latest[d.Name])));
                    }
                    observer.OnNext(text);
                });
                return new CompositeDisposable(listed, heard, played, ticks);
            });
        }

        static string Unopened(PreflightDevice device, SpeakerSounds sent)
        {
            if (device.Kind != "speakerArray") return device.Name + " person 0";
            return device.Name + " sounds " + (sent == null ? 0 : sent.Fewest).ToString(Preflight.Invariant);
        }

        static string Entry(PreflightDevice device, DeviceReport report)
        {
            if (report.Error != null) return device.Name + " error " + report.Error;
            var rate = report.Seconds > 0 ? report.Count / report.Seconds : 0;
            return device.Name + " " + report.Measure + " " + rate.ToString("0.#", Preflight.Invariant);
        }
    }

    /// <summary>The rig kind's task file, read and checked each time its path comes (at Go).</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Reads the preflight's task file (countSeconds, questions by rig.yml kind) at each input, refusing an unknown key, a count outside 1 to 60 s or a question for a kind no preflight lists.")]
    public class ReadPreflightTask
    {
        /// <summary>The task file's path.</summary>
        [Description("The task file's path.")]
        public string Path { get; set; }

        /// <summary>The task, once per input.</summary>
        public IObservable<PreflightTask> Process<TSource>(IObservable<TSource> source)
        {
            return source.Select(_ => PreflightTask.Parse(File.ReadAllText(Path)));
        }
    }

    /// <summary>
    /// The check, from its task (read at Go): each counted device counted in turn for the task's countSeconds from its
    /// reports (step metric), then each device of a kind the task has a question for asked in turn (prompt metric) until
    /// its Confirm comes. Gives the raw rows, then a Done state, then completes.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    [Description("From the task (read at Go): counts each device in turn, then asks the person what the task asks about each device of a kind; gives the raw rows.")]
    public class RunCheck
    {
        /// <summary>Kinds with nothing to count, only a person's answer.</summary>
        [Description("Kinds with nothing to count, only a person's answer.")]
        public string NotCounted { get; set; } = "speakerArray";

        /// <summary>The check's states. A speaker array's rows are, before its person row, one per rig.yml speaker:
        /// SpeakerArray,sounds:&lt;speaker&gt;,n, the test sounds sent to the mixer on that speaker's channel by the answer.</summary>
        public IObservable<CheckState> Process(IObservable<PreflightDevice[]> devices, IObservable<DeviceReport> reports, IObservable<PreflightTask> task, IObservable<string> confirms, IObservable<SpeakerSounds> sounds)
        {
            var skip = Preflight.Kinds(NotCounted);
            return Observable.Create<CheckState>(observer =>
            {
                var inbox = new BlockingCollection<object>();
                var cancel = new CancellationTokenSource();
                Action<Exception> fail = e => inbox.Add(e);
                var subscriptions = new CompositeDisposable(
                    devices.Take(1).Subscribe(l => inbox.Add(l), fail),
                    reports.Subscribe(r => inbox.Add(r), fail),
                    task.Take(1).Subscribe(t => inbox.Add(t), fail),
                    confirms.Subscribe(c => inbox.Add(c ?? string.Empty), fail),
                    sounds.Subscribe(s => inbox.Add(s), fail));
                var thread = new Thread(() =>
                {
                    try { new Run(inbox, cancel.Token, observer, skip).Check(); }
                    catch (OperationCanceledException) { }  // the subscription was disposed: nothing left to report to
                });
                thread.IsBackground = true;
                thread.Name = "Preflight check";
                thread.Start();
                return new CompositeDisposable(subscriptions, Disposable.Create(cancel.Cancel));
            });
        }

        class Run
        {
            readonly BlockingCollection<object> inbox;
            readonly CancellationToken token;
            readonly IObserver<CheckState> observer;
            readonly HashSet<string> skip;
            readonly Dictionary<string, string> errors = new Dictionary<string, string>();
            readonly Dictionary<string, bool> answers = new Dictionary<string, bool>();
            PreflightDevice[] list;
            PreflightTask task;
            SpeakerSounds sent;

            public Run(BlockingCollection<object> inbox, CancellationToken token, IObserver<CheckState> observer, HashSet<string> skip)
            {
                this.inbox = inbox;
                this.token = token;
                this.observer = observer;
                this.skip = skip;
            }

            public void Check()
            {
                Send(null, string.Empty, string.Empty, false);
                while (list == null || task == null) Next(Timeout.InfiniteTimeSpan);
                var counted = list.Where(d => !skip.Contains(d.Kind)).ToList();
                for (var i = 0; i < counted.Count; i++)
                {
                    var device = counted[i];
                    Send(null, (i + 1).ToString(Preflight.Invariant) + "/" + counted.Count.ToString(Preflight.Invariant) + " " + device.Name, string.Empty, false);
                    foreach (var row in Count(device.Name)) Send(row, null, null, false);
                }
                foreach (var device in list.Where(d => task.Questions.ContainsKey(d.Kind)))
                {
                    while (!answers.ContainsKey(device.Name))
                    {
                        Send(null, string.Empty, device.Name + ": " + task.Questions[device.Kind], false);
                        Next(Timeout.InfiniteTimeSpan);
                    }
                    // The evidence before the person's word: the test sounds the mixer was sent for each speaker, by the answer.
                    if (device.Kind == "speakerArray" && sent != null)
                    {
                        for (var i = 0; i < sent.Names.Length; i++)
                            Send(Row(device.Name, "sounds:" + sent.Names[i], sent.Counts[i].ToString(Preflight.Invariant), string.Empty), null, null, false);
                    }
                    Send(Row(device.Name, "person", answers[device.Name] ? "1" : "0", string.Empty), string.Empty, string.Empty, false);
                }
                Send(null, string.Empty, string.Empty, true);
                observer.OnCompleted();
            }

            // The device's rows: its count over whole reports adding up to the window, and their seconds.
            IEnumerable<PreflightRow> Count(string device)
            {
                string error;
                if (errors.TryGetValue(device, out error)) return new[] { Row(device, "error", string.Empty, error) };
                long count = 0;
                double seconds = 0;
                string measure = null;
                var window = task.CountSeconds;
                var deadline = Stopwatch.StartNew();
                var limit = TimeSpan.FromSeconds(window + 5);
                while (seconds < window)
                {
                    var left = limit - deadline.Elapsed;
                    if (left <= TimeSpan.Zero) break;
                    var report = Next(left) as DeviceReport;
                    if (report == null || report.Device != device) continue;
                    if (report.Error != null) return new[] { Row(device, "error", string.Empty, report.Error) };
                    count += report.Count;
                    seconds += report.Seconds;
                    measure = report.Measure;
                }
                if (measure == null)
                    return new[] { Row(device, "error", string.Empty, "it sent no report in " + limit.TotalSeconds.ToString(Preflight.Invariant) + " s") };
                return new[]
                {
                    Row(device, measure, count.ToString(Preflight.Invariant), string.Empty),
                    Row(device, "seconds", seconds.ToString("0.###", Preflight.Invariant), string.Empty),
                };
            }

            // The next item from the inbox, after noting what it says; null after the timeout.
            object Next(TimeSpan timeout)
            {
                object item;
                if (!inbox.TryTake(out item, timeout.Equals(Timeout.InfiniteTimeSpan) ? Timeout.Infinite : (int)Math.Ceiling(timeout.TotalMilliseconds), token)) return null;
                var error = item as Exception;
                if (error != null) { observer.OnError(error); throw new OperationCanceledException(); }
                if (item is PreflightDevice[]) list = (PreflightDevice[])item;
                else if (item is PreflightTask) task = (PreflightTask)item;
                else if (item is SpeakerSounds) sent = (SpeakerSounds)item;
                var report = item as DeviceReport;
                // A device's latest error: one that has since sent its first item has none.
                if (report != null && report.Error != null) errors[report.Device] = report.Error;
                else if (report != null) errors.Remove(report.Device);
                var text = item as string;
                if (text != null && task != null)
                {
                    // The first answer after Go for each device is the one kept.
                    string device;
                    bool yes;
                    if (!Preflight.ParseAnswer(text, out device, out yes)) Console.Error.WriteLine("preflight: Confirm '" + text + "' ignored: give <Device> yes or <Device> no");
                    else if (!answers.ContainsKey(device)) answers[device] = yes;
                }
                return item;
            }

            void Send(PreflightRow row, string step, string prompt, bool done)
            {
                if (row != null) Console.Error.WriteLine("preflight: " + row.device + " " + row.measure + " " + row.value + " " + row.detail);
                observer.OnNext(new CheckState { Row = row, Step = step, Prompt = prompt, Done = done });
            }
        }

        static PreflightRow Row(string device, string measure, string value, string detail)
        {
            return new PreflightRow { device = device, measure = measure, value = value, detail = detail };
        }
    }

    /// <summary>The rows of the check's states, for CsvWriter.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("The rows of the check's states.")]
    public class RowsOf
    {
        /// <summary>Each state's row, where it has one.</summary>
        public IObservable<PreflightRow> Process(IObservable<CheckState> source)
        {
            return source.Where(s => s.Row != null).Select(s => s.Row);
        }
    }

    /// <summary>The first Confirm for the device after Go, the person's answer.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    [Description("The first Confirm for the device after Go.")]
    public class Answered
    {
        /// <summary>The device's name.</summary>
        [Description("The device's name, such as SpeakerArray.")]
        public string Device { get; set; }

        /// <summary>The Confirm's text, once.</summary>
        public IObservable<string> Process<TGo>(IObservable<string> confirms, IObservable<TGo> go)
        {
            return Observable.Defer(() =>
            {
                var name = Device;
                return confirms.SkipUntil(go).Where(text =>
                {
                    string device;
                    bool yes;
                    return Preflight.ParseAnswer(text, out device, out yes) && device == name;
                }).Take(1);
            });
        }
    }
}
