using Bonsai;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reactive.Linq;

namespace OpenRigs.Devices.Zapit
{
    /// <summary>
    /// TCP client for the Zapit optogenetics server. Connects when subscribed; each command is sent
    /// and its reply read before the next, off the caller's thread. Before disconnecting, however the
    /// workflow stops, it sends a StopStimulus, so a continuous stimulus never outlives the workflow.
    /// </summary>
    [Combinator]
    [Description("TCP client for the Zapit optogenetics server: sends each ZapitCommand and returns its ZapitReply, one at a time, off the trial logic's thread. Sends StopStimulus before disconnecting, however the workflow stops.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ZapitClient
    {
        /// <summary>Gets or sets the host running Zapit.</summary>
        [Description("Host name or IP address of the PC running Zapit; 127.0.0.1 when Zapit runs on this PC.")]
        public string Host { get; set; } = "127.0.0.1";

        /// <summary>Gets or sets the Zapit server's TCP port.</summary>
        [Description("TCP port of the Zapit server: tcpServer.port in the Zapit settings file.")]
        public int Port { get; set; } = 1488;

        /// <summary>Gets or sets how long to wait for a reply, in milliseconds.</summary>
        [Description("Milliseconds to wait for a reply before failing. Zapit answers StartStimulus only after the waveforms are on the DAQ.")]
        public int ReceiveTimeout { get; set; } = 5000;

        /// <summary>Sends each command and returns its reply.</summary>
        public IObservable<ZapitReply> Process(IObservable<ZapitCommand> source)
        {
            return Observable.Using(
                () => new ZapitConnection(Host, Port, ReceiveTimeout),
                connection => source
                    .Select(command => Observable.Defer(() => Observable.Start(() => connection.SendReceive(command))))
                    .Concat());
        }

        sealed class ZapitConnection : IDisposable
        {
            const int StopReplyTimeout = 1000;

            readonly TcpClient client;
            readonly NetworkStream stream;
            readonly object gate = new object();
            readonly string endpoint;
            bool disposed;

            public ZapitConnection(string host, int port, int receiveTimeout)
            {
                endpoint = string.Format("{0}:{1}", host, port);
                client = new TcpClient();
                try
                {
                    client.Connect(host, port);
                }
                catch (SocketException ex)
                {
                    client.Close();
                    throw new InvalidOperationException(string.Format(
                        "Could not connect to Zapit at {0}. Is Zapit running with tcpServer.enable true in its settings file? ({1})",
                        endpoint, ex.Message), ex);
                }
                client.NoDelay = true;
                client.ReceiveTimeout = receiveTimeout;
                stream = client.GetStream();
            }

            public ZapitReply SendReceive(ZapitCommand command)
            {
                var request = command.ToBytes();
                var response = new byte[ZapitReply.MessageLength];
                lock (gate)
                {
                    if (disposed) throw new ObjectDisposedException("Zapit connection to " + endpoint);
                    try
                    {
                        stream.Write(request, 0, request.Length);
                        var read = 0;
                        while (read < response.Length)
                        {
                            var count = stream.Read(response, read, response.Length - read);
                            if (count <= 0) throw new IOException("Zapit closed the connection.");
                            read += count;
                        }
                    }
                    catch (IOException ex)
                    {
                        throw new IOException(string.Format("Zapit at {0} did not answer {1}: {2}", endpoint, command, ex.Message), ex);
                    }
                }
                var reply = ZapitReply.Parse(response);
                if (reply.Command != command.Command)
                {
                    throw new InvalidOperationException(string.Format(
                        "Zapit at {0} is out of step: it answered {1} while {2} was pending.", endpoint, reply.Command, command.Command));
                }
                return reply;
            }

            public void Dispose()
            {
                // The gate waits for a request in flight, so StopStimulus is the last command sent.
                lock (gate)
                {
                    if (disposed) return;
                    disposed = true;
                    TryStopStimulus();
                    stream.Dispose();
                    client.Close();
                }
            }

            // Best effort: the connection is closing and Zapit may already be gone.
            void TryStopStimulus()
            {
                try
                {
                    var deadline = Stopwatch.StartNew();
                    client.SendTimeout = StopReplyTimeout;
                    var request = ZapitCommand.Stop().ToBytes();
                    stream.Write(request, 0, request.Length);
                    var response = new byte[ZapitReply.MessageLength];
                    var read = 0;
                    while (read < response.Length)
                    {
                        var remaining = StopReplyTimeout - (int)deadline.ElapsedMilliseconds;
                        if (remaining <= 0) break;
                        client.ReceiveTimeout = remaining;
                        var count = stream.Read(response, read, response.Length - read);
                        if (count <= 0) break;
                        read += count;
                    }
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
