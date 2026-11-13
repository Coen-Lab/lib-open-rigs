using System;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Core;

namespace OpenRigs.Devices.Tests
{
    [TestClass]
    public class RewardForSideTests
    {
        static readonly RewardLine Left = new RewardLine { Side = "left", Line = 1 };
        static readonly RewardLine Right = new RewardLine { Side = "right", Line = 2 };

        [TestMethod]
        public void PicksTheSidesLineIgnoringCase()
        {
            Assert.AreSame(Left, RewardForSide.Pick("Left", Left, Right));
            Assert.AreSame(Right, RewardForSide.Pick("right", Left, Right));
        }

        [TestMethod]
        public void RefusesAnotherSide()
        {
            var ex = Assert.ThrowsException<ArgumentException>(() => RewardForSide.Pick("centre", Left, Right));
            StringAssert.Contains(ex.Message, "side 'centre': the reward lines are left and right");
        }

        // One FirePulses queues each reward a gap after the last (RewardLineTests); one per reward did not.
        [TestMethod]
        public void DeliverRewardFiresEveryRewardThroughOneFirePulses()
        {
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            XDocument workflow;
            using (var stream = typeof(RewardForSide).Assembly.GetManifestResourceStream("OpenRigs.Devices.BehaviorBoard.DeliverReward.bonsai"))
            {
                workflow = XDocument.Load(stream);
            }
            var types = workflow.Descendants().Select(e => (string)e.Attribute(xsi + "type")).Where(t => t != null).ToList();
            Assert.AreEqual(1, types.Count(t => t == "core:FirePulses"));
            Assert.IsFalse(types.Contains("rx:CreateObservable") || types.Contains("rx:Concat"));

            var nodes = workflow.Root.Element(Bonsai + "Workflow").Element(Bonsai + "Nodes").Elements().ToList();
            var edges = workflow.Root.Element(Bonsai + "Workflow").Element(Bonsai + "Edges").Elements()
                .Select(e => new { From = (int)e.Attribute("From"), To = (int)e.Attribute("To"), Label = (string)e.Attribute("Label") }).ToList();
            Func<int, string> type = index => (string)nodes[index].Attribute(xsi + "type") == "Combinator"
                ? (string)nodes[index].Elements().First().Attribute(xsi + "type")
                : (string)nodes[index].Attribute(xsi + "type");
            var fire = nodes.FindIndex(n => type(nodes.IndexOf(n)) == "core:FirePulses");
            var input = edges.Single(e => e.To == fire && e.Label == "Source1").From;
            Assert.AreEqual("InputMapping", type(input));
            CollectionAssert.AreEquivalent(new[] { "Line", "WidthMs", "Count", "PulseGapMs" },
                nodes[input].Descendants(Bonsai + "Property").Select(p => (string)p.Attribute("Name")).ToArray());
            Assert.AreEqual("core:PlanReward", type(edges.Single(e => e.To == input).From));
        }

        // One cap on a reward: LoadRewardLine's default, DeliverReward's two lines and the Python dome model's MAX_REWARD_UL.
        [TestMethod]
        public void MaxRewardIsThePythonModels()
        {
            XNamespace core = "clr-namespace:OpenRigs.Core;assembly=OpenRigs.Core";
            XDocument workflow;
            using (var stream = typeof(RewardForSide).Assembly.GetManifestResourceStream("OpenRigs.Devices.BehaviorBoard.DeliverReward.bonsai"))
            {
                workflow = XDocument.Load(stream);
            }
            var caps = workflow.Descendants(core + "MaxRewardUl").Select(e => (double)e).ToList();
            Assert.AreEqual(2, caps.Count);
            Assert.IsTrue(caps.All(c => c == LoadRewardLine.DefaultMaxRewardUl), string.Join(", ", caps));
            StringAssert.Matches(ZapitTests.DomePy(), new System.Text.RegularExpressions.Regex(
                @"(?m)^MAX_REWARD_UL = " + LoadRewardLine.DefaultMaxRewardUl.ToString(System.Globalization.CultureInfo.InvariantCulture) + @"\r?$"));
        }

        static readonly XNamespace Bonsai ="https://bonsai-rx.org/2018/workflow";
    }
}
