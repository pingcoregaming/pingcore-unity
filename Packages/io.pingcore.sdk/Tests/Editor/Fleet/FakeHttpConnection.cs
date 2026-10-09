using System;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>
    /// The smallest HTTP/1.1 server side the fake needs, on one accepted TCP connection: read one
    /// request (<see cref="FakeHttpRequestReader"/>), then either one answer with <c>Connection: close</c>,
    /// or a chunked stream (the watch) written until it ends.
    /// Raw TCP rather than <c>HttpListener</c> so the fake can stop accepting while a stream stays
    /// open, as Node's <c>server.close()</c> does, and can end a connection with a reset.
    /// </summary>
    internal sealed class FakeHttpConnection
    {
        private readonly object writeLock = new object();
        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private bool ended;

        public FakeHttpConnection(TcpClient client)
        {
            this.client = client;
            client.NoDelay = true;
            client.ReceiveTimeout = 10000;
            stream = client.GetStream();
        }

        /// <summary>True once the connection was ended or a write to it failed.</summary>
        public bool Ended
        {
            get
            {
                lock (writeLock)
                {
                    return ended;
                }
            }
        }

        /// <summary>Reads one request (<see cref="FakeHttpRequestReader"/>); throws on a malformed one or a connection that went away.</summary>
        public FakeRequest ReadRequest()
        {
            return FakeHttpRequestReader.Read(stream, () => WriteRaw(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n")));
        }

        /// <summary>Writes one whole answer and closes the connection gracefully.</summary>
        public void Respond(int status, string body, string contentType)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body ?? string.Empty);
            string head = "HTTP/1.1 " + status + " " + Reason(status) + "\r\n"
                + "Content-Type: " + contentType + "\r\n"
                + "Content-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                + "Connection: close\r\n\r\n";
            WriteRaw(Concat(Encoding.ASCII.GetBytes(head), payload));
            End(false, false);
        }

        /// <summary>Writes the head of a chunked 200 stream; the body follows through <see cref="WriteChunk"/>.</summary>
        public void BeginStream(string contentType)
        {
            WriteRaw(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: " + contentType + "\r\nCache-Control: no-cache\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"));
        }

        /// <summary>Writes one chunk; a failed write ends the connection.</summary>
        public void WriteChunk(byte[] data)
        {
            if (data.Length == 0)
            {
                return;
            }

            byte[] size = Encoding.ASCII.GetBytes(data.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
            WriteRaw(Concat(Concat(size, data), Encoding.ASCII.GetBytes("\r\n")));
        }

        /// <summary>Ends the connection: a reset when <paramref name="abort"/>, else the last chunk of a stream and a graceful close.</summary>
        public void End(bool abort, bool streaming = true)
        {
            lock (writeLock)
            {
                if (ended)
                {
                    return;
                }

                ended = true;
                try
                {
                    if (abort)
                    {
                        client.Client.LingerState = new LingerOption(true, 0);
                        client.Client.Close();
                        return;
                    }

                    if (streaming)
                    {
                        byte[] last = Encoding.ASCII.GetBytes("0\r\n\r\n");
                        stream.Write(last, 0, last.Length);
                    }

                    stream.Flush();
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                catch (Exception)
                {
                    // The client went away first.
                }
                finally
                {
                    client.Close();
                }
            }
        }

        private void WriteRaw(byte[] bytes)
        {
            lock (writeLock)
            {
                if (ended)
                {
                    return;
                }

                try
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
                catch (Exception)
                {
                    ended = true;
                    client.Close();
                }
            }
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var all = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, all, 0, a.Length);
            Buffer.BlockCopy(b, 0, all, a.Length, b.Length);
            return all;
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 404: return "Not Found";
                case 409: return "Conflict";
                case 500: return "Internal Server Error";
                case 501: return "Not Implemented";
                case 503: return "Service Unavailable";
                default: return "Status";
            }
        }
    }
}
