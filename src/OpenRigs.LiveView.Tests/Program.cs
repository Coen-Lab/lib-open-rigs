using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using OpenRigs.LiveView;

// Stand-ins for the device data types, which the recipes recognise by full name, so the plan can
// be tested without Bonsai.Harp or OpenCV.Net installed.
namespace Bonsai.Harp { public class HarpMessage { } }
namespace OpenCV.Net { public class IplImage { } }
// OpenRigs.Logging's LoggedRows (0.1.0-bbox.5 on), found by full name among the loaded assemblies.
namespace OpenRigs.Logging { public class LoggedRows { } }

// Decides StreamDevices' plan from a rig.yml in a temporary folder, without Bonsai's editor or
// hardware: exit 0 when every check passes. dotnet run --project src\OpenRigs.LiveView.Tests
namespace OpenRigs.LiveView.Tests
{
    // A camera's frame paired with its trigger, as coen-behaviour-box publishes Camera-Box.Data.
    public class Paired { public Frame Value { get; set; } public long Trigger { get; set; } }
    public class Frame { public OpenCV.Net.IplImage Image { get; set; } }

    static class Program
    {
        static int failed;

        static void Check(string what, bool ok, string why)
        {
            Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what + (ok ? "" : " - " + why));
            if (!ok) failed++;
        }

        static LiveViewPlan Plan(string root, string rigYml, Func<string, Type> published)
        {
            var machine = Path.Combine(root, Environment.MachineName);
            Directory.CreateDirectory(machine);
            File.WriteAllText(Path.Combine(machine, "rig.yml"), rigYml);
            return LiveViewPlan.Create(root, published);
        }

        static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "openrigs-liveview-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Console.WriteLine("Devices from rig.yml:");
                var rig = "behaviorBoard:\n  main:\n    portName: COM3\n    pulseController: null\n" +
                          "camera:\n  box:\n    serialNumber: '1'\n    liveView:\n      rate: 10\n      width: 160\n" +
                          "  face:\n    serialNumber: '2'\n  side:\n    serialNumber: '3'\n    liveView:\n      enabled: false\n" +
                          "lickometer:\n  left:\n    portName: COM4\n" +
                          "rootPath: C:\\data\nversion: 0.0.0\n";
                var plan = Plan(root, rig, name =>
                    name == "PathPrefix" ? typeof(string)
                    : name == "BehaviorBoard-Main.Data" ? typeof(Bonsai.Harp.HarpMessage)
                    : name == "Camera-Box.Data" ? typeof(Paired)
                    : name == "Lickometer-Left.Data" ? typeof(object)
                    : null);
                var streamed = string.Join(", ", plan.Streamed.Select(d => d.Stream));
                Check("each published device with a recipe is streamed", streamed == "BehaviorBoard-Main.encoder, Camera-Box.video", streamed);
                var board = plan.Streamed.FirstOrDefault(d => d.Name == "BehaviorBoard-Main");
                var boardStreams = board == null ? "" : string.Join(", ", board.Streams);
                Check("a board streams its encoder and, as a second stream, its channels", boardStreams == "BehaviorBoard-Main.encoder, BehaviorBoard-Main.channels", boardStreams);
                var values = board == null ? null : board.TemplateValues();
                Check("the board's template gets its channels stream and a 100/s analog interval",
                    values != null && values["Channels"] == "BehaviorBoard-Main.channels" && values["AnalogInterval"] == "PT0.01S",
                    values == null ? "not streamed" : values["Channels"] + " " + values["AnalogInterval"]);
                var cameraValues = plan.Streamed.First(d => d.Name == "Camera-Box").TemplateValues();
                Check("a camera has no channels stream", cameraValues["Channels"] == "" && plan.Streamed.First(d => d.Name == "Camera-Box").Streams.Count() == 1, cameraValues["Channels"]);
                var box = plan.Streamed.FirstOrDefault(d => d.Name == "Camera-Box");
                Check("a camera's image is found by type, in a frame paired with its trigger", box != null && box.ImagePath == "Value.Image", box == null ? "not streamed" : box.ImagePath);
                Check("rig.yml's liveView rate and width are taken, the height kept", box != null && box.Rate == 10 && box.Width == 160 && box.Height == 240, box == null ? "" : box.ToString());
                Check("a device the workflow does not publish is skipped, saying so", plan.Skipped.Any(s => s.StartsWith("Camera-Face: the workflow publishes no Camera-Face.Data")), string.Join(" | ", plan.Skipped));
                Check("liveView enabled false is skipped", plan.Skipped.Any(s => s.StartsWith("Camera-Side: liveView enabled is false")), string.Join(" | ", plan.Skipped));
                Check("a kind with no recipe is skipped", plan.Skipped.Any(s => s.StartsWith("Lickometer-Left: no live-view recipe")), string.Join(" | ", plan.Skipped));
                Check("settings that are not devices (rootPath, version) are not devices", !plan.Skipped.Any(s => s.StartsWith("RootPath") || s.StartsWith("Version")), string.Join(" | ", plan.Skipped));
                Check("a string PathPrefix gives the session key and elapsed", plan.HasExperimentFolder, "no experiment folder");
                Check("nothing to note in a good rig.yml", plan.Notes.Count == 0, string.Join(" | ", plan.Notes));
                Check("a Logging with LoggedRows has its rows streamed as logs", plan.StreamsLoggedRows, "not streamed");
                var olderLogging = LiveViewPlan.Create(root, name => name == "PathPrefix" ? typeof(string) : null, name => null);
                Check("an older Logging, without LoggedRows, streams no rows and says so",
                    !olderLogging.StreamsLoggedRows && olderLogging.Notes.Any(n => n.Contains("has no LoggedRows")), string.Join(" | ", olderLogging.Notes));
                Check("the plan changes when LoggedRows comes or goes, so the inner workflow is remade",
                    olderLogging.Signature(5556, 5558) != LiveViewPlan.Create(root, name => name == "PathPrefix" ? typeof(string) : null).Signature(5556, 5558), "same signature");

