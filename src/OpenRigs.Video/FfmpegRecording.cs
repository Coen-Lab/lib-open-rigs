using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Bonsai.Harp;
using OpenCV.Net;

namespace OpenRigs.Video
{
    /// <summary>
    /// One video file and its per-frame CSV, written by an ffmpeg.exe child process fed raw frames on
    /// its stdin by a thread of its own. Frames wait in a bounded queue: <see cref="Add"/> never
    /// blocks, and a frame that finds the queue full is dropped and counted. The CSV gets a row for
    /// each frame once it is in ffmpeg's stdin, so its rows are the video's frames, in order.
    /// </summary>
    internal sealed class FfmpegRecording
    {
        /// <summary>The CSV's header: the Harp time of the frame's trigger, then the camera's frame counter and clock. Exposure and gain are fixed while recording and saved once in the experiment's settings.</summary>
        public const string CsvHeader = "harpTime,frameId,cameraClockNs";

        readonly string fileName;
        readonly double frameRate;
        readonly VideoEncoder encoder;
        readonly TimeSpan stopTimeout;
        readonly TextWriter log;
        readonly BlockingCollection<Timestamped<VideoDataFrame>> queue;
        readonly int warnAt;
        readonly Thread thread;
        readonly ManualResetEventSlim done = new ManualResetEventSlim();
        readonly object gate = new object();
        readonly Queue<string> errorLines = new Queue<string>();

        Process? process;
        Stream? stdin;
        StreamWriter? csv;
        byte[]? buffer;
        int width, height, channels;
        Thread? deadline;
        Thread[] readers = new Thread[0];
        bool ending, deadlinePassed, warnedFull, behind, closed;
        long received, written, dropped;
        int? exitCode;
        string? failure;  // the first thing that went wrong, null while all is well
        string? problem;  // what Finish reports, set once the writer is done

        /// <summary>Starts the writer thread; ffmpeg starts at the first frame, which sets the frame size.</summary>
        public FfmpegRecording(string fileName, double frameRate, VideoEncoder encoder, int queueCapacity, TimeSpan stopTimeout, TextWriter log)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("The video's FileName is empty.");
            if (!(frameRate > 0)) throw new ArgumentOutOfRangeException(nameof(frameRate), "The video's FrameRate must be above 0 frames a second.");
            this.fileName = Path.GetFullPath(fileName);
            if (File.Exists(this.fileName)) throw new IOException("The video " + this.fileName + " already exists, and a recording never writes over one.");
            if (File.Exists(CsvFileName)) throw new IOException("The frame log " + CsvFileName + " already exists, and a recording never writes over one.");
            this.frameRate = frameRate;
            this.encoder = encoder;
            this.stopTimeout = stopTimeout;
            this.log = log;
            queue = new BlockingCollection<Timestamped<VideoDataFrame>>(new ConcurrentQueue<Timestamped<VideoDataFrame>>(), queueCapacity);
            warnAt = Math.Max(1, queueCapacity / 5);
            // A background thread while recording, so a workflow that never ends it cannot hold the
            // process open; End makes it a foreground thread until the file is closed.
            thread = new Thread(Run) { IsBackground = true, Name = "ffmpeg " + Path.GetFileName(this.fileName) };
            thread.Start();
        }

        /// <summary>The CSV beside the video: the same name with .csv.</summary>
        public string CsvFileName => Path.ChangeExtension(fileName, ".csv");

        /// <summary>The file a recording that went wrong leaves beside the video, saying what went wrong.</summary>
        public string ErrorFileName => Path.ChangeExtension(fileName, ".error.txt");

        /// <summary>Frames offered so far.</summary>
        public long Received => Interlocked.Read(ref received);

        /// <summary>Frames in ffmpeg's stdin, each with its CSV row.</summary>
        public long Written => Interlocked.Read(ref written);

        /// <summary>Frames dropped because the queue was full.</summary>
        public long Dropped => Interlocked.Read(ref dropped);

        /// <summary>Frames waiting for the writer.</summary>
        public int Waiting => queue.Count;

        /// <summary>The ffmpeg process's id once it has started; for tests.</summary>
        internal int? FfmpegProcessId { get { lock (gate) return process?.Id; } }

        /// <summary>For tests: runs on the writer's thread once ffmpeg has started, before it is handed over.</summary>
        internal Action? Started { get; set; }

