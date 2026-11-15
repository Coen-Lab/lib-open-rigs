using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Core;

namespace OpenRigs.Devices.Tests
{
    /// <summary>
    /// RigHardware reports Ready from its ReadyWatchdog: the stages its ReadyWaitsFor names, HarpClockSet and
    /// SpoutsHomed by default, and MixerStarted (from SpeakerMixer, through ReadyStages) on a workflow that plays sound.
    /// </summary>
    [TestClass]
    public class RigReadyTests
    {
        static readonly XNamespace Bonsai = "https://bonsai-rx.org/2018/workflow";
        static readonly XNamespace Core = "clr-namespace:OpenRigs.Core;assembly=OpenRigs.Core";

        static XElement ReadyWatchdog()
        {
            using (var stream = typeof(RewardForSide).Assembly.GetManifestResourceStream("OpenRigs.Devices.RigHardware.bonsai"))
            {
                return XDocument.Load(stream).Descendants(Bonsai + "Expression").Single(e => (string)e.Element(Bonsai + "Name") == "ReadyWatchdog");
            }
        }

        static List<string> Ready(string expected, params string[] stages)
        {
            var arrived = new Subject<string>();
            var ready = new List<string>();
            var watchdog = new StartupWatchdog { Expected = expected, TimeoutSeconds = 60 };
            using (watchdog.Process(arrived).Subscribe(ready.Add))
            {
                foreach (var stage in stages) arrived.OnNext(stage);
            }
            return ready;
        }

        [TestMethod]
        public void ReadyWaitsForTheHarpClockAndTheSpoutsByDefault()
        {
            var expected = (string)ReadyWatchdog().Descendants(Core + "Expected").Single();
            Assert.AreEqual("HarpClockSet, SpoutsHomed", expected);
            Assert.AreEqual(0, Ready(expected, "HarpClockSet").Count);
            CollectionAssert.AreEqual(new[] { "SpoutsHomed" }, Ready(expected, "HarpClockSet", "SpoutsHomed"));
        }

        [TestMethod]
        public void ReadyDoesNotComeBeforeTheMixerStartsOnAWorkflowThatPlaysSound()
        {
            var withMixer = (string)ReadyWatchdog().Descendants(Core + "Expected").Single() + ", MixerStarted";
            Assert.AreEqual(0, Ready(withMixer, "HarpClockSet", "SpoutsHomed").Count);
            CollectionAssert.AreEqual(new[] { "MixerStarted" }, Ready(withMixer, "HarpClockSet", "SpoutsHomed", "MixerStarted"));
            CollectionAssert.AreEqual(new[] { "SpoutsHomed" }, Ready(withMixer, "MixerStarted", "HarpClockSet", "SpoutsHomed"));
        }

        [TestMethod]
        public void ReadyWaitsForIsTheWatchdogsExpected()
        {
            var workflow = ReadyWatchdog().Parent;
            var mapping = workflow.Elements(Bonsai + "Expression").SelectMany(e => e.Elements(Bonsai + "Property"))
                .Single(p => (string)p.Attribute("DisplayName") == "ReadyWaitsFor");
            Assert.AreEqual("Expected", (string)mapping.Attribute("Name"));
        }
    }
}
