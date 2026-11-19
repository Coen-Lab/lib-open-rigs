using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using Bonsai;
using Bonsai.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// Stand-ins for Bonsai.Mixer's types without a sound card, with the same names, properties and shapes: CreateSource and
// AppendBuffer are SelectMany over their sources, StopSource a Do, and a source plays its buffers in the order appended.
namespace OpenRigs.Audio.Tests.Mixer
{
    public class MixerSource
    {
        public readonly List<long> Buffers = new List<long>();
        public readonly List<double> Stops = new List<double>();
        public float Gain;

        public void Append(long buffer)
        {
            if (Stops.Count > 0) throw new InvalidOperationException("a buffer appended to a stopped source");
            Buffers.Add(buffer);
        }

        public void Stop(double fadeDuration) { Stops.Add(fadeDuration); }
    }

    public class MixerContext
    {
        public static readonly List<MixerSource> Sources = new List<MixerSource>();

        public MixerSource CreateSource(bool looping, bool playing, float initialGain)
        {
            var source = new MixerSource { Gain = initialGain };
            lock (Sources) Sources.Add(source);
            return source;
        }
    }

    /// <summary>The mixer, started after DueTimeSeconds.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class StartMixer
    {
        public double DueTimeSeconds { get; set; }

        public IObservable<MixerContext> Process() { return Observable.Timer(TimeSpan.FromSeconds(DueTimeSeconds)).Select(_ => new MixerContext()); }
    }

    [Combinator]
    public class CreateSource
    {
        public bool Looping { get; set; }
        public bool Playing { get; set; }
        public float Gain { get; set; }

        public IObservable<MixerSource> Process(IObservable<MixerContext> source)
        {
            return source.SelectMany(mixer => Observable.Create<MixerSource>(observer =>
            {
                var context = mixer.CreateSource(Looping, Playing, Gain);
                observer.OnNext(context);
                return Disposable.Create(() => context.Stop(0));
            }));
        }
    }

    [Combinator]
    public class AppendBuffer
    {
        public IObservable<long> Process(IObservable<long> source, IObservable<MixerSource> mixerSource)
        {
            return mixerSource.SelectMany(context => source.Do(context.Append));
        }
    }

    public class StopSource : Sink<MixerSource>
    {
        public double FadeDuration { get; set; }

        public override IObservable<MixerSource> Process(IObservable<MixerSource> source)
        {
            return source.Do(context => context.Stop(FadeDuration));
        }
    }
}