                Console.WriteLine("Bad settings:");
                var bad = Plan(root, "camera:\n  box:\n    liveView:\n      rate: fast\n      width: 100.5\n      colour: red\n", name => name == "Camera-Box.Data" ? typeof(OpenCV.Net.IplImage) : null);
                var camera = bad.Streamed.Single();
                Check("a bad rate or width is noted and the default kept", camera.Rate == 5 && camera.Width == 320 && bad.Notes.Count(n => n.Contains("rate 'fast'") || n.Contains("width '100.5'")) == 2, string.Join(" | ", bad.Notes));
                Check("an unknown liveView setting is noted", bad.Notes.Any(n => n.Contains("no setting 'colour'")), string.Join(" | ", bad.Notes));
                var nulls = Plan(root, "camera:\n  box:\n    liveView:\n      enabled: null\n      rate: null\n      width: ~\n", name => name == "Camera-Box.Data" ? typeof(OpenCV.Net.IplImage) : null);
                Check("a liveView setting left null keeps its default, unnoted", nulls.Streamed.Single().Rate == 5 && !nulls.Notes.Any(), string.Join(" | ", nulls.Notes));
                Check("data that is the image itself is streamed as it is", camera.ImagePath == "it", camera.ImagePath);
                var wrong = Plan(root, "behaviorBoard:\n  main:\n    portName: COM3\n", name => name == "BehaviorBoard-Main.Data" ? typeof(string) : null);
                Check("a board whose data is not HarpMessage is skipped, saying why", wrong.Skipped.Any(s => s.Contains("is not HarpMessage")), string.Join(" | ", wrong.Skipped));
                // L2-L2: HarpRig.NotHarp's rule, as RecordHarpDevices records and the manifest expects.
                var idle = Plan(root, "behaviorBoard:\n  idle:\n    portName: null\n", name => name == "BehaviorBoard-Idle.Data" ? typeof(Bonsai.Harp.HarpMessage) : null);
                Check("a board with portName: null is not streamed, though its data is HarpMessage", !idle.Streamed.Any() && idle.Skipped.Any(s => s.StartsWith("BehaviorBoard-Idle: it has no portName")), string.Join(" | ", idle.Skipped));
                // Pip, 11 Nov 2026: the Lickety Splits and stepper drivers drawn under the board.
                var harp = Plan(root, "licketySplit:\n  left:\n    portName: COM15\n  right:\n    portName: null\n" +
                                      "stepperDriver:\n  a:\n    portName: COM14\n",
                    name => name.EndsWith(".Data") ? typeof(Bonsai.Harp.HarpMessage) : null);
                var harpStreams = string.Join(", ", harp.Streamed.SelectMany(d => d.Streams));
                Check("a Lickety Split and a stepper driver each stream one .channels",
                    harpStreams == "LicketySplit-Left.channels, StepperDriver-A.channels", harpStreams);
                Check("a Lickety Split with portName: null is not streamed, as a board",
                    harp.Skipped.Any(s => s.StartsWith("LicketySplit-Right: it has no portName")), string.Join(" | ", harp.Skipped));
                var stepper = harp.Streamed.FirstOrDefault(d => d.Name == "StepperDriver-A");
                Check("a stepper driver's positions are sampled at 10/s, and its summary says what it sends",
                    stepper != null && stepper.TemplateValues()["Interval"] == "PT0.1S" && stepper.Summary.Contains("MotorStopped"),
                    stepper == null ? "not streamed" : stepper.TemplateValues()["Interval"] + " " + stepper.Summary);
                var notHarp = Plan(root, "licketySplit:\n  left:\n    portName: COM15\n", name => name == "LicketySplit-Left.Data" ? typeof(string) : null);
                Check("a Lickety Split whose data is not HarpMessage is skipped", notHarp.Skipped.Any(s => s.Contains("is not HarpMessage")), string.Join(" | ", notHarp.Skipped));
                // L2-L1: one empty instance hid its whole kind, without a word.
                var empty = Plan(root, "behaviorBoard:\n  main:\n    portName: COM3\n  spare:\ncamera:\n  box:\n    serialNumber: '1'\n",
                    name => name == "BehaviorBoard-Main.Data" ? typeof(Bonsai.Harp.HarpMessage) : name == "Camera-Box.Data" ? typeof(OpenCV.Net.IplImage) : null);
                Check("an empty entry beside a board is noted, naming it, and its kind is not streamed",
                    empty.Notes.Any(n => n == "the rig.yml entry behaviorBoard: spare: has no settings, so every behaviorBoard is passed over and none is streamed")
                    && string.Join(", ", empty.Streamed.Select(d => d.Name)) == "Camera-Box", string.Join(" | ", empty.Notes));
                // L2-L5: an entry under a device kind that is not a set of settings is named whatever it holds.
                var spares = Plan(root, "behaviorBoard:\n  main:\n    portName: COM3\n  spare: none\ncamera:\n  box:\n    serialNumber: '1'\n  off: off\n  list: []\n",
                    name => name == "BehaviorBoard-Main.Data" ? typeof(Bonsai.Harp.HarpMessage) : name == "Camera-Box.Data" ? typeof(OpenCV.Net.IplImage) : null);
                Check("a device kind's entry holding none, off or [] is named, with what it holds",
                    spares.Notes.Contains("the rig.yml entry behaviorBoard: spare: none has no settings, so every behaviorBoard is passed over and none is streamed")
                    && spares.Notes.Contains("the rig.yml entries camera: off: off, camera: list: [] have no settings, so every camera is passed over and none is streamed"), string.Join(" | ", spares.Notes));
                var notDevices = Plan(root, "speakerArray:\n  main:\n    channels: 2\n  syncChannel: 3\n", name => null);
                Check("a kind that is not a device, holding a value beside settings, is not named", !notDevices.Notes.Any(n => n.Contains("speakerArray")), string.Join(" | ", notDevices.Notes));
                // L2-L3: enabled in YamlDotNet's words, as app-rig-control's rig_yaml.py reads it.
                foreach (var word in new[] { "false", "False", "n", "No", "off", "OFF" })
                {
                    var disabled = Plan(root, "camera:\n  box:\n    liveView:\n      enabled: " + word + "\n", name => name == "Camera-Box.Data" ? typeof(OpenCV.Net.IplImage) : null);
                    Check("liveView enabled " + word + " is not streamed, unnoted", !disabled.Streamed.Any() && !disabled.Notes.Any(), string.Join(" | ", disabled.Notes));
                }
                foreach (var word in new[] { "true", "TRUE", "y", "Yes", "on", "On" })
                {
                    var enabled = Plan(root, "camera:\n  box:\n    liveView:\n      enabled: " + word + "\n", name => name == "Camera-Box.Data" ? typeof(OpenCV.Net.IplImage) : null);
                    Check("liveView enabled " + word + " is streamed, unnoted", enabled.Streamed.Count == 1 && !enabled.Notes.Any(), string.Join(" | ", enabled.Notes));
                }
                var maybe = Plan(root, "camera:\n  box:\n    liveView:\n      enabled: maybe\n", name => name == "Camera-Box.Data" ? typeof(OpenCV.Net.IplImage) : null);
                Check("liveView enabled maybe is noted and streamed", maybe.Streamed.Count == 1 && maybe.Notes.Any(n => n.Contains("enabled 'maybe'")), string.Join(" | ", maybe.Notes));
                var none = LiveViewPlan.Create(Path.Combine(root, "nowhere"), name => null);
                Check("no rig.yml streams nothing, with a note, and does not throw", none.Streamed.Count == 0 && none.Notes.Any(n => n.StartsWith("no rig.yml at")), string.Join(" | ", none.Notes));

