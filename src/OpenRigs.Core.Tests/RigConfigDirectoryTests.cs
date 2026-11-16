using System;
using System.Reactive.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class RigConfigDirectoryTests
    {
        // No root of its own: an unset ConfigRoot stops at start naming where to set it, never another folder.
        [TestMethod]
        public void AnUnsetConfigRootNamesReadLabRig()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => new ResolveRigConfigDirectory().Generate().Wait());
            StringAssert.Contains(ex.Message, "ReadLabRig's ConfigRoot");
        }
    }
}
