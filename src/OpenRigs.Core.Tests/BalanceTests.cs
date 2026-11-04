using System;
using System.Linq;
using System.Reactive.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Core.Calibration;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class BalanceTests
    {
        [TestMethod]
        public void ParsesStableAndUnstableReadings()
        {
            var stable = ParseBalanceReading.Parse("   12.345 g", "g", "?", 1.0);
            Assert.AreEqual(12.345f, stable.Value, 1e-6f);
            Assert.IsTrue(stable.IsStable);
            Assert.AreEqual(1.0, stable.Timestamp);

            var unstable = ParseBalanceReading.Parse("   12.345 g?", "g", "?", 2.0);
            Assert.AreEqual(12.345f, unstable.Value, 1e-6f);
            Assert.IsFalse(unstable.IsStable);
            Assert.AreEqual(0f, unstable.Confidence);
        }

        [TestMethod]
        public void DropsIgnoredAndEmptyLines()
        {
            Assert.IsTrue(ParseBalanceReading.IsIgnored("ES", "ES"));
            Assert.IsTrue(ParseBalanceReading.IsIgnored("  ", "ES"));
            Assert.IsTrue(ParseBalanceReading.IsIgnored("EL", "ES, EL"));
            Assert.IsFalse(ParseBalanceReading.IsIgnored("1.0 g", "ES"));
        }

        [TestMethod]
        public void ReportsAnUnparsableLine()
        {
            try
            {
                ParseBalanceReading.Parse("hello", "g", "?", 0);
                Assert.Fail("expected a failure");
            }
            catch (InvalidOperationException ex)
            {
                StringAssert.Contains(ex.Message, "'hello'");
            }
        }

        [TestMethod]
        public void TheOperatorFiltersThenParses()
        {
            var readings = new ParseBalanceReading()
                .Process(new[] { "ES", "  1.000 g", "  1.500 g?" }.ToObservable())
                .ToList().Wait();
            Assert.AreEqual(2, readings.Count);
            Assert.IsTrue(readings[0].IsStable);
            Assert.IsFalse(readings[1].IsStable);
        }

        [TestMethod]
        public void PollsTareThenPrintOnTheSettingsSchedule()
        {
            var settings = new SerialBalance { BootDelayS = 0.02, TareDelayS = 0.03, PrintPeriodS = 0.02, TareCommand = "T", PrintCommand = "IP" };
            var commands = PollBalance.Commands(settings).Take(4).ToList().Timeout(TimeSpan.FromSeconds(5)).Wait();
            CollectionAssert.AreEqual(new[] { "T", "IP", "IP", "IP" }, commands.ToArray());
        }

        [TestMethod]
        public void AnEmptyCommandIsNotSent()
        {
            var settings = new SerialBalance { BootDelayS = 0.01, TareDelayS = 0.01, PrintPeriodS = 0.01, TareCommand = "", PrintCommand = "IP" };
            var commands = PollBalance.Commands(settings).Take(2).ToList().Timeout(TimeSpan.FromSeconds(5)).Wait();
            CollectionAssert.AreEqual(new[] { "IP", "IP" }, commands.ToArray());
        }

        [TestMethod]
        public void TheSettingsDefaultsMatchAnOhausStyleBalance()
        {
            var settings = new SerialBalance();
            Assert.AreEqual("COMx", settings.PortName);
            Assert.AreEqual(9600, settings.BaudRate);
            Assert.AreEqual("T", settings.TareCommand);
            Assert.AreEqual("g", settings.UnitSuffix);
            Assert.AreEqual("?", settings.UnstableMarker);
        }
    }
}