                Console.WriteLine("Temporary devices, in no rig.yml:");
                var also = LiveViewPlan.Create(Path.Combine(root, "nowhere"),
                    name => name == "Camera-Temporary.Data" ? typeof(OpenCV.Net.IplImage) : null);
                var extra = also.Streamed.SingleOrDefault();
                Check("a published Camera-Temporary.Data is streamed by the camera recipe, with no rig.yml",
                    extra != null && extra.Name == "Camera-Temporary" && extra.Rate == 5 && extra.ImagePath == "it", extra == null ? string.Join(" | ", also.Skipped) : extra.ToString());
                Check("a temporary device not published is not mentioned", !none.Skipped.Any(s => s.Contains("Temporary")), string.Join(" | ", none.Skipped));
                var withRig = Plan(root, "camera:\n  box:\n    serialNumber: '1'\n",
                    name => name == "Camera-Temporary.Data" || name == "Camera-Box.Data" ? typeof(OpenCV.Net.IplImage) : null);
                var both = string.Join(", ", withRig.Streamed.Select(d => d.Stream));
                Check("a temporary camera streams beside rig.yml's", both == "Camera-Temporary.video, Camera-Box.video", both);
                Check("a temporary device changes the plan's signature", also.Signature(5556, 5558) != none.Signature(5556, 5558), "same signature");

