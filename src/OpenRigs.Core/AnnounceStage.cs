using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Core
{
    /// <summary>
    /// Announces one start-up stage: emits <see cref="Stage"/> at the first element of its input and
    /// then never completes, so a MulticastSubject after it never completes the shared ReadyStages
    /// subject (Bonsai 2.9 passes a multicast source's completion on to a ReplaySubject, which would
    /// end every other piece's announcement with it).
    /// </summary>
    [Combinator]
    [DefaultProperty("Stage")]
    [Description("Emits Stage at the first element of its input and never completes, so a multicast into ReadyStages after it leaves the subject open for every other stage.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class AnnounceStage
    {
        /// <summary>Gets or sets the stage this announces.</summary>
        [Description("The stage this announces, as Ready's ReadyWaitsFor names it, such as SpoutsHomed.")]
        public string Stage { get; set; }

        /// <summary>Announces the stage at the first element of the input.</summary>
        public IObservable<string> Process<TSource>(IObservable<TSource> source)
        {
            return Observable.Defer(() =>
            {
                var stage = Stage;
                if (string.IsNullOrWhiteSpace(stage)) throw new InvalidOperationException("AnnounceStage has no Stage to announce.");
                return source.Take(1).Select(_ => stage).Concat(Observable.Never<string>());
            });
        }
    }
}
