using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Logging.Tests
{
    /// <summary>
    /// BinaryLogWriter's foreground thread ends, and the file closes, on each way a subscription
    /// ends: the source completes (never disposed by hand), the subscription is disposed while the
    /// source still runs, or the file cannot be written.
    /// </summary>
    [TestClass]
    public class BinaryLogWriterTests
    {
        string folder;

        [TestInitialize]
        public void Initialize()
        {
            folder = Path.Combine(Path.GetTempPath(), "binary-log-" + Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        // The writer's thread holds the file open until it ends: an exclusive open succeeds only then.
        static bool Closes(string path)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return true;
                }
                catch (IOException) { Thread.Sleep(10); }
            }
            return false;
        }

        [TestMethod]
        public void TheSourceCompleting_ClosesTheFileWithoutADispose()
        {
            var path = Path.Combine(folder, "a.bin");
            new BinaryLogWriter { FileName = path }.Process(new[] { new byte[] { 1, 2 }, new byte[] { 3 } }.ToObservable()).Subscribe(_ => { });
            Assert.IsTrue(Closes(path));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }

        [TestMethod]
        public void ADisposeWhileTheSourceRuns_ClosesTheFile()
        {
            var path = Path.Combine(folder, "b.bin");
            var source = new Subject<byte[]>();
            var subscription = new BinaryLogWriter { FileName = path }.Process(source).Subscribe(_ => { });
            source.OnNext(new byte[] { 7 });
            SpinWait.SpinUntil(() => File.Exists(path), TimeSpan.FromSeconds(5));
            Assert.ThrowsException<IOException>(() => File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose(), "held open while the source runs");
            subscription.Dispose();
            Assert.IsTrue(Closes(path));
            CollectionAssert.AreEqual(new byte[] { 7 }, File.ReadAllBytes(path));
        }

        [TestMethod]
        public void AFileThatExists_IsAnErrorAndTheThreadEnds()
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "c.bin");
            File.WriteAllBytes(path, new byte[] { 9 });
            var source = new Subject<byte[]>();
            Exception error = null;
            var failed = new ManualResetEventSlim();
            new BinaryLogWriter { FileName = path }.Process(source).Subscribe(_ => { }, ex => { error = ex; failed.Set(); });
            Assert.IsTrue(failed.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsInstanceOfType(error, typeof(IOException));
            source.OnNext(new byte[] { 1 });  // after the error: dropped, nothing waits for it
            CollectionAssert.AreEqual(new byte[] { 9 }, File.ReadAllBytes(path));
        }
    }
}
