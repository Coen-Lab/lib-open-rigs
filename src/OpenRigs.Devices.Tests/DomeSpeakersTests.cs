using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Devices.Tests
{
    [TestClass]
    public class DomeSpeakersTests
    {
        [TestMethod]
        public void EverySpeakerNeedsAFilterNamingThoseWithout()
        {
            DomeSpeakers.RequireFilters(new Dictionary<string, string> { { "left", "calibration/speaker-left.json" } });
            var ex = Assert.ThrowsException<InvalidOperationException>(() => DomeSpeakers.RequireFilters(new Dictionary<string, string>
            {
                { "right", null }, { "left", "calibration/speaker-left.json" }, { "centre", " " },
            }));
            StringAssert.Contains(ex.Message, "speaker 'centre', 'right' has no filter");
            StringAssert.Contains(ex.Message, "calibration/speaker-centre.json");
        }
    }
}
