using NetMQ;
using System;
using System.Net;
using System.Net.Sockets;

namespace OpenRigs.Devices.Tests
{
    /// <summary>Loopback ports for workflows that bind their own.</summary>
    static class TestPorts
    {
        /// <summary>A port free now; another process can take it before the workflow binds it (see <see cref="Taken"/>).</summary>
        public static int Free()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>Whether <paramref name="error"/>, or an error inside it, is a port already in use.</summary>
        public static bool Taken(Exception error)
        {
            for (; error != null; error = error.InnerException)
                if (error is AddressAlreadyInUseException || (error is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse)) return true;
            return false;
        }
    }
}
