using System;
using System.Text;

namespace PingCore.Fleet
{
    /// <summary>
    /// Turns the watch stream's bytes into lines: split on LF, a trailing CR dropped, UTF-8
    /// decoded only once a line is whole (so a multi-byte character split across chunks
    /// survives), surrounding whitespace trimmed and blank lines skipped. A line longer than
    /// the bound ends the stream, like the fleet probe's <c>maxWatchLine</c>: the caller
    /// abandons the connection and reconnects.
    /// </summary>
    internal sealed class WatchLineSplitter
    {
        /// <summary>The default line bound: 4 MiB.</summary>
        public const int DefaultMaxLineBytes = 4 * 1024 * 1024;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        private readonly int maxLineBytes;
        private byte[] pending = new byte[1024];
        private int length;

        public WatchLineSplitter(int maxLineBytes = DefaultMaxLineBytes)
        {
            if (maxLineBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLineBytes));
            }

            this.maxLineBytes = maxLineBytes;
        }

        /// <summary>Bytes buffered for the line in progress.</summary>
        public int PendingBytes => length;

        /// <summary>
        /// Feeds one chunk. Every completed non-blank line goes to <paramref name="onLine"/> in
        /// order. Returns false when a line exceeded the bound; the splitter is then reset and
        /// the rest of the chunk is dropped.
        /// </summary>
        public bool Push(byte[] data, int offset, int count, Action<string> onLine)
        {
            int end = offset + count;
            int start = offset;
            for (int i = offset; i < end; i++)
            {
                if (data[i] != (byte)'\n')
                {
                    continue;
                }

                if (!Append(data, start, i - start))
                {
                    return false;
                }

                Emit(onLine);
                start = i + 1;
            }

            return Append(data, start, end - start);
        }

        /// <summary>At end of stream: emits a final line that had no LF.</summary>
        public void Finish(Action<string> onLine)
        {
            if (length > 0)
            {
                Emit(onLine);
            }
        }

        private bool Append(byte[] data, int offset, int count)
        {
            if (count == 0)
            {
                return true;
            }

            if ((long)length + count > maxLineBytes)
            {
                length = 0;
                return false;
            }

            if (length + count > pending.Length)
            {
                int size = pending.Length;
                while (size < length + count)
                {
                    size = Math.Min(Math.Max(size * 2, 1024), maxLineBytes);
                }

                Array.Resize(ref pending, size);
            }

            Buffer.BlockCopy(data, offset, pending, length, count);
            length += count;
            return true;
        }

        private void Emit(Action<string> onLine)
        {
            int n = length;
            length = 0;
            if (n > 0 && pending[n - 1] == (byte)'\r')
            {
                n--;
            }

            string line = Utf8.GetString(pending, 0, n).Trim();
            if (line.Length > 0)
            {
                onLine(line);
            }
        }
    }
}
