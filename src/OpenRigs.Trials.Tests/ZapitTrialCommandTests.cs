using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Trials.Tests
{
    [TestClass]
    public class ZapitTrialCommandTests
    {
        class Trial : ILaserDurationTrial
        {
            public double LaserDurationSeconds { get { return 1.5; } }
            public int TrialNumber { get; set; }
            public int LaserCondition { get; set; }
            public string LaserPointsMm { get; set; }
            public bool LaserOn { get; set; }
            public double LaserPowerMw { get; set; }
        }

        [TestMethod]
        public void TheTrialCommandGoesAtArmingOnAHardwareTriggeredRigElseAtTheTrigger()
        {
            foreach (var hardwareTriggered in new[] { true, false })
            {
                var arm = new System.Reactive.Subjects.Subject<Trial>();
                var trigger = new System.Reactive.Subjects.Subject<Trial>();
                var sent = new List<string>();
                var operatorUnderTest = new ZapitTrialCommand { HardwareTriggered = hardwareTriggered };
                using (operatorUnderTest.Process(arm, trigger).Subscribe(c => sent.Add(c.ToString())))
                {
                    arm.OnNext(new Trial { TrialNumber = 1, LaserCondition = 3, LaserOn = true, LaserPowerMw = 4.5 });
                    trigger.OnNext(new Trial { TrialNumber = 2, LaserCondition = 2, LaserOn = false, LaserPowerMw = 4.5 });
                }
                var expected = hardwareTriggered
                    ? "StartStimulus(condition=3, laserOn=True, hardwareTriggered=True, duration=1.5, power=4.5, delay=0)"
                    : "StartStimulus(condition=2, laserOn=False, hardwareTriggered=False, duration=1.5, power=0, delay=0)";
                CollectionAssert.AreEqual(new[] { expected }, sent);
            }
        }
    }
}