        /// <summary>ffmpeg's exit code once it has exited; null before, or if it never started or was killed and did not report.</summary>
        public int? ExitCode { get { lock (gate) return exitCode; } }

        /// <summary>
        /// Queues a frame for the writer, never blocking: when the queue is full the frame is dropped
        /// and counted. Throws if the recording has already failed (ffmpeg exited, a write failed).
        /// </summary>
        public void Add(Timestamped<VideoDataFrame> frame)
        {
            lock (gate)
            {
                if (failure != null) throw new InvalidOperationException(failure);
                if (ending) throw new InvalidOperationException("The recording of " + fileName + " has ended; no frame can be added.");
            }
            if (frame.Value == null || frame.Value.Image == null) return;
            Interlocked.Increment(ref received);
            bool added;
            try { added = queue.TryAdd(frame); }
            catch (InvalidOperationException)
            {
                // Ended on another thread since the check above (a Stop racing the camera's last frame).
                Interlocked.Decrement(ref received);
                return;
            }
            if (!added)
            {
                var count = Interlocked.Increment(ref dropped);
                if (!warnedFull)
                {
                    warnedFull = true;
                    Warn("the queue of " + queue.BoundedCapacity + " frames is full: frame " + frame.Value.ChunkData.FrameID +
                         " was dropped, and every frame that finds it full will be (counted, reported at Stop)");
                }
                return;
            }
            var waiting = queue.Count;
            if (!behind && waiting >= warnAt)
            {
                behind = true;
                Warn(waiting + " frames are waiting for ffmpeg (of " + queue.BoundedCapacity + " the queue holds): the encoder is behind");
            }
            else if (behind && waiting < warnAt / 2)
            {
                behind = false;
                Warn("ffmpeg has caught up (" + waiting + " frames waiting)");
            }
        }

        /// <summary>
        /// Ends the recording and waits, at most the stop timeout and a second, for the queue to
        /// drain and ffmpeg to close the file. Returns what went wrong (frames dropped, ffmpeg
        /// failed or had to be killed), or null when the file holds every frame offered.
        /// </summary>
        public string? Finish()
        {
            End();
            // Once the deadline has killed ffmpeg the writer only has to see it exit: up to 5 s more for that, as
            // a loaded CI runner took over a second, and the report then says it was killed.
            if (!done.Wait(stopTimeout + TimeSpan.FromSeconds(1)) && !(DeadlinePassed && done.Wait(TimeSpan.FromSeconds(5))))
            {
                return "The recording of " + fileName + " did not finish within " + stopTimeout.TotalSeconds + " s of its end.";
            }
            lock (gate) return problem;
        }

        /// <summary>
        /// Ends the recording without waiting: the writer finishes on its own, and the process
        /// stays open until it has (at most the stop timeout). What goes wrong is written to
        /// stderr, the trace and a .error.txt beside the video, since nothing downstream can take it.
        /// </summary>
        public void End()
        {
            lock (gate)
            {
                if (ending) return;
                ending = true;
                // The file must close by then, or ffmpeg is killed so the writer can end. A thread of its
                // own, not a timer: a busy thread pool ran a timer's kill seconds late (CI).
                deadline = new Thread(() => { if (!done.Wait(stopTimeout)) Expire(); }) { IsBackground = true, Name = "ffmpeg deadline " + Path.GetFileName(fileName) };
                deadline.Start();
            }
            try { thread.IsBackground = false; }
            catch (ThreadStateException) { }  // already finished
            queue.CompleteAdding();
        }

        bool DeadlinePassed { get { lock (gate) return deadlinePassed; } }

        /// <summary>Waits for the writer to finish, at most a timeout; true when it has.</summary>
        public bool Wait(TimeSpan timeout) => done.Wait(timeout);

        void Expire()
        {
            lock (gate)
            {
                if (done.IsSet) return;
                deadlinePassed = true;
                Fail("ffmpeg did not close " + fileName + " within " + stopTimeout.TotalSeconds +
                     " s of the recording's end and was killed; the file keeps every whole cluster written (Matroska), " +
                     Math.Max(0, Received - Dropped - Written) + " queued frames were not written");
            }
            Kill();
        }