namespace OpenRigs.Audio.Tests
{
    [TestClass]
    public class StopStimulusSoundsTests
    {
        // SpeakerMixer's stimulus path and its StopStimulusSounds (nodes 45 on), on the stand-in mixer. Each trial's sound
        // and stop is sent as a two-choice task sends one: a SelectMany whose Take(1) multicasts it, and completes.
        const string Workflow = @"<?xml version=""1.0"" encoding=""utf-8""?>
<WorkflowBuilder Version=""2.9.0"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
  xmlns:rx=""clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core"" xmlns:sys=""clr-namespace:System;assembly=mscorlib""
  xmlns:scr=""clr-namespace:Bonsai.Scripting.Expressions;assembly=Bonsai.Scripting.Expressions""
  xmlns:mix=""clr-namespace:OpenRigs.Audio.Tests.Mixer;assembly=OpenRigs.Audio.Tests""
  xmlns:aud=""clr-namespace:OpenRigs.Audio;assembly=OpenRigs.Audio"" xmlns=""https://bonsai-rx.org/2018/workflow"">
<Workflow><Nodes>
  <Expression xsi:type=""rx:BehaviorSubject"" TypeArguments=""sys:Int64""><rx:Name>StimulusSounds</rx:Name></Expression>
  <Expression xsi:type=""rx:BehaviorSubject"" TypeArguments=""sys:Object""><rx:Name>StopStimulusSounds</rx:Name></Expression>
  <Expression xsi:type=""Combinator""><Combinator xsi:type=""mix:StartMixer""><mix:DueTimeSeconds>0.25</mix:DueTimeSeconds></Combinator></Expression>
  <Expression xsi:type=""rx:BehaviorSubject""><Name>Mixer</Name></Expression>
  <Expression xsi:type=""SubscribeSubject""><Name>StimulusSounds</Name></Expression>
  <Expression xsi:type=""SubscribeSubject""><Name>Mixer</Name></Expression>
  <Expression xsi:type=""Combinator""><Combinator xsi:type=""aud:CheckMixerStarted""><aud:Sound>StimulusSounds</aud:Sound></Combinator></Expression>
  <Expression xsi:type=""rx:PublishSubject""><Name>MixerStimulusSounds</Name></Expression>
  <Expression xsi:type=""SubscribeSubject""><Name>Mixer</Name></Expression>
  <Expression xsi:type=""Combinator""><Combinator xsi:type=""mix:CreateSource""><mix:Looping>false</mix:Looping><mix:Playing>true</mix:Playing><mix:Gain>1</mix:Gain></Combinator></Expression>
  <Expression xsi:type=""rx:BehaviorSubject""><Name>StimulusSource</Name></Expression>
  <Expression xsi:type=""SubscribeSubject""><Name>MixerStimulusSounds</Name></Expression>
  <Expression xsi:type=""rx:SelectMany""><Name>AppendStimulusSound</Name><Workflow><Nodes>
    <Expression xsi:type=""WorkflowInput""><Name>Source1</Name></Expression>
    <Expression xsi:type=""SubscribeSubject""><Name>StimulusSource</Name></Expression>
    <Expression xsi:type=""Combinator""><Combinator xsi:type=""rx:Take""><rx:Count>1</rx:Count></Combinator></Expression>
    <Expression xsi:type=""Combinator""><Combinator xsi:type=""mix:AppendBuffer"" /></Expression>
    <Expression xsi:type=""WorkflowOutput"" />
  </Nodes><Edges><Edge From=""0"" To=""3"" Label=""Source1"" /><Edge From=""1"" To=""2"" Label=""Source1"" /><Edge From=""2"" To=""3"" Label=""Source2"" /><Edge From=""3"" To=""4"" Label=""Source1"" /></Edges></Workflow></Expression>
  <Expression xsi:type=""SubscribeSubject""><Name>StopStimulusSounds</Name></Expression>
  <Expression xsi:type=""SubscribeSubject""><Name>StimulusSource</Name></Expression>
  <Expression xsi:type=""Combinator""><Combinator xsi:type=""rx:WithLatestFrom"" /></Expression>
  <Expression xsi:type=""MemberSelector""><Selector>Item2</Selector></Expression>
  <Expression xsi:type=""Combinator""><Combinator xsi:type=""mix:StopSource""><mix:FadeDuration>0.005</mix:FadeDuration></Combinator></Expression>
  <Expression xsi:type=""SubscribeSubject""><Name>Mixer</Name></Expression>
  <Expression xsi:type=""Combinator""><Combinator xsi:type=""rx:WithLatestFrom"" /></Expression>
  <Expression xsi:type=""MemberSelector""><Selector>Item2</Selector></Expression>
  <Expression xsi:type=""scr:ExpressionTransform""><scr:Expression>CreateSource(false, true, 1)</scr:Expression></Expression>
  <Expression xsi:type=""MulticastSubject""><Name>StimulusSource</Name></Expression>
  <Expression xsi:type=""Combinator""><Combinator xsi:type=""rx:Timer""><rx:DueTime>PT0.4S</rx:DueTime><rx:Period>PT0.2S</rx:Period></Combinator></Expression>
  <Expression xsi:type=""rx:SelectMany""><Name>PlayStim</Name><Workflow><Nodes>
    <Expression xsi:type=""WorkflowInput""><Name>Source1</Name></Expression>
    <Expression xsi:type=""Combinator""><Combinator xsi:type=""rx:Take""><rx:Count>1</rx:Count></Combinator></Expression>
    <Expression xsi:type=""MulticastSubject""><Name>StimulusSounds</Name></Expression>
    <Expression xsi:type=""WorkflowOutput"" />
  </Nodes><Edges><Edge From=""0"" To=""1"" Label=""Source1"" /><Edge From=""1"" To=""2"" Label=""Source1"" /><Edge From=""2"" To=""3"" Label=""Source1"" /></Edges></Workflow></Expression>
  <Expression xsi:type=""WorkflowOutput"" />
  <Expression xsi:type=""Combinator""><Combinator xsi:type=""rx:Timer""><rx:DueTime>STOPS</rx:DueTime><rx:Period>PT0.4S</rx:Period></Combinator></Expression>
  <Expression xsi:type=""rx:SelectMany""><Name>StopStim</Name><Workflow><Nodes>
    <Expression xsi:type=""WorkflowInput""><Name>Source1</Name></Expression>
    <Expression xsi:type=""Combinator""><Combinator xsi:type=""rx:Take""><rx:Count>1</rx:Count></Combinator></Expression>
    <Expression xsi:type=""MulticastSubject""><Name>StopStimulusSounds</Name></Expression>
    <Expression xsi:type=""WorkflowOutput"" />
  </Nodes><Edges><Edge From=""0"" To=""1"" Label=""Source1"" /><Edge From=""1"" To=""2"" Label=""Source1"" /><Edge From=""2"" To=""3"" Label=""Source1"" /></Edges></Workflow></Expression>
</Nodes><Edges>
  <Edge From=""2"" To=""3"" Label=""Source1"" />
  <Edge From=""4"" To=""6"" Label=""Source1"" /><Edge From=""5"" To=""6"" Label=""Source2"" /><Edge From=""6"" To=""7"" Label=""Source1"" />
  <Edge From=""8"" To=""9"" Label=""Source1"" /><Edge From=""9"" To=""10"" Label=""Source1"" /><Edge From=""11"" To=""12"" Label=""Source1"" />
  <Edge From=""13"" To=""15"" Label=""Source1"" /><Edge From=""14"" To=""15"" Label=""Source2"" /><Edge From=""15"" To=""16"" Label=""Source1"" />
  <Edge From=""16"" To=""17"" Label=""Source1"" /><Edge From=""17"" To=""19"" Label=""Source1"" /><Edge From=""18"" To=""19"" Label=""Source2"" />
  <Edge From=""19"" To=""20"" Label=""Source1"" /><Edge From=""20"" To=""21"" Label=""Source1"" /><Edge From=""21"" To=""22"" Label=""Source1"" />
  <Edge From=""23"" To=""24"" Label=""Source1"" /><Edge From=""24"" To=""25"" Label=""Source1"" />
  <Edge From=""26"" To=""27"" Label=""Source1"" />
</Edges></Workflow>
</WorkflowBuilder>
";

