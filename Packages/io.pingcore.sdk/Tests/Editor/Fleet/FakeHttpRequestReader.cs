using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>One request the fake received.</summary>
    internal sealed class FakeRequest
    {
        public FakeRequest(string method, string path, string body, IReadOnlyDictionary<string, string> headers)
        {
            Method = method;
            Path = path;
            Body = body;
            Headers = headers;
        }

        public string Method { get; }

        public string Path { get; }

        public string Body { get; }

        /// <summary>The request headers, keys case-insensitive.</summary>
        public IReadOnlyDictionary<string, string> Headers { get; }

        public bool IsWrite => Method != "GET";

        /// <summary>The <c>Connection</c> header, or null when the client sent none.</summary>
        public string Connection => Headers.TryGetValue("Connection", out string value) ? value : null;

        public override string ToString() => Method + " " + Path;
    }

    /// <summary>
    /// The request side of <see cref="FakeHttpConnection"/>: reads one HTTP/1.1 request (Content-Length or
    /// chunked body) from a stream, and asks for <c>100 Continue</c> when the client expects it.
    /// </summary>
    internal static class FakeHttpRequestReader
    {
        private const int MaxHeaderBytes = 64 * 1024;

        /// <summary>Reads one request; throws on a malformed one or a connection that went away.</summary>
        /// <param name="stream">The connection's stream.</param>
        /// <param name="sendContinue">Writes <c>100 Continue</c>; called before the body when the request expects it.</param>
        public static FakeRequest Read(Stream stream, Action sendContinue)
        {
            string head = ReadHead(stream);
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] start = lines[0].Split(' ');
            if (start.Length < 3)
            {
                throw new InvalidDataException("bad request line: " + lines[0]);
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                }
            }

            if (headers.TryGetValue("Expect", out string expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                sendContinue();
            }

            string body = null;
            if (headers.TryGetValue("Transfer-Encoding", out string encoding) && encoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                body = ReadChunkedBody(stream);
            }
            else if (headers.TryGetValue("Content-Length", out string length) && int.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count > 0)
            {
                body = Encoding.UTF8.GetString(ReadExactly(stream, count));
            }

            string target = start[1];
            int query = target.IndexOf('?');
            return new FakeRequest(start[0], query < 0 ? target : target.Substring(0, query), body, headers);
        }

        private static string ReadHead(Stream stream)
        {
            var head = new MemoryStream();
            int matched = 0;
            while (matched < 4)
            {
                int b = stream.ReadByte();
                if (b < 0)
                {
                    throw new EndOfStreamException("the connection closed before the request head ended");
                }

                head.WriteByte((byte)b);
                matched = b == (matched % 2 == 0 ? '\r' : '\n') ? matched + 1 : (b == '\r' ? 1 : 0);
                if (head.Length > MaxHeaderBytes)
                {
                    throw new InvalidDataException("request head over " + MaxHeaderBytes + " bytes");
                }
            }

            string text = Encoding.ASCII.GetString(head.ToArray());
            return text.Substring(0, text.Length - 4);
        }

        private static string ReadChunkedBody(Stream stream)
        {
            var body = new MemoryStream();
            while (true)
            {
                string sizeLine = ReadLine(stream);
                int semicolon = sizeLine.IndexOf(';');
                int size = int.Parse(semicolon < 0 ? sizeLine : sizeLine.Substring(0, semicolon), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (size == 0)
                {
                    while (ReadLine(stream).Length > 0)
                    {
                        // Trailers are ignored.
                    }

                    return Encoding.UTF8.GetString(body.ToArray());
                }

                byte[] chunk = ReadExactly(stream, size);
                body.Write(chunk, 0, chunk.Length);
                ReadLine(stream);
            }
        }

        private static string ReadLine(Stream stream)
        {
            var line = new StringBuilder();
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0)
                {
                    throw new EndOfStreamException("the connection closed inside a chunked body");
                }

                if (b == '\n')
                {
                    return line.ToString().TrimEnd('\r');
                }

                line.Append((char)b);
            }
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0)
                {
                    throw new EndOfStreamException("the connection closed inside the request body");
                }

                read += n;
            }

            return buffer;
        }
    }
}
