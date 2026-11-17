using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;

[Combinator]
[Description("Emits once when the expected number of cameras have reported ready, and throws an error naming how many reported if the timeout elapses first.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class WaitForCameras
{
    public WaitForCameras()
    {
        Timeout = 30;
    }

    [Description("The number of cameras that must report ready. Zero emits immediately without waiting.")]
    public int CameraCount { get; set; }

    [Description("The maximum time to wait for every camera to report ready, in seconds.")]
    public double Timeout { get; set; }

    public IObservable<int> Process<TSource>(IObservable<TSource> source)
    {
        return Observable.Create<int>(observer =>
        {
            var cameraCount = CameraCount;
            if (cameraCount <= 0)
            {
                observer.OnNext(0);
                observer.OnCompleted();
                return Disposable.Empty;
            }

            // Cameras report from their own acquisition threads, and the timer fires on another.
            var gate = new object();
            var reported = 0;
            var done = false;
            var timeout = TimeSpan.FromSeconds(Timeout);
            var timer = Observable.Timer(timeout).Subscribe(_ =>
            {
                lock (gate)
                {
                    if (done) return;
                    done = true;
                    observer.OnError(new TimeoutException(string.Format(
                        "Only {0} of {1} cameras reported ready within {2} s. The camera triggers were not started. " +
                        "Check that every camera is connected and not open in another program such as SpinView, " +
                        "and that the camera count in the rig configuration matches the cameras in the workflow.",
                        reported, cameraCount, timeout.TotalSeconds)));
                }
            });

            // Completion of the ready subject is ignored. Only the count of reports opens the gate.
            var subscription = source.Subscribe(_ =>
            {
                lock (gate)
                {
                    if (done) return;
                    if (++reported < cameraCount) return;
                    done = true;
                    observer.OnNext(reported);
                    observer.OnCompleted();
                }
            }, error =>
            {
                lock (gate)
                {
                    if (done) return;
                    done = true;
                    observer.OnError(error);
                }
            });

            return new CompositeDisposable(timer, subscription);
        });
    }
}