        // Sounds 0-3 at 0.4, 0.6, 0.8 and 1.0 s, the mixer at 0.25 s and stops from `stops` every 0.4 s; each source's
        // buffers and stops at 1.2 s, before the workflow is disposed.
        static List<Mixer.MixerSource> Run(string stops)
        {
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(Workflow.Replace("STOPS", stops)))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            var run = Expression.Lambda<Func<IObservable<long>>>(builder.Workflow.Build()).Compile()();
            lock (Mixer.MixerContext.Sources) Mixer.MixerContext.Sources.Clear();
            Exception error = null;
            using (run.Subscribe(_ => { }, ex => error = ex))
            {
                Thread.Sleep(1200);
                Assert.IsNull(error, error == null ? null : error.ToString());
                // Copies, as the workflow's disposal stops CreateSource's source.
                lock (Mixer.MixerContext.Sources) return Mixer.MixerContext.Sources.Select(s =>
                {
                    var copy = new Mixer.MixerSource { Gain = s.Gain };
                    copy.Buffers.AddRange(s.Buffers);
                    copy.Stops.AddRange(s.Stops);
                    return copy;
                }).ToList();
            }
        }

        [TestMethod]
        public void AStopFadesTheSoundOutOver5MsAndTheNextPlaysOnAFreshSourceAtFullLevel()
        {
            // Stops at 0.1 s (before the mixer: nothing), 0.5 s (sound 0 playing) and 0.9 s (sound 2 playing, after 1).
            var sources = Run("PT0.1S");
            Assert.AreEqual(3, sources.Count, string.Join(" | ",sources.Select(s => string.Join(",", s.Buffers) + " / " + string.Join(",", s.Stops))));
            CollectionAssert.AreEqual(new long[] { 0 }, sources[0].Buffers);
            CollectionAssert.AreEqual(new[] { 0.005 }, sources[0].Stops);
            CollectionAssert.AreEqual(new long[] { 1, 2 }, sources[1].Buffers, "sounds sent between stops queue on one source");
            CollectionAssert.AreEqual(new[] { 0.005 }, sources[1].Stops);
            CollectionAssert.AreEqual(new long[] { 3 }, sources[2].Buffers);
            Assert.AreEqual(0, sources[2].Stops.Count);
            Assert.IsTrue(sources.All(s => s.Gain == 1));
        }

        [TestMethod]
        public void WithoutStopsEverySoundQueuesOnOneSource()
        {
            var sources = Run("PT1H");
            Assert.AreEqual(1, sources.Count);
            CollectionAssert.AreEqual(new long[] { 0, 1, 2, 3 }, sources[0].Buffers);
            Assert.AreEqual(0, sources[0].Stops.Count);
        }

        [TestMethod]
        public void SpeakerMixerHasTheStopPathTested()
        {
            string xml;
            using (var stream = typeof(SpeakerSet).Assembly.GetManifestResourceStream("OpenRigs.Audio.SpeakerMixer.bonsai"))
            using (var reader = new StreamReader(stream)) xml = reader.ReadToEnd();
            StringAssert.Matches(xml, new Regex("TypeArguments=\"sys:Object\">\\s*<rx:Name>StopStimulusSounds</rx:Name>"));
            StringAssert.Contains(xml, "<mix:FadeDuration>0.005</mix:FadeDuration>");
            StringAssert.Contains(xml, "<scr:Expression>CreateSource(false, true, 1)</scr:Expression>");
            StringAssert.Contains(xml, "<Name>AppendStimulusSound</Name>");        }
    }
}
