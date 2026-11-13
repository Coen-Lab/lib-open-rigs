using System;
using System.Reactive.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Core;

namespace OpenRigs.Video.Tests
{
    [TestClass]
    public class ExposureFitsTriggerTests
    {
        public class Camera { public double ExposureTime { get; set; } }
        public class Trigger { public double Trigger0Frequency { get; set; } public double Trigger1Frequency { get; set; } }
        public class Board { public Trigger CameraTriggerController { get; set; } }

        static NamedDevice<Camera> Run(double exposure, int output, Trigger trigger)
        {
            var pair = Tuple.Create(new NamedDevice<Camera>("Camera-Face", new Camera { ExposureTime = exposure }),
                new NamedDevice<Board>("BehaviorBoard-Main", new Board { CameraTriggerController = trigger }));
            return new ExposureFitsTrigger { TriggerOutput = output }.Process(Observable.Return(pair)).Wait();
        }

        [TestMethod]
        public void PassesAnExposureThatFits()
        {
            Assert.AreEqual("Camera-Face", Run(19000, 0, new Trigger { Trigger0Frequency = 50 }).Name);
        }

        [TestMethod]
        public void RefusesAnExposureTooLongForItsTrigger()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => Run(19000, 1, new Trigger { Trigger0Frequency = 50, Trigger1Frequency = 60 }));
            StringAssert.Contains(ex.Message, "Camera-Face's ExposureTime is 19000 us, longer than 15666");
        }

        [TestMethod]
        public void RefusesABoardWithNoCameraTrigger()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => Run(1000, 0, null));
            StringAssert.Contains(ex.Message, "BehaviorBoard-Main in rig.yml has no CameraTriggerController");
        }
    }
}
