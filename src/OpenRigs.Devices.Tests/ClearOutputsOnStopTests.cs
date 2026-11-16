using Harp.Behavior;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Devices.Tests
{
    [TestClass]
    public class ClearOutputsOnStopTests
    {
        // A board unplugged mid-run, or a port name that is no port at all, is passed over at Stop, never thrown.
        [TestMethod]
        public void APortThatIsNotThereIsPassedOver()
        {
            Assert.IsFalse(ClearOutputsOnStop.Clear("COM97", DigitalOutputs.DO3));
            Assert.IsFalse(ClearOutputsOnStop.Clear("UNMAPPED", DigitalOutputs.DO3));
        }

        // Unmapped, it clears no line: DO3 may be another rig's reward valve.
        [TestMethod]
        public void UnmappedItClearsNothing()
        {
            Assert.AreEqual(DigitalOutputs.None, new ClearOutputsOnStop().Outputs);
        }
    }
}