                Console.WriteLine("Board channels:");
                var once = new TagRegister { Register = "OutputSet" }.Process(new[] { 5 }.ToObservable()).ToList().Wait();
                Check("an event register is named and sent once", once.Count == 1 && once[0].Register == "OutputSet" && (int)once[0].Value == 5, once.Count.ToString());
                var held = new TagRegister { Register = "OutputState", Repeat = TimeSpan.FromMilliseconds(50) }
                    .Process(Observable.Return(7).Concat(Observable.Never<int>())).Take(4).ToList().Timeout(TimeSpan.FromSeconds(5)).Wait();
                Check("a state register's latest value is sent again every Repeat, unchanged",
                    held.Count == 4 && held.All(m => m.Register == "OutputState" && (int)m.Value == 7), held.Count.ToString());
                var tag = new TagRegister { RepeatXml = "PT2S" };
                Check("Repeat is read from the workflow file's duration", tag.Repeat == TimeSpan.FromSeconds(2), tag.Repeat.ToString());

                Console.WriteLine("Session key:");
                Check("the experiment folder's last two names", SessionKey.FromPathPrefix(@"C:\data\sub-TC002\ses-003B1_2026-10-30T19-57-12\") == "sub-TC002/ses-003B1_2026-10-30T19-57-12", SessionKey.FromPathPrefix(@"C:\data\sub-TC002\ses-003B1_2026-10-30T19-57-12\"));
                var refused = false;
                try { SessionKey.FromPathPrefix(@"C:\data\TC002\003B1"); } catch (ArgumentException) { refused = true; }
                Check("a folder not named sub-/ses- is refused", refused, "taken");

                Console.WriteLine("The workflow's path, as the manifest has it:");
                var repo = Path.Combine(root, "repo");
                var src = Path.Combine(repo, "src");
                Directory.CreateDirectory(src);
                File.WriteAllText(Path.Combine(src, "main.bonsai"), "");
                // Bonsai was started from the repository with src\main.bonsai, then works from src.
                var found = OpenRigs.Shared.WorkflowFile.FullPath(@"src\main.bonsai", src);
                Check("a relative path given at launch is found from the workflow's folder", found == Path.Combine(src, "main.bonsai"), found);
                Check("an absolute one is kept", OpenRigs.Shared.WorkflowFile.FullPath(Path.Combine(src, "main.bonsai"), root) == Path.Combine(src, "main.bonsai"), "changed");
                // Round 5: the live view read every argument, the manifest only the person's. Both now
                // compile in src\Shared\WorkflowFile.cs; Bonsai's bootstrapper adds its own after --no-boot.
                var relaunched = OpenRigs.Shared.WorkflowFile.Find(new[] { "Bonsai.exe", "--no-editor", "--no-boot", @"C:\other\x.bonsai", "--@pipe", "p" }, src);
                Check("a workflow named only after the bootstrapper's --no-boot is not the one launched", relaunched == null, relaunched);
                // RecordHarpDevices' default; a StreamDevices left unset streamed nothing (set C, S4-L2).
                var settings = new StreamDevicesSettings();
                Check("ConfigRoot defaults to the folder app-rig-setup keeps each rig.yml under", settings.ConfigRoot == @"C:\Coen-Lab-Local\RigConfigs", settings.ConfigRoot);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
            Console.WriteLine(failed == 0 ? "All checks passed." : failed + " check(s) failed.");
            return failed == 0 ? 0 : 1;
        }
    }
}