        void Run()
        {
            try
            {
                foreach (var frame in queue.GetConsumingEnumerable())
                {
                    if (Failed()) continue;  // drain, so the queue's frames are released
                    try { Write(frame); }
                    catch (RecordingException ex)
                    {
                        // A frame ffmpeg cannot take: close its stdin, so it encodes what it holds
                        // and finishes the file, rather than killing it.
                        Fail(ex.Message);
                        Close();
                    }
                    catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                    {
                        Fail(Explain(ex));
                        Kill();
                    }
                }
                // Every row is written and flushed once the queue has drained: the CSV is closed now,
                // not after ffmpeg exits, since ExperimentManifest's final write, which does not wait
                // for ffmpeg, reads a file still open as closed: false.
                CloseCsv();
                Close();
            }
            catch (Exception ex)
            {
                Fail("the recording's writer failed: " + ex.Message);
                Kill();
            }
            finally
            {
                CloseCsv();
                Report();
                Process? ended;
                lock (gate)
                {
                    ended = process;
                    process = null;
                }
                ended?.Dispose();
                done.Set();
            }
        }

        // Closes the frame log, once: no row is written after it.
        void CloseCsv()
        {
            var closing = csv;
            csv = null;
            try { closing?.Dispose(); }
            catch (IOException ex) { Fail("the frame log " + CsvFileName + " could not be closed: " + ex.Message); }
        }

        bool Failed()
        {
            lock (gate) return failure != null;
        }

        void Write(Timestamped<VideoDataFrame> frame)
        {
            var image = frame.Value.Image;
            if (process == null) Start(image);
            if (image.Width != width || image.Height != height || image.Channels != channels || image.Depth != IplDepth.U8)
            {
                throw new RecordingException("frame " + frame.Value.ChunkData.FrameID + " is " + Describe(image) +
                    ", but the video was started at " + width + " x " + height + " with " + channels + " channel(s) of 8 bits");
            }
            var rowBytes = width * channels;
            if (image.WidthStep == rowBytes) Marshal.Copy(image.ImageData, buffer!, 0, buffer!.Length);
            else
            {
                for (var row = 0; row < height; row++)
                {
                    Marshal.Copy(image.ImageData + row * image.WidthStep, buffer!, row * rowBytes, rowBytes);
                }
            }
            stdin!.Write(buffer!, 0, buffer!.Length);
            var chunk = frame.Value.ChunkData;
            // Each row reaches the file system as its frame reaches ffmpeg: a killed Bonsai leaves
            // ffmpeg its stdin's frames to finish the file with, and the CSV a row for each, short by
            // at most the frame in hand. One small write a frame (50 a second) costs nothing beside
            // the frame's own; a row buffered for a second would lose up to a second of rows.
            csv!.Write(frame.Seconds.ToString("R", CultureInfo.InvariantCulture) + "," +
                       chunk.FrameID.ToString(CultureInfo.InvariantCulture) + "," +
                       chunk.Timestamp.ToString(CultureInfo.InvariantCulture) + "\r\n");
            csv.Flush();
            Interlocked.Increment(ref written);
        }

