using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Xml.Serialization;
using Bonsai;

namespace OpenRigs.Logging
{
    /// <summary>
    /// Represents an operator that writes each byte array in the sequence to a raw binary file, like
    /// the Bonsai MatrixWriter, on a thread that keeps the process open until the file is closed.
    /// </summary>
    /// <remarks>
    /// MatrixWriter opens, writes and closes its file on a background thread. Headless Bonsai
    /// (--no-editor, as a rig runs it) returns as soon as the workflow completes, and the process
    /// ends with that thread wherever it was: a Harp log written that way could miss its last
    /// messages, or never be created. This writer's thread is a foreground thread, so the process
    /// ends only after the file is closed. Writes stay off the source's thread, as MatrixWriter's do.
    /// </remarks>
    [XmlType(Namespace = "clr-namespace:OpenRigs.Logging;assembly=OpenRigs.Logging")]
    [Description("Writes each byte array in the sequence to a raw binary file, which must not exist yet. The file is written on its own thread, which keeps Bonsai open until the file is closed, so headless Bonsai cannot exit with bytes unwritten.")]
    public class BinaryLogWriter : Sink<byte[]>
    {
        /// <summary>
        /// Gets or sets the name of the file to write.
        /// </summary>
        [Description("The name of the file to write. Its folder is created if needed.")]
        [Editor("Bonsai.Design.SaveFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string FileName { get; set; }

        /// <summary>
        /// Writes each byte array in the sequence to the file, in order.
        /// </summary>
        public override IObservable<byte[]> Process(IObservable<byte[]> source)
        {
            var path = FileName;
            return Observable.Create<byte[]>(observer =>
            {
                if (string.IsNullOrEmpty(path))
                {
                    throw new InvalidOperationException("A valid file name must be specified.");
                }

                var pending = new BlockingCollection<byte[]>();
                // The writer thread's OnError and the source's OnNext must never overlap.
                observer = Observer.Synchronize(observer, new object());
                new Thread(() => Write(path, pending, observer))
                {
                    IsBackground = false,
                    Name = "BinaryLogWriter " + System.IO.Path.GetFileName(path)
                }.Start();

                var subscription = source.Do(input =>
                {
                    // Under the lock so that an element racing a Stop on another thread is dropped
                    // rather than added after the end.
                    lock (pending)
                    {
                        if (!pending.IsAddingCompleted) pending.Add(input);
                    }
                }).SubscribeSafe(observer);

                return new CompositeDisposable(
                    subscription,
                    Disposable.Create(() =>
                    {
                        lock (pending) pending.CompleteAdding();
                    }));
            });
        }

        static void Write(string path, BlockingCollection<byte[]> pending, IObserver<byte[]> observer)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                using (var stream = new FileStream(path, FileMode.CreateNew))
                {
                    foreach (var bytes in pending.GetConsumingEnumerable())
                    {
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch (Exception error)
            {
                observer.OnError(error);
            }
        }
    }
}
