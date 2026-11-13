using System;
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
    }
}
