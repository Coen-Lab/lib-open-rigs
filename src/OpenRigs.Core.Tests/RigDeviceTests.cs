using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class RigDeviceTests
    {
        static readonly Dictionary<string, string> Cameras = new Dictionary<string, string> { { "face", "port 1" }, { "body", "port 2" } };

        static NamedDevice<string> Take(string name, Dictionary<string, string> entries)
        {
            return new RigDevice { Name = name }.Process(Observable.Return(entries)).Wait();
        }

        [TestMethod]
        public void TakesTheEntryTheNameGivesWithTheName()
        {
            var device = Take("Camera-Face", Cameras);
            Assert.AreEqual("Camera-Face", device.Name);
            Assert.AreEqual("port 1", device.Settings);
        }

        [TestMethod]
        public void ANameRigYmlLacksNamesTheEntriesItHas()
        {
            var ex = Assert.ThrowsException<KeyNotFoundException>(() => Take("Camera-Side", Cameras));
            StringAssert.Contains(ex.Message, "rig.yml has no camera named 'side', which this workflow records as Camera-Side. Its camera entries: body, face.");
            ex = Assert.ThrowsException<KeyNotFoundException>(() => Take("Camera-Side", new Dictionary<string, string>()));
            StringAssert.Contains(ex.Message, "Its camera section has no entries.");
        }

        [TestMethod]
        public void AnUnsetOrMalformedNameFailsAtStart()
        {
            // A lost Name mapping still builds, so the unset name must fail when the workflow starts.
            foreach (var name in new[] { null, "", "camera-face", "Camera", "Camera-Face-2", "Camera-Face_1" })
            {
                var ex = Assert.ThrowsException<ArgumentException>(() => Take(name, Cameras), name ?? "null");
                StringAssert.Contains(ex.Message, "set it to Kind-Instance");
            }
        }
    }
}
