using Bonsai;
using Bonsai.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Xml;

namespace OpenRigs.Devices.Tests
{
    /// <summary>
    /// ReadyGate's WorkflowStages: a stage the workflow announces itself holds Ready until it comes, as the valve
    /// calibration's CalibrationReady does.
    /// </summary>
    [TestClass]
    public class ReadyGateTests
    {
        // A piece's stage A at once and the workflow's own stage B after 1 s. Ready's report needs a ControlServer,
        // which this test has none of, so the workflow stops at Ready, saying so: the time of that stop is Ready's.
        const string Workflow = @"<?xml version=""1.0"" encoding=""utf-8""?>
<WorkflowBuilder Version=""2.9.0"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
                 xmlns:rx=""clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core""
                 xmlns:core=""clr-namespace:OpenRigs.Core;assembly=OpenRigs.Core""
                 xmlns=""https://bonsai-rx.org/2018/workflow"">
  <Workflow>
    <Nodes>
      <Expression xsi:type=""IncludeWorkflow"" Path=""OpenRigs.Devices:ReadyGate.bonsai"">
        <ReadyWaitsFor>A</ReadyWaitsFor>
        <WorkflowStages>B</WorkflowStages>
      </Expression>
      <Expression xsi:type=""Combinator""><Combinator xsi:type=""rx:Timer""><rx:DueTime>PT0S</rx:DueTime><rx:Period>PT0S</rx:Period></Combinator></Expression>
      <Expression xsi:type=""Combinator""><Combinator xsi:type=""core:AnnounceStage""><core:Stage>A</core:Stage></Combinator></Expression>
      <Expression xsi:type=""MulticastSubject""><Name>ReadyStages</Name></Expression>
      <Expression xsi:type=""Combinator""><Combinator xsi:type=""rx:Timer""><rx:DueTime>PT1S</rx:DueTime><rx:Period>PT0S</rx:Period></Combinator></Expression>
      <Expression xsi:type=""Combinator""><Combinator xsi:type=""core:AnnounceStage""><core:Stage>B</core:Stage></Combinator></Expression>
      <Expression xsi:type=""MulticastSubject""><Name>ReadyStages</Name></Expression>
    </Nodes>
    <Edges>
      <Edge From=""1"" To=""2"" Label=""Source1"" />
      <Edge From=""2"" To=""3"" Label=""Source1"" />
      <Edge From=""4"" To=""5"" Label=""Source1"" />
      <Edge From=""5"" To=""6"" Label=""Source1"" />
    </Edges>
  </Workflow>
</WorkflowBuilder>";

        [TestMethod]
        public void TheWorkflowsOwnStageHoldsReadyUntilItComes()
        {
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(Workflow)))
            {
                builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            }
            Exception error = null;
            var stopped = new ManualResetEventSlim();
            var workflow = builder.Workflow.BuildObservable();
            var clock = Stopwatch.StartNew();
            using (workflow.Subscribe(_ => { }, e => { error = e; stopped.Set(); }, stopped.Set))
            {
                Assert.IsTrue(stopped.Wait(TimeSpan.FromSeconds(10)), "Ready never came");
            }
            var at = clock.Elapsed.TotalSeconds;
            Assert.IsNotNull(error, "the workflow completed without reporting Ready");
            StringAssert.Contains(error.GetBaseException().Message, "ReportPhase needs a running ControlServer");
            Assert.IsTrue(at >= 0.9, "Ready came at " + at.ToString("0.00") + " s, before the workflow's own stage B at 1 s");
        }
    }
}
