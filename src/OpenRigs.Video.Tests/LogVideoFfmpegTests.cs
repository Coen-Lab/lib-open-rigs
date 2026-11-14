using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Xml;
using Bonsai;
using Bonsai.Expressions;
using Bonsai.Harp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Video.Tests
{
    /// <summary>A frame source for building workflows in tests; it never runs.</summary>
    public class TestFrames : Source<Timestamped<VideoDataFrame>>
    {
        /// <summary>No frames.</summary>
        public override IObservable<Timestamped<VideoDataFrame>> Generate() => Observable.Empty<Timestamped<VideoDataFrame>>();
    }

    /// <summary>OpenRigs.Logging's LogVideoFfmpeg include, built (not run) as a workflow uses it in place of LogVideo.</summary>
    [TestClass]
    public class LogVideoFfmpegTests
    {
        const string Harness = @"<?xml version=""1.0"" encoding=""utf-8""?>
<WorkflowBuilder Version=""2.9.0""
                 xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
                 xmlns:rx=""clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core""
                 xmlns:t=""clr-namespace:OpenRigs.Video.Tests;assembly=OpenRigs.Video.Tests""
                 xmlns=""https://bonsai-rx.org/2018/workflow"">
  <Workflow>
    <Nodes>
      <Expression xsi:type=""Combinator""><Combinator xsi:type=""StringProperty""><Value>C:\Data\sub-1\ses-1</Value></Combinator></Expression>
      <Expression xsi:type=""rx:AsyncSubject""><Name>PathPrefix</Name></Expression>
      <Expression xsi:type=""Combinator""><Combinator xsi:type=""t:TestFrames"" /></Expression>
      <Expression xsi:type=""IncludeWorkflow"" Path=""OpenRigs.Logging:LogVideoFfmpeg.bonsai"">
        <LogName>Camera-Test</LogName>
        <FrameRate>40</FrameRate>
        <ConfigRoot>D:\Configs</ConfigRoot>
      </Expression>
    </Nodes>
    <Edges>
      <Edge From=""0"" To=""1"" Label=""Source1"" />
      <Edge From=""2"" To=""3"" Label=""Source1"" />
    </Edges>
  </Workflow>
</WorkflowBuilder>";

        [TestMethod]
        public void TakesWhatLogVideoTakesAndBuilds()
        {
            // Loaded here, so the include's Path resolves to the embedded workflow.
            Assert.IsNotNull(typeof(OpenRigs.Logging.LoggedRows).Assembly);
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(Harness))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            builder.Workflow.Build();  // throws on a type or a mapping the include gets wrong

            var include = (IncludeWorkflowBuilder)builder.Workflow.Select(node => node.Value).Last();
            var writer = include.Workflow.Descendants().OfType<CombinatorBuilder>().Select(c => c.Combinator).OfType<FfmpegVideoWriter>().Single();
            Assert.AreEqual(40, writer.FrameRate);
            Assert.AreEqual(@"D:\Configs", writer.ConfigRoot);
        }

        // The frames as OpenRigs.Devices' TriggeredSpinnaker include gives them, and so as every
        // workflow's LogVideo takes them: a dynamic type its ExpressionTransform makes, not a
        // VideoDataFrame (the first rig run of LogVideoFfmpeg failed to build on this, 14 Nov 2026).
        const string SpinnakerHarness = @"<?xml version=""1.0"" encoding=""utf-8""?>
<WorkflowBuilder Version=""2.9.0""
                 xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
                 xmlns:rx=""clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core""
                 xmlns:scr=""clr-namespace:Bonsai.Scripting.Expressions;assembly=Bonsai.Scripting.Expressions""
                 xmlns:harp=""clr-namespace:Bonsai.Harp;assembly=Bonsai.Harp""
                 xmlns:t=""clr-namespace:OpenRigs.Video.Tests;assembly=OpenRigs.Video.Tests""
                 xmlns=""https://bonsai-rx.org/2018/workflow"">
  <Workflow>
    <Nodes>
      <Expression xsi:type=""Combinator""><Combinator xsi:type=""StringProperty""><Value>C:\Data\sub-1\ses-1</Value></Combinator></Expression>
      <Expression xsi:type=""rx:AsyncSubject""><Name>PathPrefix</Name></Expression>
      <Expression xsi:type=""Combinator""><Combinator xsi:type=""t:TestFrames"" /></Expression>
      <Expression xsi:type=""MemberSelector""><Selector>Value</Selector></Expression>
      <Expression xsi:type=""scr:ExpressionTransform"">
        <scr:Expression>new(
Image as Image,
new(ChunkData.FrameID as FrameID,
ChunkData.Timestamp as Timestamp,
ChunkData.ExposureTime as ExposureTime,
ChunkData.Gain as Gain) as ChunkData)</scr:Expression>
      </Expression>
      <Expression xsi:type=""Combinator""><Combinator xsi:type=""t:TestSeconds"" /></Expression>
      <Expression xsi:type=""Combinator""><Combinator xsi:type=""rx:Zip"" /></Expression>
      <Expression xsi:type=""Combinator""><Combinator xsi:type=""harp:CreateTimestamped"" /></Expression>
      <Expression xsi:type=""IncludeWorkflow"" Path=""OpenRigs.Logging:LogVideoFfmpeg.bonsai"">
        <LogName>Camera-Test</LogName>
      </Expression>
    </Nodes>
    <Edges>
      <Edge From=""0"" To=""1"" Label=""Source1"" />
      <Edge From=""2"" To=""3"" Label=""Source1"" />
      <Edge From=""3"" To=""4"" Label=""Source1"" />
      <Edge From=""4"" To=""6"" Label=""Source1"" />
      <Edge From=""5"" To=""6"" Label=""Source2"" />
      <Edge From=""6"" To=""7"" Label=""Source1"" />
      <Edge From=""7"" To=""8"" Label=""Source1"" />
    </Edges>
  </Workflow>
</WorkflowBuilder>";

        [TestMethod]
        public void TakesTheSpinnakerIncludesFramesAndBuilds()
        {
            Assert.IsNotNull(typeof(OpenRigs.Logging.LoggedRows).Assembly);
            Assert.IsNotNull(typeof(Bonsai.Scripting.Expressions.ExpressionTransform).Assembly);
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(SpinnakerHarness))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            builder.Workflow.Build();  // threw "No method overload found" while the writer took only VideoDataFrame
        }
    }

    /// <summary>Harp seconds for the test frames; it never runs.</summary>
    public class TestSeconds : Source<double>
    {
        /// <summary>No seconds.</summary>
        public override IObservable<double> Generate() => Observable.Empty<double>();
    }
}
