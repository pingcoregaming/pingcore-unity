using System;
using System.Threading;
using System.Threading.Tasks;

namespace PingCore.Fleet
{
    /// <summary>How a line stream ended without a transport failure.</summary>
    public enum LineStreamEnd
    {
        /// <summary>The server closed the response.</summary>
        EndOfStream = 0,

        /// <summary>A line exceeded the bound (4 MiB); the stream was abandoned so the caller reconnects.</summary>
        LineTooLong = 1,

        /// <summary>The server answered a non-2xx status; no line was read.</summary>
        NotSuccess = 2,
    }

    /// <summary>The end of one streamed GET.</summary>
    public sealed class LineStreamResult
    {
        /// <param name="status">The HTTP status of the response.</param>
        /// <param name="end">How the stream ended.</param>
        public LineStreamResult(int status, LineStreamEnd end)
        {
            Status = status;
            End = end;
        }

        /// <summary>The HTTP status of the response.</summary>
        public int Status { get; }

        /// <summary>How the stream ended.</summary>
        public LineStreamEnd End { get; }
    }

    /// <summary>
    /// The seam for the watch stream: one long-lived GET whose body is newline-delimited text.
    /// The default implementation reads the chunked response as it arrives
    /// (<c>HttpCompletionOption.ResponseHeadersRead</c>) with no timeout.
    /// </summary>
    public interface ILineStreamTransport
    {
        /// <summary>
        /// Opens <paramref name="url"/> and hands every non-blank line (UTF-8, CR and LF
        /// stripped) to <paramref name="onLine"/> on the caller's context, in order, until the
        /// stream ends. Returns how it ended. Throws only for a transport failure or cancellation.
        /// </summary>
        Task<LineStreamResult> ReadLinesAsync(string url, Action<string> onLine, CancellationToken cancellationToken);
    }
}
