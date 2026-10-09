using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// Scans raw bytes for the token shape encoded as UTF-8 (or ASCII) and as UTF-16LE, the encoding
    /// of string literals in a .NET assembly. Equivalent to <see cref="BuildGuardPolicy.TokenPattern"/>
    /// over the decoded text: only ASCII letters and digits count as token characters. Pure: it reads
    /// only the stream it is handed, in chunks, so a file of any size is scanned in bounded memory.
    /// </summary>
    public static class BuildGuardByteScanner
    {
        /// <summary>How much of a file is read at a time.</summary>
        public const int DefaultChunkSize = 8 * 1024 * 1024;

        /// <summary>
        /// Bytes kept from the end of one window for the next: twice (the longest prefix, the minimum
        /// tail and the preceding character), so a token straddling a chunk boundary is matched in
        /// UTF-16LE as well as UTF-8. A token longer than this is held until it ends.
        /// </summary>
        public static readonly int Overlap = 2 * (LongestPrefixLength() + BuildGuardPolicy.MinimumTokenTail + 1);

        /// <summary>Scans a whole array in one window.</summary>
        public static BuildGuardScanResult Scan(byte[] data, string location, IReadOnlyCollection<string> allowedDscTokens = null)
        {
            if (data == null || data.Length == 0)
            {
                return BuildGuardScanResult.Empty;
            }

            var findings = new List<BuildGuardFinding>();
            int allowed = 0;
            var window = new Window(data, 0, data.Length, true);
            ScanEncoded(window, 1, 0, data.Length, location, allowedDscTokens, findings, ref allowed);
            ScanEncoded(window, 2, 0, data.Length, location, allowedDscTokens, findings, ref allowed);
            return new BuildGuardScanResult(findings, allowed);
        }

        /// <summary>
        /// Scans a stream in chunks of <paramref name="chunkSize"/> bytes. Positions in findings are
        /// offsets from the start of the stream, as <see cref="Scan"/> reports them.
        /// </summary>
        public static BuildGuardScanResult ScanStream(Stream stream, string location, IReadOnlyCollection<string> allowedDscTokens = null,
            int chunkSize = DefaultChunkSize)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (chunkSize < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSize), chunkSize, "The chunk size must be at least one byte.");
            }

            var findings = new List<BuildGuardFinding>();
            int allowed = 0;
            byte[] buffer = new byte[chunkSize + Overlap];
            int length = 0;
            long start = 0;
            long resumeUtf8 = 0;
            long resumeUtf16 = 0;
            while (true)
            {
                if (buffer.Length - length < chunkSize)
                {
                    Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + chunkSize));
                }

                int read = ReadFully(stream, buffer, length, chunkSize);
                length += read;
                bool atEnd = read < chunkSize;
                long end = start + length;
                long limit = atEnd ? end : end - Overlap;
                var window = new Window(buffer, start, length, atEnd);
                resumeUtf8 = ScanEncoded(window, 1, resumeUtf8, limit, location, allowedDscTokens, findings, ref allowed);
                resumeUtf16 = ScanEncoded(window, 2, resumeUtf16, limit, location, allowedDscTokens, findings, ref allowed);
                if (atEnd)
                {
                    break;
                }

                // Keep everything from the earliest position still to scan, plus one UTF-16 code unit
                // before it for the preceding-character rule.
                long keep = Math.Max(start, Math.Min(resumeUtf8, resumeUtf16) - 2);
                int drop = (int)(keep - start);
                if (drop > 0)
                {
                    Buffer.BlockCopy(buffer, drop, buffer, 0, length - drop);
                    length -= drop;
                    start = keep;
                }
            }

            return new BuildGuardScanResult(findings, allowed);
        }

        private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, offset + total, count - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        /// <summary>
        /// Scans candidate starts in [<paramref name="from"/>, <paramref name="limit"/>) and returns the
        /// position to resume from. A candidate whose token runs into the end of a window that is not
        /// the end of the stream is deferred: the returned position is that candidate.
        /// </summary>
        private static long ScanEncoded(Window window, int width, long from, long limit, string location,
            IReadOnlyCollection<string> allowedDscTokens, List<BuildGuardFinding> findings, ref int allowed)
        {
            long last = window.End - width;
            long i = Math.Max(from, window.Start);
            for (; i < limit && i <= last; i++)
            {
                int c = window.CharAt(i, width);
                if (c != 'u' && c != 's' && c != 'c' && c != 'd')
                {
                    continue;
                }

                if (IsTokenChar(window.CharAt(i - width, width)))
                {
                    continue;
                }

                string prefix = MatchPrefix(window, i, width);
                if (prefix == null)
                {
                    continue;
                }

                long tailStart = i + prefix.Length * width;
                long end = tailStart;
                while (end <= last && IsTokenChar(window.CharAt(end, width)))
                {
                    end += width;
                }

                if (!window.AtEnd && end > last)
                {
                    return i;
                }

                long tail = (end - tailStart) / width;
                if (tail < BuildGuardPolicy.MinimumTokenTail)
                {
                    continue;
                }

                var token = new StringBuilder(prefix.Length + (int)tail);
                for (long p = i; p < end; p += width)
                {
                    token.Append((char)window.CharAt(p, width));
                }

                string value = token.ToString();
                if (BuildGuardPolicy.IsAllowedDscToken(value, allowedDscTokens))
                {
                    allowed++;
                }
                else
                {
                    findings.Add(BuildGuardPolicy.SecretFinding(location, value, "byte " + i + " (" + (width == 1 ? "UTF-8" : "UTF-16LE") + ")"));
                }

                i = end - 1;
            }

            return i;
        }

        private static string MatchPrefix(Window window, long start, int width)
        {
            foreach (string prefix in BuildGuardPolicy.TokenPrefixes)
            {
                bool matched = true;
                for (int k = 0; k < prefix.Length; k++)
                {
                    if (window.CharAt(start + (long)k * width, width) != prefix[k])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    return prefix;
                }
            }

            return null;
        }

        private static bool IsTokenChar(int c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

        private static int LongestPrefixLength()
        {
            int longest = 0;
            foreach (string prefix in BuildGuardPolicy.TokenPrefixes)
            {
                longest = Math.Max(longest, prefix.Length);
            }

            return longest;
        }

        /// <summary>Bytes [<see cref="Start"/>, <see cref="End"/>) of the stream, held at the front of a buffer.</summary>
        private readonly struct Window
        {
            private readonly byte[] data;
            private readonly int length;

            public Window(byte[] data, long start, int length, bool atEnd)
            {
                this.data = data;
                this.length = length;
                Start = start;
                AtEnd = atEnd;
            }

            public long Start { get; }
            public long End => Start + length;
            public bool AtEnd { get; }

            /// <summary>The ASCII character at a stream position for the given code unit width, or -1.</summary>
            public int CharAt(long position, int width)
            {
                long relative = position - Start;
                if (relative < 0 || relative + width > length)
                {
                    return -1;
                }

                byte low = data[relative];
                if (low >= 0x80)
                {
                    return -1;
                }

                if (width == 2 && data[relative + 1] != 0)
                {
                    return -1;
                }

                return low;
            }
        }
    }
}
