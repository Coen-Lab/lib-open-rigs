using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenRigs.Devices
{
    /// <summary>The rules every dome workflow holds rig.yml's speakers to (open_rigs.rigs.dome DomeSpeakerArray holds the same).</summary>
    public static class DomeSpeakers
    {
        /// <summary>
        /// Throws, naming each speaker without one, unless every speaker has a filter: a dome
        /// never plays a speaker unequalised. <paramref name="filters"/> is each speaker's name and rig.yml filter.
        /// </summary>
        public static void RequireFilters(IEnumerable<KeyValuePair<string, string>> filters)
        {
            var missing = filters.Where(speaker => string.IsNullOrWhiteSpace(speaker.Value)).Select(speaker => speaker.Key).OrderBy(name => name).ToList();
            if (missing.Count == 0) return;
            throw new InvalidOperationException(string.Format(
                "rig.yml's speaker {0} has no filter, and a dome plays every speaker equalised. Give each speaker in speakerArray.speakers " +
                "its SpeakerFilter artefact, as in filter: calibration/speaker-{1}.json.",
                string.Join(", ", missing.Select(name => "'" + name + "'")), missing[0]));
        }
    }
}
