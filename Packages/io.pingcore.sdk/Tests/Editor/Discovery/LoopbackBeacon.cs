using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>
    /// A WebSocket latency beacon on an ephemeral loopback port, over raw TCP (the RFC 6455 server
    /// side, no <c>HttpListener</c>, so no URL reservation): it answers each text <c>ping</c> with
    /// <c>pong</c> after exactly <see cref="Hold"/>, spun on a <see cref="Stopwatch"/> because
    /// <c>Thread.Sleep</c> on Windows rounds to the 15.6 ms timer tick. Each connection has its own
    /// thread. A test fixture only; the runtime never starts a thread.
    /// </summary>
    internal sealed class LoopbackBeacon : IDisposable
    {
        private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly TcpListener listener;
        private readonly List<TcpClient> clients = new List<TcpClient>();
        private int pings;
        private volatile bool stopping;

        private LoopbackBeacon(TimeSpan hold)
        {
            Hold = hold;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            new Thread(AcceptLoop) { IsBackground = true, Name = "LoopbackBeacon accept" }.Start();
        }

        /// <summary>How long each pong is held after its ping is read.</summary>
        public TimeSpan Hold { get; }

        public int Port { get; }

        public string Url => "ws://127.0.0.1:" + Port + "/";

        /// <summary>Text pings answered so far, over every connection.</summary>
        public int Pings => Volatile.Read(ref pings);

        public static LoopbackBeacon Start(TimeSpan hold) => new LoopbackBeacon(hold);

        public void Dispose()
        {
            stopping = true;
            listener.Stop();
            lock (clients)
            {
                foreach (TcpClient client in clients)
                {
                    client.Close();
                }
            }
        }

        private void AcceptLoop()
        {
            while (!stopping)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    return;
                }

                lock (clients)
                {
                    clients.Add(client);
                }

                new Thread(() => Serve(client)) { IsBackground = true, Name = "LoopbackBeacon connection" }.Start();
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();
                string key = ReadUpgradeKey(stream);
                string accept;
                using (SHA1 sha1 = SHA1.Create())
                {
                    accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + AcceptGuid)));
                }

                byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                stream.Write(response, 0, response.Length);
                stream.Flush();

                while (!stopping)
                {
                    (int opcode, byte[] payload) = ReadFrame(stream);
                    if (opcode == 0x8)
                    {
                        WriteFrame(stream, 0x8, payload);
                        return;
                    }

                    if (opcode == 0x9)
                    {
                        WriteFrame(stream, 0xA, payload);
                        continue;
                    }

                    if (opcode == 0x1 && Encoding.UTF8.GetString(payload) == "ping")
                    {
                        Stopwatch held = Stopwatch.StartNew();
                        while (held.Elapsed < Hold)
                        {
                            Thread.SpinWait(20);
                        }

                        Interlocked.Increment(ref pings);
                        WriteFrame(stream, 0x1, Encoding.UTF8.GetBytes("pong"));
                    }
                }
            }
            catch (Exception)
            {
                // The probe aborts its socket when it is done; the connection ends either way.
            }
            finally
            {
                client.Close();
            }
        }

        private static string ReadUpgradeKey(Stream stream)
        {
            var head = new StringBuilder();
            while (head.Length < 4 || head.ToString(head.Length - 4, 4) != "\r\n\r\n")
            {
                int b = stream.ReadByte();
                if (b < 0 || head.Length > 8192)
                {
                    throw new IOException("no upgrade request");
                }

                head.Append((char)b);
            }

            foreach (string line in head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && string.Equals(line.Substring(0, colon).Trim(), "Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(colon + 1).Trim();
                }
            }

            throw new IOException("no Sec-WebSocket-Key");
        }

        private static (int Opcode, byte[] Payload) ReadFrame(Stream stream)
        {
            byte[] head = ReadExactly(stream, 2);
            int opcode = head[0] & 0x0F;
            bool masked = (head[1] & 0x80) != 0;
            long length = head[1] & 0x7F;
            if (length == 126)
            {
                byte[] ext = ReadExactly(stream, 2);
                length = (ext[0] << 8) | ext[1];
            }
            else if (length == 127)
            {
                byte[] ext = ReadExactly(stream, 8);
                length = 0;
                for (int i = 0; i < 8; i++)
                {
                    length = (length << 8) | ext[i];
                }
            }

            if (length > 65536)
            {
                throw new IOException("frame too large for a beacon");
            }

            byte[] mask = masked ? ReadExactly(stream, 4) : null;
            byte[] payload = ReadExactly(stream, (int)length);
            if (mask != null)
            {
                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] ^= mask[i % 4];
                }
            }

            return (opcode, payload);
        }

        private static void WriteFrame(Stream stream, int opcode, byte[] payload)
        {
            if (payload.Length > 125)
            {
                throw new IOException("the beacon only sends short frames");
            }

            var frame = new byte[2 + payload.Length];
            frame[0] = (byte)(0x80 | opcode);
            frame[1] = (byte)payload.Length;
            Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);
            stream.Write(frame, 0, frame.Length);
            stream.Flush();
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            var bytes = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(bytes, read, count - read);
                if (n <= 0)
                {
                    throw new IOException("the connection closed");
                }

                read += n;
            }

            return bytes;
        }
    }
}
