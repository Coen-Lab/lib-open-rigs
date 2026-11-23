using Newtonsoft.Json;
using YamlDotNet.Serialization;

namespace OpenRigs.Devices.Rigs
{
    // rig.yml's zapit block names each channel on its own beside the device, as lib_zapit reads it; ZapitDaq takes NI's
    // full paths. The one place they are joined: map ZapitDaq's channels from these.
    public partial class Zapit
    {
        /// <summary>Gets the X galvo's AO channel as ZapitDaq takes it, such as Dev1/ao0.</summary>
        [JsonIgnore, YamlIgnore]
        public string GalvoXChannelPath { get { return Path(GalvoXChannel); } }

        /// <summary>Gets the Y galvo's AO channel as ZapitDaq takes it.</summary>
        [JsonIgnore, YamlIgnore]
        public string GalvoYChannelPath { get { return Path(GalvoYChannel); } }

        /// <summary>Gets the laser's AO channel as ZapitDaq takes it.</summary>
        [JsonIgnore, YamlIgnore]
        public string LaserChannelPath { get { return Path(LaserChannel); } }

        /// <summary>Gets the masking LED's AO channel as ZapitDaq takes it.</summary>
        [JsonIgnore, YamlIgnore]
        public string LedChannelPath { get { return Path(LedChannel); } }

        /// <summary>Gets the photodiode's AI channel as ZapitDaq takes it.</summary>
        [JsonIgnore, YamlIgnore]
        public string PhotodiodeChannelPath { get { return Path(PhotodiodeChannel); } }


        /// <summary>Gets the trigger's PFI terminal as ZapitDaq takes it, such as /Dev1/PFI0.</summary>
        [JsonIgnore, YamlIgnore]
        public string TriggerTerminal { get { return "/" + Path(TriggerChannel); } }

        string Path(string line) { return Device + "/" + line; }
    }
}