        void Start(IplImage image)
        {
            if (image.Depth != IplDepth.U8 || (image.Channels != 1 && image.Channels != 3))
            {
                throw new RecordingException("the first frame is " + Describe(image) +
                    "; the video takes 8-bit frames with 1 channel (Mono8) or 3 (BGR)");
            }
            var refused = encoder.RefuseSize(image.Width, image.Height);
            if (refused != null) throw new RecordingException("the video cannot be started: " + refused);
            width = image.Width;
            height = image.Height;
            channels = image.Channels;
            buffer = new byte[width * height * channels];
            Directory.CreateDirectory(Path.GetDirectoryName(fileName)!);
            var info = new ProcessStartInfo(encoder.FfmpegPath, encoder.Arguments(width, height, channels == 1 ? "gray" : "bgr24", frameRate, fileName))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            var started = new Process { StartInfo = info };
            try { started.Start(); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                started.Dispose();
                throw new RecordingException(encoder.FfmpegPath + " could not be started: " + ex.Message);
            }
            // ffmpeg's output is read on threads of its own, not the thread pool's (Process's
            // BeginErrorReadLine), so its last words are in when it exits however busy the pool is.
            readers = new[] { ReadLines(started.StandardError), ReadLines(started.StandardOutput) };
            Started?.Invoke();
            bool late;
            lock (gate)
            {
                process = started;
                late = deadlinePassed;
            }
            // A deadline that passed while ffmpeg was starting killed nothing: killed here, the frame's
            // write fails rather than blocking for good on an ffmpeg nobody ends.
            if (late) Kill();
            stdin = started.StandardInput.BaseStream;
            // Created once ffmpeg runs, so an ffmpeg that cannot start leaves no header-only CSV.
            try
            {
                csv = new StreamWriter(new FileStream(CsvFileName, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                csv.Write(CsvHeader + "\r\n");
                csv.Flush();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new RecordingException("the frame log " + CsvFileName + " could not be created: " + ex.Message);
            }
        }

        // Closes ffmpeg's stdin, so it encodes what it holds and closes the file, and waits for it,
        // at most until the deadline. Once only.
        void Close()
        {
            if (process == null || closed) return;
            closed = true;
            try { stdin!.Close(); }
            catch (IOException ex) { Fail(Explain(ex)); }
            var exited = process.WaitForExit((int)stopTimeout.TotalMilliseconds);
            if (exited) ReadToTheEnd();
            lock (gate)
            {
                if (exited && !deadlinePassed)
                {
                    exitCode = process.ExitCode;
                    if (exitCode != 0) Fail("ffmpeg exited with code " + exitCode + LastErrors());
                }
            }
            if (!exited)
            {
                Fail("ffmpeg did not exit within " + stopTimeout.TotalSeconds + " s of the recording's end and was killed");
                Kill();
            }
        }

        string Explain(Exception ex)
        {
            Process? running;
            lock (gate) running = process;
            if (running != null && running.WaitForExit(2000))
            {
                ReadToTheEnd();  // ffmpeg's stderr says why
                lock (gate) exitCode = deadlinePassed ? null : running.ExitCode;
                return "ffmpeg stopped while recording (exit code " + running.ExitCode + ")" + LastErrors();
            }
            return "writing to ffmpeg failed: " + ex.Message + LastErrors();
        }

        void Kill()
        {
            Process? running;
            lock (gate) running = process;
            try
            {
                if (running != null && !running.HasExited) running.Kill();
            }
            catch (InvalidOperationException) { }  // exited meanwhile
            catch (System.ComponentModel.Win32Exception) { }  // exiting
        }

        void Fail(string what)
        {
            lock (gate) failure ??= what;
        }

        Thread ReadLines(StreamReader reader)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null) Remember(line);
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }  // closed with the process
            }) { IsBackground = true, Name = "ffmpeg output " + Path.GetFileName(fileName) };
            thread.Start();
            return thread;
        }

        // Once ffmpeg has exited: its output read to the end, at most 2 s (a child it left could hold the pipe open).
        void ReadToTheEnd()
        {
            foreach (var reader in readers) reader.Join(TimeSpan.FromSeconds(2));
        }

        void Remember(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (errorLines)
            {
                errorLines.Enqueue(line!);
                while (errorLines.Count > 20) errorLines.Dequeue();
            }
        }

        string LastErrors()
        {
            lock (errorLines) return errorLines.Count == 0 ? "" : ": " + string.Join(" | ", errorLines);
        }

        // What went wrong, as one message, also on stderr, the trace and the .error.txt file.
        void Report()
        {
            var problems = new List<string>();
            lock (gate)
            {
                if (failure != null) problems.Add(failure);
            }
            var lost = Dropped;
            if (lost > 0) problems.Add(lost + " of " + Received + " frames were dropped because the queue of " + queue.BoundedCapacity +
                                       " frames was full (the encoder fell behind); the video and its CSV hold the other " + Written);
            if (problems.Count == 0) return;
            var message = "Video " + fileName + ": " + string.Join("; ", problems) + ".";
            lock (gate) problem = message;
            Trace.TraceError(message);
            log.WriteLine("ERROR " + message);
            try { File.WriteAllText(ErrorFileName, message + Environment.NewLine); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { log.WriteLine("ERROR " + ErrorFileName + " could not be written: " + ex.Message); }
        }

        void Warn(string what)
        {
            var message = "Video " + Path.GetFileName(fileName) + ": " + what + ".";
            Trace.TraceWarning(message);
            log.WriteLine("WARNING " + message);
        }

        static string Describe(IplImage image)
        {
            return image.Width + " x " + image.Height + " with " + image.Channels + " channel(s) of " + image.Depth;
        }

        // A frame the video cannot take, or an ffmpeg that cannot start: the recording's own refusal.
        sealed class RecordingException : Exception
        {
            public RecordingException(string message) : base(message) { }
        }
    }
}
