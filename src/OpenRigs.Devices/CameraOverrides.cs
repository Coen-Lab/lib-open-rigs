using System;
using System.ComponentModel;
using System.Reactive.Linq;
using Bonsai;
using OpenRigs.Core;
using OpenRigs.Devices.Rigs;

namespace OpenRigs.Devices
{
    /// <summary>
    /// Replaces a camera's binning from rig.yml with the value set here, for as long as it stays set (a launch property
    /// on a rig that wants it, such as the behaviour box's). Blank by default, and blank keeps rig.yml's value, so rig.yml
    /// stays the rig's standing setting. The output is a copy; RigConfigs keeps rig.yml as read.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Overrides the camera's binning from rig.yml while set. Blank keeps rig.yml's value.")]
    public class CameraOverrides
    {
        /// <summary>Gets or sets the binning, 1 for full resolution; null keeps rig.yml's.</summary>
        [Description("Binning, 1 for full resolution. Blank: rig.yml's binning.")]
        public int? Binning { get; set; }

        /// <summary>The camera with the override applied.</summary>
        public IObservable<NamedDevice<SpinnakerCamera>> Process(IObservable<NamedDevice<SpinnakerCamera>> source)
        {
            return source.Select(Apply);
        }

        /// <summary>The camera with the override applied; refuses a binning below 1, naming it.</summary>
        public NamedDevice<SpinnakerCamera> Apply(NamedDevice<SpinnakerCamera> device)
        {
            var binning = Binning;
            if (binning.HasValue && binning.Value < 1)
            {
                throw new InvalidOperationException(string.Format(
                    "{0}'s Binning is {1}; it must be 1 or more, or blank to use rig.yml's.", device.Name, binning.Value));
            }
            var settings = device.Settings.Copy();
            if (binning.HasValue) settings.Binning = binning.Value;
            return new NamedDevice<SpinnakerCamera>(device.Name, settings);
        }
    }
}

namespace OpenRigs.Devices.Rigs
{
    // Beside the generated class (LabRig.Generated.cs), which regeneration rewrites: its copy constructor is protected.
    public partial class SpinnakerCamera
    {
        /// <summary>A copy of this camera's settings.</summary>
        public SpinnakerCamera Copy()
        {
            return new SpinnakerCamera(this);
        }
    }
}
