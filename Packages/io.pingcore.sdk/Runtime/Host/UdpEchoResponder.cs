using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PingCore.Discovery.Host
{
    /// <summary>
    /// Answers Discovery's <c>udp-echo</c> reachability probe for a heartbeat-tier game server (a
    /// self-hosted dedicated server or a listen host). An open-registration app always verifies
    /// with <c>udp-echo</c>, so a game server that heartbeats to one is listed only while this
    /// answers. Bind it to the <c>queryPort</c> the heartbeat sends (<see cref="HeartbeatReporterOptions.QueryPort"/>,
    /// by default the game port plus one), because the game port belongs to the game's own transport.
    /// <para>
    /// It echoes exactly the 21-byte DSCV1 challenge (<c>DSCV1</c> plus a 16-byte nonce), byte for
    /// byte, from the same socket it arrived on, and drops every other datagram. Each source address
    /// gets at most 5 replies per 10 s and all sources together at most 100 (<c>EchoRateLimiter</c>),
    /// so the port cannot be used to reflect traffic. It receives with async socket calls (no thread,
    /// no timer), so on the Unity main thread replies go out on the next frame. The socket is dual
    /// mode (IPv6 and IPv4) where the platform allows it, else IPv4.
    /// </para>
    /// </summary>
    public sealed class UdpEchoResponder : IDisposable
    {
        // Windows reports an ICMP port unreachable for an earlier send as WSAECONNRESET on the next
        // receive; SIO_UDP_CONNRESET turned off stops that. Other platforms do not have it.
        private const int SioUdpConnReset = -1744830452;
        private const int ReceiveBufferBytes = 2048;

        private readonly Socket socket;
        private readonly EchoRateLimiter limiter;
        private readonly Func<DateTimeOffset> clock;
        private readonly object gate = new object();
        private readonly bool dualMode;
        private int echoed;
        private int dropped;
        private int throttled;
        private int disposed;

        private UdpEchoResponder(Socket socket, bool dualMode, EchoRateLimiter limiter, Func<DateTimeOffset> clock)
        {
            this.socket = socket;
            this.dualMode = dualMode;
            this.limiter = limiter;
            this.clock = clock;
            Port = ((IPEndPoint)socket.LocalEndPoint).Port;
        }

        /// <summary>The UDP port the responder is bound to (useful with port 0).</summary>
        public int Port { get; }

        /// <summary>Challenges answered.</summary>
        public int Echoed => Volatile.Read(ref echoed);

        /// <summary>Datagrams dropped because they were not a DSCV1 challenge.</summary>
        public int Dropped => Volatile.Read(ref dropped);

        /// <summary>Challenges dropped by the per-source or global cap.</summary>
        public int Throttled => Volatile.Read(ref throttled);

        /// <summary>
        /// Binds to <paramref name="port"/> on every local address and starts answering. Throws
        /// <see cref="ArgumentOutOfRangeException"/> for a port outside 0 to 65535 and
        /// <see cref="SocketException"/> when the port cannot be bound (for example, it is in use).
        /// </summary>
        public static UdpEchoResponder Bind(int port)
        {
            return Bind(port, new EchoRateLimiter(), () => DateTimeOffset.UtcNow);
        }

        /// <summary>Like <see cref="Bind(int)"/>, returning false with a message instead of throwing.</summary>
        public static bool TryBind(int port, out UdpEchoResponder responder, out string error)
        {
            responder = null;
            error = null;
            try
            {
                responder = Bind(port);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                error = "port " + port + " is not 0 to 65535";
            }
            catch (SocketException e)
            {
                error = "cannot bind UDP port " + port + ": " + e.SocketErrorCode;
            }

            return false;
        }

        /// <summary>Test seam: a responder with its own cap and clock.</summary>
        internal static UdpEchoResponder Bind(int port, EchoRateLimiter limiter, Func<DateTimeOffset> clock)
        {
            if (port < 0 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            Socket socket = null;
            bool dual = false;
            try
            {
                if (Socket.OSSupportsIPv6)
                {
                    try
                    {
                        socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                        socket.DualMode = true;
                        socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                        dual = true;
                    }
                    catch (Exception e) when (IsNoDualMode(e))
                    {
                        // Only a platform without IPv6 or dual mode falls back to IPv4. A busy port is
                        // not retried on IPv4: Windows would let that bind succeed beside the dual-mode
                        // socket holding the port, and the probe would reach the other socket.
                        socket?.Dispose();
                        socket = null;
                    }
                }

                if (socket == null)
                {
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.Bind(new IPEndPoint(IPAddress.Any, port));
                }

                TryDisableConnectionReset(socket);
                var responder = new UdpEchoResponder(socket, dual, limiter ?? new EchoRateLimiter(), clock ?? (() => DateTimeOffset.UtcNow));
                _ = responder.ReceiveLoopAsync();
                return responder;
            }
            catch
            {
                socket?.Dispose();
                throw;
            }
        }

        /// <summary>Stops answering and closes the socket.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1)
            {
                return;
            }

            socket.Dispose();
        }

        private bool IsDisposed => Volatile.Read(ref disposed) == 1;

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[ReceiveBufferBytes];
            EndPoint any = dualMode ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);
            while (!IsDisposed)
            {
                SocketReceiveFromResult received;
                try
                {
                    received = await socket.ReceiveFromAsync(new ArraySegment<byte>(buffer), SocketFlags.None, any);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException e)
                {
                    if (IsDisposed)
                    {
                        return;
                    }

                    // An oversized datagram (Windows reports it instead of truncating) is not a
                    // challenge; a reset is a late ICMP for an earlier reply. Both leave the socket usable.
                    if (e.SocketErrorCode == SocketError.MessageSize)
                    {
                        Interlocked.Increment(ref dropped);
                        continue;
                    }

                    if (e.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        continue;
                    }

                    return;
                }

                Handle(buffer, received.ReceivedBytes, received.RemoteEndPoint as IPEndPoint);
            }
        }

        private void Handle(byte[] buffer, int count, IPEndPoint remote)
        {
            if (remote == null || !EchoFrame.IsChallenge(buffer, count))
            {
                Interlocked.Increment(ref dropped);
                return;
            }

            IPAddress address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
            bool allowed;
            lock (gate)
            {
                allowed = limiter.TryAcquire(address.ToString(), clock());
            }

            if (!allowed)
            {
                Interlocked.Increment(ref throttled);
                return;
            }

            try
            {
                socket.SendTo(buffer, 0, count, SocketFlags.None, remote);
                Interlocked.Increment(ref echoed);
            }
            catch (SocketException)
            {
                // The prober is gone or unreachable; it retries on its own cadence.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static bool IsNoDualMode(Exception e)
        {
            if (e is NotSupportedException || e is PlatformNotSupportedException)
            {
                return true;
            }

            var socketError = e as SocketException;
            return socketError != null
                && (socketError.SocketErrorCode == SocketError.AddressFamilyNotSupported
                    || socketError.SocketErrorCode == SocketError.ProtocolNotSupported
                    || socketError.SocketErrorCode == SocketError.ProtocolFamilyNotSupported
                    || socketError.SocketErrorCode == SocketError.OperationNotSupported
                    || socketError.SocketErrorCode == SocketError.AddressNotAvailable
                    || socketError.SocketErrorCode == SocketError.InvalidArgument);
        }

        private static void TryDisableConnectionReset(Socket socket)
        {
            try
            {
                socket.IOControl((IOControlCode)SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (Exception e) when (e is SocketException || e is NotSupportedException || e is PlatformNotSupportedException || e is InvalidOperationException)
            {
                // Not Windows: no such control, and no such behaviour to turn off.
            }
        }
    }
}
