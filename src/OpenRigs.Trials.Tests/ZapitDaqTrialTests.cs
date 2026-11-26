using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Opto.ZapitDaq;
using OpenRigs.Zapit;

namespace OpenRigs.Trials.Tests
{
    [TestClass]
    public class ZapitDaqTrialTests
    {
        class Trial : IOptoTrial
        {
            public int TrialNumber { get; set; }
            public int LaserCondition { get; set; }
            public string LaserPointsMm { get; set; }
            public bool LaserOn { get; set; }
            public double LaserPowerMw { get; set; }
        }

        class TimedTrial : Trial, IOptoDurationTrial
        {
            public double LaserDurationSeconds { get { return 1.5; } }
        }

        // The loop's Arm event for a trial, with the LaserSeconds the loop gives a laser played until stopped.
        static TrialEvent<Trial, string, string> ArmEvent(Trial trial, double laserSeconds)
        {
            var constructor = typeof(TrialEvent<Trial, string, string>).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];
            var e = (TrialEvent<Trial, string, string>)constructor.Invoke(new object[] { TrialEventKind.Arm, 1.0, trial, null, null, null });
            typeof(TrialEvent<Trial, string, string>).GetProperty("LaserSeconds").SetValue(e, laserSeconds);
            return e;
        }

        static List<string> Arms(bool untilStopped, params TrialEvent<Trial, string, string>[] events)
        {
            var sent = new List<string>();
            var arms = new System.Reactive.Subjects.Subject<TrialEvent<Trial, string, string>>();
            using (new ZapitDaqTrialCommand { UntilStopped = untilStopped }.Process(arms).Subscribe(c => sent.Add(c.ToString())))
            {
                foreach (var e in events) arms.OnNext(e);
            }
            return sent;
        }

        [TestMethod]
        public void EachTrialIsArmedWithItsConditionLaserAndDuration()
        {
            var timed = new TimedTrial { TrialNumber = 1, LaserCondition = 3, LaserOn = true, LaserPowerMw = 4.5 };
            var untimed = new Trial { TrialNumber = 2, LaserCondition = 2, LaserOn = false, LaserPowerMw = 4.5 };
            CollectionAssert.AreEqual(new[] { "Arm(trial=1, condition=3, laserOn=True, power=4.5, duration=1.5, untilStopped=False)", "Arm(trial=2, condition=2, laserOn=False, power=0, duration=0, untilStopped=False)" },
                Arms(false, ArmEvent(timed, 0), ArmEvent(untimed, 0)));
            // Played until stopped: the Arm event's LaserSeconds (its cap, or its stimulus's own off), not the trial's own duration, and
            // UntilStopped, so ZapitDaq streams it for its early stop.
            CollectionAssert.AreEqual(new[] { "Arm(trial=1, condition=3, laserOn=True, power=4.5, duration=1.6, untilStopped=True)", "Arm(trial=2, condition=2, laserOn=False, power=0, duration=0, untilStopped=True)" },
                Arms(true, ArmEvent(timed, 1.6), ArmEvent(untimed, 0)));
            var ex = Assert.ThrowsException<InvalidOperationException>(() => ZapitDaqTrial.Arm(new Trial { TrialNumber = 4, LaserCondition = 0 }));
            StringAssert.Contains(ex.Message, "Trial 4 has laser condition 0");
        }

        [TestMethod]
        public void AnArmedEventMustBeTheTrialsAsAsked()
        {
            ZapitDaqTrial.CheckArmed(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Armed, Trial = 7, Condition = 3, LaserOn = true }, 7, 3, true);
            foreach (var wrong in new[] { new ZapitDaqEvent { Trial = 6, Condition = 3, LaserOn = true }, new ZapitDaqEvent { Trial = 7, Condition = 2, LaserOn = true }, new ZapitDaqEvent { Trial = 7, Condition = 3 } })
            {
                Assert.ThrowsException<InvalidOperationException>(() => ZapitDaqTrial.CheckArmed(wrong, 7, 3, true));
            }
        }

        [TestMethod]
        public void TheLeadAndTheArmTimeFitTheShortestInterval()
        {
            Assert.IsNull(ZapitDaqTrial.LeadProblem(3.2, 3.4));
            StringAssert.Contains(ZapitDaqTrial.LeadProblem(3.3, 3.4), "plus Zapit's 0.2 s arm time must be at most trialStructure.itiMinSeconds (3.4 s)");
        }

        [TestMethod]
        public void TheArmTimeIsPythonsToo()
        {
            var python = File.ReadAllText(Path.Combine(Repo(), "src", "open_rigs", "components", "zapit.py"));
            StringAssert.Contains(python, "ZAPIT_ARM_SECONDS = " + ZapitDaqTrial.ArmSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            StringAssert.Contains(python, "ZAPIT_FREE_MARGIN_SECONDS = " + ZapitDaqTrial.FreeMarginSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>The contract bank (OpenRigs.Zapit.Tests' fixture) unpacked into a new temporary folder: its index's path.</summary>
        internal static string UnpackBank()
        {
            var data = Path.Combine(Repo(), "src", "OpenRigs.Zapit.Tests", "Data");
            var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(data, "Settings_ZapitBank_T.json"), Path.Combine(folder, "Settings_ZapitBank_T.json"));
            using (var input = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(data, "Settings_ZapitBank_T.bin.gz")), System.IO.Compression.CompressionMode.Decompress))
            using (var output = File.Create(Path.Combine(folder, "Settings_ZapitBank_T.bin"))) input.CopyTo(output);
            return Path.Combine(folder, "Settings_ZapitBank_T.json");
        }

        [TestMethod]
        public void ADrawTakesTheBanksConditionsAndPointsAndTheBankIsCheckedAtGo()
        {
            var path = UnpackBank();
            var bank = ZapitBank.Load(path);
            Directory.Delete(Path.GetDirectoryName(path), true);
            var random = new Random(1);
            var conditions = new HashSet<int>();
            for (var n = 1; n <= 200; n++)
            {
                int condition; string points; bool laserOn; double power;
                ZapitDaqTrial.Draw(random, n, bank, 10, 0.5, 2, out condition, out points, out laserOn, out power);
                conditions.Add(condition);
                Assert.AreEqual(bank.Conditions[condition - 1].LaserPointsMm, points);
                Assert.IsTrue(n > 10 || !laserOn, "no laser before trialsBeforeLaser");
                Assert.AreEqual(laserOn ? 2 : 0, power);
            }
            Assert.AreEqual(bank.Conditions.Count, conditions.Count, "every condition drawn");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => ZapitBankForTask.Check(null, "x.yml", 2)).Message, "No Zapit bank was loaded before Go");
            var other = Path.GetTempFileName();
            File.WriteAllText(other, "stimLocations01: {}\n");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => ZapitBankForTask.Check(bank, other, 2)).Message, "its conditions hash differs");
            File.Delete(other);
        }

        static string Repo()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(dir.FullName, "OpenRigs.sln"))) dir = dir.Parent;
            return dir.FullName;
        }
    }
}
