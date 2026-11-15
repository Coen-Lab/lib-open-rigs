using System;
using System.Reactive.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class RequireValueTests
    {
        [TestMethod]
        public void PassesAValueAndStopsWithItsMessageAtNull()
        {
            var require = new RequireValue { Message = "rig.yml has no zapit section, which ConnectZapit needs." };
            Assert.AreEqual("x", require.Process(Observable.Return("x")).Wait());
            var ex = Assert.ThrowsException<InvalidOperationException>(() => require.Process(Observable.Return((string)null)).Wait());
            Assert.AreEqual("rig.yml has no zapit section, which ConnectZapit needs.", ex.Message);
        }

        [TestMethod]
        public void RefusesNoMessage()
        {
            Assert.ThrowsException<InvalidOperationException>(() => new RequireValue().Process(Observable.Return("x")));
        }
    }
}
