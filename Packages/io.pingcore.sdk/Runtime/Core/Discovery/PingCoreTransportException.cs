using System;

namespace PingCore.Core.Discovery
{
    /// <summary>
    /// Thrown by an <see cref="IHttpTransport"/> when no HTTP answer arrived (connection failure,
    /// DNS, TLS, timeout, reset). Its message must never contain a URL, a header or a body,
    /// because a request URL can carry a ticket id and a header a bearer token.
    /// <see cref="DiscoveryCaller"/> turns it into <see cref="DiscoveryOutcome.Unreachable"/>.
    /// </summary>
    public sealed class PingCoreTransportException : Exception
    {
        /// <summary>Creates the exception with a message that names the failure only.</summary>
        public PingCoreTransportException(string message)
            : base(message)
        {
        }

        /// <summary>Creates the exception with a message that names the failure only, and its cause.</summary>
        public PingCoreTransportException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
