using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Zapit;

namespace OpenRigs.Trials.Tests
{
    [TestClass]
    public class ZapitTrialCommandTests
    {
        class Trial : IZapitTrial
        {
            public int TrialNumber { get; set; }
            public int LaserCondition { get; set; }
            public string LaserPointsMm { get; set; }
            public bool LaserOn { get; set; }
            public double LaserPowerMw { get; set; }
        }

        class TimedTrial : Trial, ILaserDurationTrial
        {
            public double LaserDurationSeconds { get { return 1.5; } }
        }

        [TestMethod]
        public void EachTrialIsArmedWithItsConditionLaserAndDuration()
        {
            var sent = new List<string>();
            var trials = new System.Reactive.Subjects.Subject<Trial>();
            using (new ZapitTrialCommand().Process(trials).Subscribe(c => sent.Add(c.ToString())))
            {
                trials.OnNext(new TimedTrial { TrialNumber = 1, LaserCondition = 3, LaserOn = true, LaserPowerMw = 4.5 });
                trials.OnNext(new Trial { TrialNumber = 2, LaserCondition = 2, LaserOn = false, LaserPowerMw = 4.5 });
            }
            CollectionAssert.AreEqual(new[] { "Arm(trial=1, condition=3, laserOn=True, power=4.5, duration=1.5)", "Arm(trial=2, condition=2, laserOn=False, power=0, duration=0)" }, sent);
            var ex = Assert.ThrowsException<InvalidOperationException>(() => ZapitTrial.Arm(new Trial { TrialNumber = 4, LaserCondition = 0 }));
            StringAssert.Contains(ex.Message, "Trial 4 has laser condition 0");
        }

        [TestMethod]
        public void AnArmedEventMustBeTheTrialsAsAsked()
        {
            ZapitTrial.CheckArmed(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Armed, Trial = 7, Condition = 3, LaserOn = true }, 7, 3, true);
            foreach (var wrong in new[] { new ZapitDaqEvent { Trial = 6, Condition = 3, LaserOn = true }, new ZapitDaqEvent { Trial = 7, Condition = 2, LaserOn = true }, new ZapitDaqEvent { Trial = 7, Condition = 3 } })
            {
                Assert.ThrowsException<InvalidOperationException>(() => ZapitTrial.CheckArmed(wrong, 7, 3, true));
            }
        }

        [TestMethod]
        public void TheLeadAndTheArmTimeFitTheShortestInterval()
        {
            Assert.IsNull(ZapitTrial.LeadProblem(3.2, 3.4));
            StringAssert.Contains(ZapitTrial.LeadProblem(3.3, 3.4), "plus Zapit's 0.2 s arm time must be at most trialStructure.itiMinSeconds (3.4 s)");
        }

        [TestMethod]
        public void TheArmTimeIsPythonsToo()
        {
            var python = File.ReadAllText(Path.Combine(Repo(), "src", "open_rigs", "components", "zapit.py"));
            StringAssert.Contains(python, "ZAPIT_ARM_SECONDS = " + ZapitTrial.ArmSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void ADrawTakesTheBanksConditionsAndPointsAndTheBankIsCheckedAtGo()
        {
            var data = Path.Combine(Repo(), "src", "OpenRigs.Zapit.Tests", "Data");
            var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(data, "Settings_ZapitBank_T.json"), Path.Combine(folder, "Settings_ZapitBank_T.json"));
            using (var input = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(data, "Settings_ZapitBank_T.bin.gz")), System.IO.Compression.CompressionMode.Decompress))
            using (var output = File.Create(Path.Combine(folder, "Settings_ZapitBank_T.bin"))) input.CopyTo(output);
            var bank = ZapitBank.Load(Path.Combine(folder, "Settings_ZapitBank_T.json"));
            Directory.Delete(folder, true);
            var random = new Random(1);
            var conditions = new HashSet<int>();
            for (var n = 1; n <= 200; n++)
            {
                int condition; string points; bool laserOn; double power;
                ZapitTrial.Draw(random, n, bank, 10, 0.5, 2, out condition, out points, out laserOn, out power);
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
