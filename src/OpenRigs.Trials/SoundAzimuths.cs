#nullable disable
using OpenRigs.Devices.Rigs;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenRigs.Trials
{
    /// <summary>Where a trial's sound plays: an auditoryStimulus.initialAzimuthsDeg item read, and rig.yml's speaker nearest
    /// it (open_rigs.rigs.conditioned_task SoundAzimuth, ALL_SPEAKERS). Here, not in OpenRigs.Audio, as rig.yml's speaker
    /// types are OpenRigs.Devices'.</summary>
    public static class SoundAzimuths
    {
        /// <summary>The item that plays the sound on every rig.yml speaker.</summary>
        public const string AllSpeakers = "all";

        /// <summary>A trial's speakerChannel when its sound plays on every rig.yml speaker.</summary>
        public const int AllSpeakersChannel = -2;

        /// <summary>An item as read (a number, or AllSpeakers): its azimuth in degrees, NaN for AllSpeakers, null for anything else.</summary>
        public static double? Parse(object item)
        {
            if (item as string == AllSpeakers) return double.NaN;
            double azimuth;
            if ((item is string || item is IConvertible) && !(item is bool) && double.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out azimuth) && !double.IsNaN(azimuth)) return azimuth;
            return null;
        }

        /// <summary>rig.yml's speaker at the smallest angle from the location (the first in rig.yml's order on a tie).</summary>
        public static KeyValuePair<string, Speaker> NearestSpeaker(EqualisedSpeakerArray speakers, double azimuthDeg, double elevationDeg)
        {
            if (speakers == null || speakers.Speakers == null || speakers.Speakers.Count == 0)
            {
                throw new InvalidOperationException("rig.yml has no speakerArray.speakers to play the auditory stimuli on.");
            }
            return speakers.Speakers.OrderBy(s => Angle(azimuthDeg, elevationDeg, s.Value.Position.Azimuth, s.Value.Position.Elevation)).First();
        }

        /// <summary>The angle between two directions, in radians.</summary>
        public static double Angle(double az1, double el1, double az2, double el2)
        {
            const double rad = Math.PI / 180;
            var cos = Math.Sin(el1 * rad) * Math.Sin(el2 * rad) + Math.Cos(el1 * rad) * Math.Cos(el2 * rad) * Math.Cos((az1 - az2) * rad);
            return Math.Acos(Math.Max(-1, Math.Min(1, cos)));
        }
    }
}
