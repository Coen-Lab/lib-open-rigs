using System;
using System.IO;
using System.Linq;
using OpenRigs.LiveView;

// Stand-ins for the device data types, which the recipes recognise by full name, so the plan can
// be tested without Bonsai.Harp or OpenCV.Net installed.
namespace Bonsai.Harp { public class HarpMessage { } }
namespace OpenCV.Net { public class IplImage { } }

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
                var box = plan.Streamed.FirstOrDefault(d => d.Name == "Camera-Box");
                Check("a camera's image is found by type, in a frame paired with its trigger", box != null && box.ImagePath == "Value.Image", box == null ? "not streamed" : box.ImagePath);
                Check("rig.yml's liveView rate and width are taken, the height kept", box != null && box.Rate == 10 && box.Width == 160 && box.Height == 240, box == null ? "" : box.ToString());
                Check("a device the workflow does not publish is skipped, saying so", plan.Skipped.Any(s => s.StartsWith("Camera-Face: the workflow publishes no Camera-Face.Data")), string.Join(" | ", plan.Skipped));
                Check("liveView enabled false is skipped", plan.Skipped.Any(s => s.StartsWith("Camera-Side: liveView enabled is false")), string.Join(" | ", plan.Skipped));
                Check("a kind with no recipe is skipped", plan.Skipped.Any(s => s.StartsWith("Lickometer-Left: no live-view recipe")), string.Join(" | ", plan.Skipped));
                Check("settings that are not devices (rootPath, version) are not devices", !plan.Skipped.Any(s => s.StartsWith("RootPath") || s.StartsWith("Version")), string.Join(" | ", plan.Skipped));
                Check("a string PathPrefix gives the session key and elapsed", plan.HasSessionFolder, "no session folder");
                Check("nothing to note in a good rig.yml", plan.Notes.Count == 0, string.Join(" | ", plan.Notes));

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
                var none = LiveViewPlan.Create(Path.Combine(root, "nowhere"), name => null);
                Check("no rig.yml streams nothing, with a note, and does not throw", none.Streamed.Count == 0 && none.Notes.Any(n => n.StartsWith("no rig.yml at")), string.Join(" | ", none.Notes));

                Console.WriteLine("Session key:");
                Check("the session folder's last two names", SessionKey.FromPathPrefix(@"C:\data\sub-TC002\ses-003B1_2026-10-30T19-57-12\") == "sub-TC002/ses-003B1_2026-10-30T19-57-12", SessionKey.FromPathPrefix(@"C:\data\sub-TC002\ses-003B1_2026-10-30T19-57-12\"));
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
                Check("ConfigRoot defaults to the folder rig-setup keeps each rig.yml under", settings.ConfigRoot == @"C:\Coen-Lab-Local\RigConfigs", settings.ConfigRoot);
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
