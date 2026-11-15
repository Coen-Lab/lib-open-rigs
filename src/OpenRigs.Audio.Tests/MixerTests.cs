using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reactive.Subjects;
using System.Threading;
using System.Xml;
using Bonsai;
using Bonsai.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Audio.Tests
{
    [TestClass]
    public class MixerTests
    {
        [TestMethod]
        public void ASoundBeforeTheMixerStopsTheWorkflowNamingItAndTheBootDelay()
        {
            var sounds = new Subject<int>();
            var mixer = new Subject<string>();
            var passed = new List<int>();
            Exception error = null;
            using (new CheckMixerStarted { Sound = "PenaltySounds" }.Process(sounds, mixer).Subscribe(passed.Add, ex => error = ex))
            {
                sounds.OnNext(1);
                mixer.OnNext("mixer");
                sounds.OnNext(2);
            }
            Assert.AreEqual(0, passed.Count);
            Assert.IsInstanceOfType(error, typeof(InvalidOperationException));
            StringAssert.Contains(error.Message, "sent on PenaltySounds");
            StringAssert.Contains(error.Message, "before the speaker mixer started (3 s after Start, or rig.yml's speakerArray.device.bootDelay if longer)");
        }

        [TestMethod]
        public void ASoundAfterTheMixerStartedIsPassedOn()
        {
            var sounds = new Subject<int>();
            var mixer = new Subject<string>();
            var passed = new List<int>();
            Exception error = null;
            using (new CheckMixerStarted { Sound = "StimulusSounds" }.Process(sounds, mixer).Subscribe(passed.Add, ex => error = ex))
            {
                mixer.OnNext("mixer");
                mixer.OnCompleted();
                sounds.OnNext(1);
                sounds.OnNext(2);
            }
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[] { 1, 2 }, passed);
        }

        // SpeakerMixer's path for StimulusSounds, with a timer for the mixer and SubscribeWhen for AppendBuffer (which
        // subscribes once the mixer starts), fed as a two-choice task feeds it: each trial a SelectMany whose
        // Take(1) multicasts one sound into the BehaviorSubject, and completes.
        const string Workflow =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<WorkflowBuilder Version=\"2.9.0\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"" +
            " xmlns:rx=\"clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core\" xmlns:sys=\"clr-namespace:System;assembly=mscorlib\"" +
            " xmlns:aud=\"clr-namespace:OpenRigs.Audio;assembly=OpenRigs.Audio\" xmlns=\"https://bonsai-rx.org/2018/workflow\">\n<Workflow><Nodes>" +
            "<Expression xsi:type=\"rx:BehaviorSubject\" TypeArguments=\"sys:Int64\"><rx:Name>StimulusSounds</rx:Name></Expression>" +
            "<Expression xsi:type=\"Combinator\"><Combinator xsi:type=\"rx:Timer\"><rx:DueTime>MIXER</rx:DueTime><rx:Period>PT0S</rx:Period></Combinator></Expression>" +
            "<Expression xsi:type=\"rx:BehaviorSubject\"><Name>Mixer</Name></Expression>" +
            "<Expression xsi:type=\"SubscribeSubject\"><Name>StimulusSounds</Name></Expression>" +
            "<Expression xsi:type=\"SubscribeSubject\"><Name>Mixer</Name></Expression>" +
            "<Expression xsi:type=\"Combinator\"><Combinator xsi:type=\"aud:CheckMixerStarted\"><aud:Sound>StimulusSounds</aud:Sound></Combinator></Expression>" +
            "<Expression xsi:type=\"rx:PublishSubject\"><Name>MixerStimulusSounds</Name></Expression>" +
            "<Expression xsi:type=\"SubscribeSubject\"><Name>MixerStimulusSounds</Name></Expression>" +
            "<Expression xsi:type=\"SubscribeSubject\"><Name>Mixer</Name></Expression>" +
            "<Expression xsi:type=\"Combinator\"><Combinator xsi:type=\"rx:SubscribeWhen\" /></Expression>" +
            "<Expression xsi:type=\"WorkflowOutput\" />" +
            "<Expression xsi:type=\"Combinator\"><Combinator xsi:type=\"rx:Timer\"><rx:DueTime>TRIALS</rx:DueTime><rx:Period>PT0.1S</rx:Period></Combinator></Expression>" +
            "<Expression xsi:type=\"rx:SelectMany\"><Name>PlayStim</Name><Workflow><Nodes>" +
            "<Expression xsi:type=\"WorkflowInput\"><Name>Source1</Name></Expression>" +
            "<Expression xsi:type=\"Combinator\"><Combinator xsi:type=\"rx:Take\"><rx:Count>1</rx:Count></Combinator></Expression>" +
            "<Expression xsi:type=\"MulticastSubject\"><Name>StimulusSounds</Name></Expression>" +
            "<Expression xsi:type=\"WorkflowOutput\" />" +
            "</Nodes><Edges><Edge From=\"0\" To=\"1\" Label=\"Source1\" /><Edge From=\"1\" To=\"2\" Label=\"Source1\" /><Edge From=\"2\" To=\"3\" Label=\"Source1\" /></Edges></Workflow></Expression>" +
            "</Nodes><Edges>" +
            "<Edge From=\"1\" To=\"2\" Label=\"Source1\" />" +
            "<Edge From=\"3\" To=\"5\" Label=\"Source1\" /><Edge From=\"4\" To=\"5\" Label=\"Source2\" /><Edge From=\"5\" To=\"6\" Label=\"Source1\" />" +
            "<Edge From=\"7\" To=\"9\" Label=\"Source1\" /><Edge From=\"8\" To=\"9\" Label=\"Source2\" /><Edge From=\"9\" To=\"10\" Label=\"Source1\" />" +
            "<Edge From=\"11\" To=\"12\" Label=\"Source1\" />" +
            "</Edges></Workflow>\n</WorkflowBuilder>\n";

        // Runs the workflow until it has played `count` sounds, stopped or `seconds` passed.
        static Tuple<List<long>, Exception> Run(string mixerDueTime, string trialsDueTime, int count, double seconds)
        {
            WorkflowBuilder builder;
            var xml = Workflow.Replace("MIXER", mixerDueTime).Replace("TRIALS", trialsDueTime);
            using (var reader = XmlReader.Create(new StringReader(xml))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            var run = Expression.Lambda<Func<IObservable<long>>>(builder.Workflow.Build()).Compile()();
            var played = new List<long>();
            Exception error = null;
            using (var done = new ManualResetEventSlim())
            using (run.Subscribe(
                sound => { lock (played) { played.Add(sound); if (played.Count >= count) done.Set(); } },
                ex => { error = ex; done.Set(); },
                done.Set))
            {
                done.Wait(TimeSpan.FromSeconds(seconds));
            }
            lock (played) return Tuple.Create(new List<long>(played), error);
        }

        [TestMethod]
        public void EachTrialsSoundFromASelectManyTakeReachesTheMixerAfterTrialOne()
        {
            // A PublishSubject fed straight by the per-trial multicast would close at trial 1 (seen on a rig).
            var result = Run("PT0.2S", "PT0.4S", 4, 10);
            Assert.IsNull(result.Item2, result.Item2 == null ? null : result.Item2.Message);
            CollectionAssert.AreEqual(new long[] { 0, 1, 2, 3 }, result.Item1);
        }

        [TestMethod]
        public void ATrialsSoundWhileTheMixerBootsStopsTheWorkflow()
        {
            var result = Run("PT2S", "PT0.1S", 1, 10);
            Assert.AreEqual(0, result.Item1.Count);
            Assert.IsNotNull(result.Item2, "the workflow did not stop");
            var error = result.Item2;
            while (error.InnerException != null) error = error.InnerException;
            StringAssert.Contains(error.Message, "sent on StimulusSounds");
            StringAssert.Contains(error.Message, "speakerArray.device.bootDelay");
        }
    }
}
