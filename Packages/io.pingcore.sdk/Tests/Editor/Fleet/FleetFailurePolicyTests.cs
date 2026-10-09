using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using NUnit.Framework;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>The pure failure policies: how a transport failure is classified, and when the ending mark of an allocation is dropped.</summary>
    public sealed class FleetFailurePolicyTests
    {
        private static HttpRequestException Wrapped(Exception inner) => new HttpRequestException("An error occurred while sending the request", inner);

        [Test]
        public void TheExceptionChainNamesRefusedResetOrOther()
        {
            var cases = new (string Label, Exception Error, TransportFailureKind Expected)[]
            {
                ("socket refused (.NET SocketsHttpHandler)", Wrapped(new SocketException((int)SocketError.ConnectionRefused)), TransportFailureKind.Refused),
                ("socket refused under a web exception (Mono)", Wrapped(new WebException("Error: ConnectFailure (Connection refused)", new SocketException((int)SocketError.ConnectionRefused), WebExceptionStatus.ConnectFailure, null)), TransportFailureKind.Refused),
                ("connect failure without a socket error", Wrapped(new WebException("Error: ConnectFailure", WebExceptionStatus.ConnectFailure)), TransportFailureKind.Refused),
                ("socket reset under an IO exception", Wrapped(new IOException("Unable to read data from the transport connection", new SocketException((int)SocketError.ConnectionReset))), TransportFailureKind.Reset),
                ("connection closed before the answer", Wrapped(new WebException("The connection was closed", WebExceptionStatus.ConnectionClosed)), TransportFailureKind.Reset),
                ("a pooled keep-alive connection the endpoint closed", Wrapped(new WebException("The underlying connection was closed", WebExceptionStatus.KeepAliveFailure)), TransportFailureKind.Reset),
                ("a socket reset beats the web status", Wrapped(new WebException("Error: ReceiveFailure", new SocketException((int)SocketError.ConnectionReset), WebExceptionStatus.ReceiveFailure, null)), TransportFailureKind.Reset),
                ("a timeout is a cancellation the caller did not ask for", new TaskCanceledException("timeout"), TransportFailureKind.Other),
                ("a timeout wrapping a reset is still a timeout", new TaskCanceledException("timeout", new IOException("x", new SocketException((int)SocketError.ConnectionReset))), TransportFailureKind.Other),
                ("a web timeout", Wrapped(new WebException("timed out", WebExceptionStatus.Timeout)), TransportFailureKind.Other),
                ("a socket timeout", Wrapped(new SocketException((int)SocketError.TimedOut)), TransportFailureKind.Other),
                ("a bare request exception", new HttpRequestException("connection refused"), TransportFailureKind.Other),
                ("an aborted connection is not a reset", Wrapped(new SocketException((int)SocketError.ConnectionAborted)), TransportFailureKind.Other),
                ("one inner exception of an aggregate", new AggregateException(new SocketException((int)SocketError.ConnectionRefused)), TransportFailureKind.Refused),
            };

            foreach (var c in cases)
            {
                Assert.That(TransportFailure.Classify(c.Error), Is.EqualTo(c.Expected), c.Label);
            }
        }

        [Test]
        public void ARefusedCallAfterAnAnswerIsEndpointClosedAndAResetATimeoutOrANeverAnsweredEndpointIsUnreachable()
        {
            const FleetCallOutcome Closed = FleetCallOutcome.EndpointClosed;
            const FleetCallOutcome Unreachable = FleetCallOutcome.Unreachable;
            var cases = new (bool Stopping, bool WatchWindow, bool Answered, TransportFailureKind Kind, FleetCallOutcome Expected)[]
            {
                // The production stop: the watch stays open, the endpoint answered before, new connections are refused.
                (false, false, true, TransportFailureKind.Refused, Closed),

                // A reset alone is not a stop: Node resets an idle keep-alive after 5 s, and a reset can lose an answer.
                (false, false, true, TransportFailureKind.Reset, Unreachable),
                (false, false, true, TransportFailureKind.Other, Unreachable),
                (false, false, false, TransportFailureKind.Refused, Unreachable),
                (false, false, false, TransportFailureKind.Reset, Unreachable),
                (false, false, false, TransportFailureKind.Other, Unreachable),

                // The watch-closed window covers every kind of failure.
                (false, true, false, TransportFailureKind.Other, Closed),
                (false, true, true, TransportFailureKind.Other, Closed),
                (false, true, false, TransportFailureKind.Refused, Closed),
                (false, true, true, TransportFailureKind.Reset, Closed),

                // NotifyProcessStopping covers everything.
                (true, false, false, TransportFailureKind.Other, Closed),
                (true, false, false, TransportFailureKind.Refused, Closed),
                (true, false, true, TransportFailureKind.Other, Closed),
                (true, false, true, TransportFailureKind.Reset, Closed),
            };

            foreach (var c in cases)
            {
                string label = "stopping " + c.Stopping + ", watch window " + c.WatchWindow + ", answered " + c.Answered + ", " + c.Kind;
                Assert.That(TransportFailure.Outcome(c.Stopping, c.WatchWindow, c.Answered, c.Kind), Is.EqualTo(c.Expected), label);
            }
        }

        [Test]
        public void OnlyARefusalAnswerDropsTheEndingMark()
        {
            var cases = new (FleetCallOutcome Outcome, bool Clears)[]
            {
                (FleetCallOutcome.Ok, false),
                (FleetCallOutcome.Rejected, true),
                (FleetCallOutcome.Unsupported, true),
                (FleetCallOutcome.Unreachable, false),
                (FleetCallOutcome.EndpointClosed, false),
                (FleetCallOutcome.Cancelled, false),
                (FleetCallOutcome.Inert, false),
            };

            DateTimeOffset now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
            foreach (var c in cases)
            {
                Assert.That(AllocationTracker.ClearsEndingMark(c.Outcome), Is.EqualTo(c.Clears), c.Outcome.ToString());

                // The same rule through the tracker: the clear that follows is the platform's only when the mark went.
                var tracker = new AllocationTracker();
                tracker.Observe("a1", "{}", now);
                tracker.MarkEnding("a1");
                tracker.OnEndAnswered("a1", c.Outcome);
                AllocationClearedReason expected = c.Clears ? AllocationClearedReason.ClearedByPlatform : AllocationClearedReason.EndedByGame;
                Assert.That(tracker.Observe(null, null, now).Cleared.Reason, Is.EqualTo(expected), c.Outcome + " then a clearing frame");
            }
        }
    }
}
