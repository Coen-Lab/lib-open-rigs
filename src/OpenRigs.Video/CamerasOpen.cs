using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace OpenRigs.Video
{
    /// <summary>
    /// The names of the cameras that have begun acquiring, each once its TriggeredSpinnaker sees it
    /// streaming: a camera in trigger mode drops any trigger that comes before, so the trigger starts
    /// only once every camera is here. A C# operator cannot multicast into a named subject, so the
    /// cameras announce through this one process-wide hub.
    /// </summary>
    [Description("The name of each camera as it begins acquiring (TriggeredSpinnaker's Name), so the trigger can start once every camera is ready for it.")]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class CamerasOpen : Source<string>
    {
        static readonly Subject<string> Opened = new Subject<string>();

        /// <summary>Announces that the camera named <paramref name="name"/> has begun acquiring.</summary>
        internal static void Announce(string name)
        {
            Opened.OnNext(name);
        }

        /// <summary>The names of the cameras as they begin acquiring, from subscription on.</summary>
        public override IObservable<string> Generate()
        {
            return Opened.AsObservable();
        }
    }
}
