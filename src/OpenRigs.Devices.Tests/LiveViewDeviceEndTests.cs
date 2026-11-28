using Bonsai;
using Bonsai.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetMQ;
using NetMQ.Sockets;
using OpenCV.Net;
using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;

namespace OpenRigs.Devices.Tests
{
    /// <summary>
    /// A device whose data ends leaves the other devices streamed: on a rig whose rig.yml names zapit.device,
    /// StreamDevices streams Camera-Zapit, whose data ends at once in a session that opens no Zapit. Its end must not reach
    /// the shared VideoMessage and close it, or no camera's video is sent. The same for the data port: a board whose data ends
    /// must not close DataMessage for the other streams. Run here, on ports of their own, with stand-in devices.
    /// </summary>
    [TestClass]
    public class LiveViewDeviceEndTests
    {
        static readonly string Folder = Path.Combine(Path.GetDirectoryName(typeof(LiveViewDeviceEndTests).Assembly.Location), "Pieces");

        [TestMethod]
        public void ACameraWhoseDataEndsLeavesTheOtherCamerasVideoSent()
        {
            WithRig(root => AssertSeen(root, true, "Camera-Balance.video", w =>
            {
                Publish(w, "Camera-Zapit.Data", new StandInCamera { Ends = true });
                Publish(w, "Camera-Balance.Data", new StandInCamera());
            }));
        }

        [TestMethod]
        public void BoardsWhoseDataEndsLeaveTheOtherDataStreamed()
        {
            WithRig(root => AssertSeen(root, false, "elapsed", w =>
            {
                Publish(w, "BehaviorBoard-Main.Data", new EndingBoard());
                Publish(w, "StepperDriver-A.Data", new EndingBoard());
                Publish(w, "LicketySplit-Left.Data", new EndingBoard());
                Publish(w, "PathPrefix", new StandInPathPrefix(), latest: true);   // never ends: the elapsed stream goes on
            }));
        }

        // A rig.yml that names zapit.device, in a config root of its own; the root is removed after.
        static void WithRig(Action<string> run)
        {
            var root = Path.Combine(Path.GetTempPath(), "LiveViewDeviceEndTests", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, Environment.MachineName));
                var rig = File.ReadAllText(Path.Combine(Folder, "rig.yml")).Replace("\r\n", "\n");
                var at = rig.IndexOf("\nzapit:\n", StringComparison.Ordinal);
                Assert.IsTrue(at >= 0, "Pieces\\rig.yml has no zapit block");
                File.WriteAllText(Path.Combine(root, Environment.MachineName, "rig.yml"), rig.Insert(at + "\nzapit:\n".Length, "  device: Dev1\n"));
                run(root);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        // The stand-ins, published as subjects ahead of StreamDevices, as a task workflow has its devices.
        static ExpressionBuilderGraph Workflow(string root, int dataPort, int videoPort, Action<ExpressionBuilderGraph> publish)
        {
            var workflow = new ExpressionBuilderGraph();
            var mapping = new ExternalizedMappingBuilder();
            mapping.ExternalizedProperties.Add(new ExternalizedMapping { Name = "ConfigRoot" });
            var readLabRig = new IncludeWorkflowBuilder { Path = "OpenRigs.Devices:ReadLabRig.bonsai" };
            var range = readLabRig.ArgumentRange;  // loads it, so its ConfigRoot can be set
            workflow.AddEdge(workflow.Add(mapping), workflow.Add(readLabRig), new ExpressionBuilderArgument(0));
            publish(workflow);
            workflow.Add(new OpenRigs.LiveView.StreamDevices { DataPort = dataPort, VideoPort = videoPort });
            workflow.SetWorkflowProperty("ConfigRoot", root);
            return workflow;
        }

        // A PublishSubject, or with latest a BehaviorSubject, so a subscriber that comes late still gets the last value.
        static void Publish<T>(ExpressionBuilderGraph workflow, string subject, Source<T> source, bool latest = false)
        {
            var declared = latest ? (ExpressionBuilder)new Bonsai.Reactive.BehaviorSubject { Name = subject } : new Bonsai.Reactive.PublishSubject { Name = subject };
            workflow.AddEdge(workflow.Add(new CombinatorBuilder { Combinator = source }), workflow.Add(declared), new ExpressionBuilderArgument(0));
        }

        // Runs the stand-ins' workflow until a message on its video (or data) port names the stream, or fails after 15 s or
        // when the workflow stops. A port found free can be taken before StreamDevices binds it: then new ports, 3 tries.
        static void AssertSeen(string root, bool onVideo, string stream, Action<ExpressionBuilderGraph> publish)
        {
            for (var attempt = 1; ; attempt++)
            {
                int dataPort = TestPorts.Free(), videoPort = TestPorts.Free();
                var port = onVideo ? videoPort : dataPort;
                Exception stopped = null;
                var last = string.Empty;
                using (var subscriber = new SubscriberSocket())
                using (Workflow(root, dataPort, videoPort, publish).BuildObservable().Subscribe(_ => { }, e => stopped = e))
                {
                    subscriber.Connect("tcp://127.0.0.1:" + port);
                    subscriber.SubscribeToAnyTopic();
                    var until = DateTime.UtcNow.AddSeconds(15);
                    while (DateTime.UtcNow < until && stopped == null)
                    {
                        NetMQMessage message = null;
                        if (!subscriber.TryReceiveMultipartMessage(TimeSpan.FromMilliseconds(200), ref message)) continue;
                        last = string.Join(" ", message.Select(frame => Encoding.UTF8.GetString(frame.ToByteArray())));
                        if (last.Contains(stream)) return;
                    }
                }
                if (attempt < 3 && TestPorts.Taken(stopped)) continue;
                Assert.IsNull(stopped, "the workflow stopped: " + stopped);
                Assert.Fail("no " + stream + " message in 15 s on port " + port + (last.Length > 0 ? "; last: " + last.Substring(0, Math.Min(200, last.Length)) : ""));
            }
        }
    }

    /// <summary>A camera's data on a desk: a small black frame every 50 ms, or, when Ends, none and an end at once.</summary>
    public class StandInCamera : Source<IplImage>
    {
        /// <summary>Whether the data ends at once, as Camera-Zapit.Data does in a session that opens no Zapit.</summary>
        public bool Ends { get; set; }

        /// <summary>The frames, or an end at once.</summary>
        /// <returns>A black 64 x 48 frame every 50 ms, or an empty sequence.</returns>
        public override IObservable<IplImage> Generate()
        {
            if (Ends) return Observable.Empty<IplImage>();
            return Observable.Interval(TimeSpan.FromMilliseconds(50)).Select(_ =>
            {
                var frame = new IplImage(new Size(64, 48), IplDepth.U8, 3);
                frame.SetZero();
                return frame;
            });
        }
    }

    /// <summary>A Harp board's data on a desk that ends at once, as a board's does when its device closes.</summary>
    public class EndingBoard : Source<Bonsai.Harp.HarpMessage>
    {
        /// <summary>An empty sequence.</summary>
        public override IObservable<Bonsai.Harp.HarpMessage> Generate() => Observable.Empty<Bonsai.Harp.HarpMessage>();
    }

    /// <summary>The experiment folder's name, sent once and never ended, as a running session's PathPrefix is.</summary>
    public class StandInPathPrefix : Source<string>
    {
        /// <summary>One folder name at once (published as a BehaviorSubject, so a later subscriber gets it), then nothing.</summary>
        public override IObservable<string> Generate() => Observable.Return(Path.Combine("data", "sub-1", "ses-1_20261128")).Concat(Observable.Never<string>());
    }
}
