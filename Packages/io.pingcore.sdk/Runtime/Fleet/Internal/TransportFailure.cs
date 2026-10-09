using System;
using System.Net;
using System.Net.Sockets;

namespace PingCore.Fleet
{
    /// <summary>What kind of transport failure ended a call, as far as the exception chain says.</summary>
    internal enum TransportFailureKind
    {
        /// <summary>The connection was refused: nothing listens on the port any more.</summary>
        Refused,

        /// <summary>The peer reset or closed the connection before it answered.</summary>
        Reset,

        /// <summary>Anything else, including a timeout.</summary>
        Other,
    }

    /// <summary>
    /// Classifies a failed call, pure. On a container stop the supervisor closes the local SDK
    /// endpoint with Node's <c>server.close()</c>: new connections are refused and idle keep-alives
    /// close, but the watch stream (a response that never ends) stays open until the process exits,
    /// so the watch does not close before SIGTERM. A transport failure is therefore
    /// <see cref="FleetCallOutcome.EndpointClosed"/> when any of these holds:
    /// <list type="bullet">
    /// <item>the process is stopping (<c>NotifyProcessStopping</c>), whatever the failure;</item>
    /// <item>the watch stream closed less than five seconds ago, whatever the failure;</item>
    /// <item>the endpoint answered at least once before and the connection was refused.</item>
    /// </list>
    /// Otherwise it is <see cref="FleetCallOutcome.Unreachable"/>: a timeout, an endpoint that never answered, or a
    /// reset outside those windows. A reset alone does not mean the endpoint closed: Node closes an idle keep-alive
    /// socket after five seconds, so a healthy endpoint can reset a pooled connection (unary calls therefore send
    /// <c>Connection: close</c>, <see cref="LoopbackHttpTransport"/>), and a reset can lose the answer of a call the
    /// endpoint handled. Only a refusal says that nothing listens any more.
    /// </summary>
    internal static class TransportFailure
    {
        public static FleetCallOutcome Outcome(bool processStopping, bool inWatchClosedWindow, bool answeredBefore, TransportFailureKind kind)
        {
            if (processStopping || inWatchClosedWindow)
            {
                return FleetCallOutcome.EndpointClosed;
            }

            return answeredBefore && kind == TransportFailureKind.Refused ? FleetCallOutcome.EndpointClosed : FleetCallOutcome.Unreachable;
        }

        /// <summary>
        /// Walks the exception chain. A <see cref="SocketException"/> decides first (<c>ConnectionRefused</c>
        /// is Refused, <c>ConnectionReset</c> is Reset, <c>TimedOut</c> is Other); then a <see cref="WebException"/>
        /// (<c>ConnectFailure</c> is Refused; <c>ConnectionClosed</c>, and <c>KeepAliveFailure</c> for a pooled
        /// keep-alive connection the endpoint closed, are Reset). A timeout or cancellation anywhere
        /// in the chain, and anything unrecognised, is <see cref="TransportFailureKind.Other"/>.
        /// </summary>
        public static TransportFailureKind Classify(Exception exception)
        {
            TransportFailureKind? fromWeb = null;
            for (Exception e = exception; e != null; e = Next(e))
            {
                if (e is TimeoutException || e is OperationCanceledException)
                {
                    return TransportFailureKind.Other;
                }

                if (e is SocketException socket)
                {
                    switch (socket.SocketErrorCode)
                    {
                        case SocketError.ConnectionRefused:
                            return TransportFailureKind.Refused;
                        case SocketError.ConnectionReset:
                            return TransportFailureKind.Reset;
                        case SocketError.TimedOut:
                            return TransportFailureKind.Other;
                    }
                }

                if (fromWeb == null && e is WebException web)
                {
                    switch (web.Status)
                    {
                        case WebExceptionStatus.ConnectFailure:
                            fromWeb = TransportFailureKind.Refused;
                            break;
                        case WebExceptionStatus.ConnectionClosed:
                        case WebExceptionStatus.KeepAliveFailure:
                            fromWeb = TransportFailureKind.Reset;
                            break;
                        case WebExceptionStatus.Timeout:
                            return TransportFailureKind.Other;
                    }
                }
            }

            return fromWeb ?? TransportFailureKind.Other;
        }

        private static Exception Next(Exception e)
        {
            if (e is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            {
                return aggregate.InnerExceptions[0];
            }

            return e.InnerException;
        }
    }
}
