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
    [Description("Emits Stage at the first element of its input and never completes, so a multicast into ReadyStages after it leaves the subject open for every other stage. Stays subscribed to its input until the input ends, so the input's completion still reaches any subject multicast before it.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class AnnounceStage
    {
        /// <summary>Gets or sets the stage this announces.</summary>
        [Description("The stage this announces, as Ready's PieceStages names it, such as SpoutsHomed.")]
        public string Stage { get; set; }

        /// <summary>Announces the stage at the first element of the input.</summary>
        public IObservable<string> Process<TSource>(IObservable<TSource> source)
        {
            return Observable.Defer(() =>
            {
                var stage = Stage;
                if (string.IsNullOrWhiteSpace(stage)) throw new InvalidOperationException("AnnounceStage has no Stage to announce.");
                // Never Take(1): it would dispose the source inside its first OnNext, so a source that completes
                // right after (HarpClockSet's Take(1)) loses its completion, and an AsyncSubject multicast before
                // this never completes (rig test, 16 Nov 2026: every Harp log waited on HarpClockSet forever).
                return Observable.Create<string>(observer =>
                {
                    var announced = 0;
                    return source.Subscribe(
                        _ => { if (System.Threading.Interlocked.Exchange(ref announced, 1) == 0) observer.OnNext(stage); },
                        observer.OnError,
                        () => { });
                });
            });
        }
    }
}
