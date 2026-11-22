using Bonsai;
using OpenTK;
using OpenTK.Graphics.OpenGL4;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;

namespace OpenRigs.Vision
{
    /// <summary>
    /// Stops the workflow at start when the display a shader window is meant for is not connected: OpenTK opens a window
    /// asked for on a missing display on the primary one instead, without a word. Also stops it without hardware OpenGL 3.3.
    /// </summary>
    [Combinator]
    [Description("Passes the sequence it is given (the projector's window) through, subscribing it only if DisplayDevice is a display on this machine; otherwise stops the workflow at start, naming Name, the display asked for and the displays there are. A shader window asked for on a missing display opens on the primary one instead, silently. Also stops it when this session has no hardware OpenGL 3.3 (an RDP session), naming the renderer, where BonVision would end Bonsai with a bare access violation.")]
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
                if (OpenTK.DisplayDevice.GetDisplay(DisplayDevice) == null)
                {
                    var present = Displays.Where(d => OpenTK.DisplayDevice.GetDisplay(d) != null).ToList();
                    return Observable.Throw<TSource>(new InvalidOperationException(string.Format(
                        "{0} cannot open the projector's window: DisplayDevice {1} is not a display on this machine, which has {2} ({3}). Connect the projector, or set DisplayDevice to its display.",
                        Name, DisplayDevice, present.Count, string.Join(", ", present))));
                }
                var software = SoftwareOpenGL();
                if (software != null)
                {
                    return Observable.Throw<TSource>(new InvalidOperationException(string.Format(
                        "{0} cannot draw: this session has no hardware OpenGL ({1}). Is it an RDP session?", Name, software)));
                }
                return source;
            });
        }

        /// <summary>
        /// The renderer and version when this session's OpenGL is below 3.3 or Windows' software "GDI Generic", where
        /// BonVision's first GL call is an access violation naming nothing; null when it is hardware OpenGL 3.3 or later.
        /// Read in a hidden 1 by 1 window.
        /// </summary>
        public static string SoftwareOpenGL()
        {
            string version, renderer;
            using (var window = new GameWindow(1, 1))
            {
                window.MakeCurrent();
                version = GL.GetString(StringName.Version) ?? "";
                renderer = GL.GetString(StringName.Renderer) ?? "";
            }
            var number = Regex.Match(version, @"^\d+\.\d+");
            if (renderer != "GDI Generic" && number.Success && Version.Parse(number.Value) >= new Version(3, 3)) return null;
            return (renderer + " " + version).Trim();
        }
    }
}
