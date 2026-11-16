using Bonsai;
using OpenTK;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;

namespace OpenRigs.Vision
{
    /// <summary>
    /// Stops the workflow at start when the display a shader window is meant for is not connected: OpenTK opens a window
    /// asked for on a missing display on the primary one instead, without a word.
    /// </summary>
    [Combinator]
    [Description("Passes the sequence it is given (the projector's window) through, subscribing it only if DisplayDevice is a display on this machine; otherwise stops the workflow at start, naming Name, the display asked for and the displays there are. A shader window asked for on a missing display opens on the primary one instead, silently.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class RequireDisplay
    {
        static readonly DisplayIndex[] Displays =
            { DisplayIndex.First, DisplayIndex.Second, DisplayIndex.Third, DisplayIndex.Fourth, DisplayIndex.Fifth, DisplayIndex.Sixth };

        /// <summary>Gets or sets the display the window is meant for.</summary>
        [Description("The display the window is meant for (the window's own DisplayDevice).")]
        public DisplayIndex DisplayDevice { get; set; } = DisplayIndex.First;

        /// <summary>Gets or sets the name of the include that needs the display, which the refusal names.</summary>
        [Description("The include that needs the display, named in the refusal.")]
        public string Name { get; set; }

        /// <summary>Subscribes the sequence if the display is connected, else stops with an error.</summary>
        public IObservable<TSource> Process<TSource>(IObservable<TSource> source)
        {
            return Observable.Defer(() =>
            {
                if (OpenTK.DisplayDevice.GetDisplay(DisplayDevice) != null) return source;
                var present = Displays.Where(d => OpenTK.DisplayDevice.GetDisplay(d) != null).ToList();
                return Observable.Throw<TSource>(new InvalidOperationException(string.Format(
                    "{0} cannot open the projector's window: DisplayDevice {1} is not a display on this machine, which has {2} ({3}). Connect the projector, or set DisplayDevice to its display.",
                    Name, DisplayDevice, present.Count, string.Join(", ", present))));
            });
        }
    }
}
