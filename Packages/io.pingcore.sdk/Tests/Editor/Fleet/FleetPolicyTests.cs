using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>The pure pieces: line splitter, allocation tracker, reservation policy, health cadence, parsers and status classification.</summary>
    public sealed class FleetPolicyTests
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        private static List<string> Split(WatchLineSplitter splitter, params string[] chunks)
        {
            var lines = new List<string>();
            foreach (string chunk in chunks)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(chunk);
                Assert.That(splitter.Push(bytes, 0, bytes.Length, lines.Add), Is.True, "chunk accepted");
            }

            return lines;
        }

        [Test]
        public void TheSplitterJoinsALineSplitAcrossChunksAndKeepsOrder()
        {
            var splitter = new WatchLineSplitter();
            List<string> lines = Split(splitter, "{\"a\":", "1}\n{\"b\"", ":2}\n");
            Assert.That(lines, Is.EqualTo(new[] { "{\"a\":1}", "{\"b\":2}" }));
            Assert.That(splitter.PendingBytes, Is.EqualTo(0));
        }

        [Test]
        public void TheSplitterToleratesCrLfAndSkipsBlankLines()
        {
            var splitter = new WatchLineSplitter();
            List<string> lines = Split(splitter, "one\r\n\r\n  \n\ntwo\r", "\nthree\n");
            Assert.That(lines, Is.EqualTo(new[] { "one", "two", "three" }));
        }

        [Test]
        public void TheSplitterDecodesAMultiByteCharacterSplitAcrossChunks()
        {
            byte[] all = Encoding.UTF8.GetBytes("{\"n\":\"café\"}\n");
            int cut = Array.IndexOf(all, (byte)0xC3) + 1;
            var splitter = new WatchLineSplitter();
            var lines = new List<string>();
            Assert.That(splitter.Push(all, 0, cut, lines.Add), Is.True);
            Assert.That(splitter.Push(all, cut, all.Length - cut, lines.Add), Is.True);
            Assert.That(lines, Is.EqualTo(new[] { "{\"n\":\"café\"}" }));
        }

        [Test]
        public void TheSplitterRefusesALineOverTheBoundAndAcceptsOneAtIt()
        {
            var atBound = new WatchLineSplitter(8);
            Assert.That(Split(atBound, "12345678\n"), Is.EqualTo(new[] { "12345678" }));

            var over = new WatchLineSplitter(8);
            var lines = new List<string>();
            byte[] head = Encoding.UTF8.GetBytes("12345");
            byte[] tail = Encoding.UTF8.GetBytes("6789\nnext\n");
            Assert.That(over.Push(head, 0, head.Length, lines.Add), Is.True, "still under the bound");
            Assert.That(over.Push(tail, 0, tail.Length, lines.Add), Is.False, "the ninth byte of one line ends the stream");
            Assert.That(lines, Is.Empty, "nothing after the over-long line is emitted");
            Assert.That(WatchLineSplitter.DefaultMaxLineBytes, Is.EqualTo(4 * 1024 * 1024));
        }

        [Test]
        public void TheSplitterEmitsAFinalLineWithoutLfAtEndOfStream()
        {
            var splitter = new WatchLineSplitter();
            var lines = Split(splitter, "a\nb");
            Assert.That(lines, Is.EqualTo(new[] { "a" }));
            splitter.Finish(lines.Add);
            Assert.That(lines, Is.EqualTo(new[] { "a", "b" }));
        }

        [Test]
        public void TheTrackerRaisesEachAllocationOnceEvenWhenAReconnectReplaysIt()
        {
            var tracker = new AllocationTracker();
            AllocationChange first = tracker.Observe("match-1", "{\"mode\":\"duel\"}", Now);
            Assert.That(first.Received?.AllocationId, Is.EqualTo("match-1"));
            Assert.That((string)first.Received.Context["mode"], Is.EqualTo("duel"));
            Assert.That(tracker.Observe("match-1", "{\"mode\":\"duel\"}", Now).IsEmpty, Is.True, "the same id again is a replay");

            AllocationChange cleared = tracker.Observe(null, null, Now);
            Assert.That(cleared.Cleared?.Reason, Is.EqualTo(AllocationClearedReason.ClearedByPlatform));
            Assert.That(tracker.Current, Is.Null);

            AllocationChange back = tracker.Observe("match-1", "{}", Now);
            Assert.That(back.Received, Is.Null, "an id seen before is never raised again");
            Assert.That(tracker.Current?.AllocationId, Is.EqualTo("match-1"), "but it is current");
        }

        [Test]
        public void TheTrackerTellsEndedByTheGameFromClearedByThePlatform()
        {
            var tracker = new AllocationTracker();
            tracker.Observe("a1", "{}", Now);
            tracker.MarkEnding("a1");
            Assert.That(tracker.Observe(null, null, Now).Cleared.Reason, Is.EqualTo(AllocationClearedReason.EndedByGame));

            tracker.Observe("a2", "{}", Now);
            tracker.MarkEnding("a2");
            tracker.OnEndAnswered("a2", FleetCallOutcome.Rejected);
            Assert.That(tracker.Observe(null, null, Now).Cleared.Reason, Is.EqualTo(AllocationClearedReason.ClearedByPlatform), "a refused end call does not count");

            tracker.Observe("a3", "{}", Now);
            tracker.MarkEnding("a3");
            tracker.OnEndAnswered("a3", FleetCallOutcome.Unreachable);
            Assert.That(tracker.Observe(null, null, Now).Cleared.Reason, Is.EqualTo(AllocationClearedReason.EndedByGame), "a lost answer keeps the mark");
        }

        [Test]
        public void AReplacedAllocationIsClearedBeforeTheNewOneIsReceived()
        {
            var tracker = new AllocationTracker();
            tracker.Observe("a1", "{}", Now);
            AllocationChange swap = tracker.Observe("a2", "{}", Now);
            Assert.That(swap.Cleared?.AllocationId, Is.EqualTo("a1"));
            Assert.That(swap.Received?.AllocationId, Is.EqualTo("a2"));
        }

        [Test]
        public void ABadContextAnnotationGivesAnEmptyContextAndIsFlagged()
        {
            var tracker = new AllocationTracker();
            AllocationChange bad = tracker.Observe("a1", "{not json", Now);
            Assert.That(bad.ContextInvalid, Is.True);
            Assert.That(bad.Received.Context.Count, Is.EqualTo(0));
            // Mutation: build the AllocationInfo without the flag and this fails, so a match join reads the empty context as rosterless.
            Assert.That(bad.Received.ContextInvalid, Is.True, "the allocation carries the flag, not only the change");
            Assert.That(tracker.Current.ContextInvalid, Is.True);

            AllocationChange array = new AllocationTracker().Observe("a1", "[1,2]", Now);
            Assert.That(array.ContextInvalid, Is.True, "an array is not an object");

            AllocationChange missing = new AllocationTracker().Observe("a1", null, Now);
            Assert.That(missing.ContextInvalid, Is.False, "an absent annotation is not an error");

            AllocationChange good = new AllocationTracker().Observe("a1", "{\"when\":\"2026-10-02T00:00:00Z\"}", Now);
            Assert.That(good.ContextInvalid, Is.False);
            Assert.That(good.Received.ContextInvalid, Is.False);
            Assert.That(missing.Received.ContextInvalid, Is.False);
            Assert.That(good.Received.Context["when"].Type, Is.EqualTo(JTokenType.String), "no date coercion");
        }

        private static string Reservation(string id, long expiresAt) => new JObject
        {
            ["reservationId"] = id,
            ["serverId"] = "4211",
            ["seats"] = 2,
            ["playerIds"] = new JArray("p-alice"),
            ["context"] = null,
            ["expiresAt"] = expiresAt,
            ["ownerKind"] = "player",
            ["ownerPlayerId"] = "p-alice",
        }.ToString();

        [Test]
        public void TheReservationPolicyMapsEveryAnswer()
        {
            long future = Now.ToUnixTimeMilliseconds() + 60000;
            var cases = new (FleetCallOutcome Outcome, int Status, string Body, ReservationLookupStatus Expected, bool Retry)[]
            {
                (FleetCallOutcome.Ok, 200, Reservation("rsv_a1", future), ReservationLookupStatus.Found, false),
                (FleetCallOutcome.Ok, 200, Reservation("rsv_a1", Now.ToUnixTimeMilliseconds()), ReservationLookupStatus.Expired, false),
                (FleetCallOutcome.Ok, 200, Reservation("rsv_other", future), ReservationLookupStatus.Error, false),
                (FleetCallOutcome.Ok, 200, "{\"reservationId\":\"rsv_a1\"}", ReservationLookupStatus.Error, false),
                (FleetCallOutcome.Ok, 200, "not json", ReservationLookupStatus.Error, false),
                (FleetCallOutcome.Rejected, 404, "{\"message\":\"reservation not found\"}", ReservationLookupStatus.NotFound, true),
                (FleetCallOutcome.Rejected, 404, "{\"message\":\"counter x not found\"}", ReservationLookupStatus.Error, false),
                (FleetCallOutcome.Rejected, 404, "{}", ReservationLookupStatus.Error, false),
                (FleetCallOutcome.Unsupported, 404, "<html>Cannot GET</html>", ReservationLookupStatus.Unsupported, false),
                (FleetCallOutcome.Unsupported, 501, "{\"error\":\"not_implemented\"}", ReservationLookupStatus.Unsupported, false),
                (FleetCallOutcome.Rejected, 500, "{\"error\":\"internal_error\"}", ReservationLookupStatus.Error, false),
                (FleetCallOutcome.Unreachable, 0, null, ReservationLookupStatus.Unreachable, false),
                (FleetCallOutcome.EndpointClosed, 0, null, ReservationLookupStatus.Unreachable, false),
                (FleetCallOutcome.Cancelled, 0, null, ReservationLookupStatus.Cancelled, false),
            };

            foreach (var c in cases)
            {
                ReservationVerdict verdict = ReservationLookupPolicy.Classify(c.Outcome, c.Status, c.Body, "rsv_a1", Now);
                string label = c.Outcome + " " + c.Status + " " + (c.Body ?? "null");
                Assert.That(verdict.Status, Is.EqualTo(c.Expected), label);
                Assert.That(verdict.Retry, Is.EqualTo(c.Retry), label + " retry");
                Assert.That(verdict.Reservation != null, Is.EqualTo(c.Expected == ReservationLookupStatus.Found || c.Expected == ReservationLookupStatus.Expired), label + " record");
            }
        }

        [Test]
        public void AReservationExpiresAtItsExpiresAtNotAMillisecondLater()
        {
            long at = Now.ToUnixTimeMilliseconds();
            Assert.That(ReservationLookupPolicy.Classify(FleetCallOutcome.Ok, 200, Reservation("r", at + 1), "r", Now).Status, Is.EqualTo(ReservationLookupStatus.Found));
            Assert.That(ReservationLookupPolicy.Classify(FleetCallOutcome.Ok, 200, Reservation("r", at), "r", Now).Status, Is.EqualTo(ReservationLookupStatus.Expired));
        }

        [Test]
        public void HealthPingsAreLoggedFirstEveryThirtiethAndOnEveryFailure()
        {
            List<int> logged = Enumerable.Range(1, 95).Where(seq => HealthCadence.ShouldLog(seq, true)).ToList();
            Assert.That(logged, Is.EqualTo(new[] { 1, 30, 60, 90 }));
            Assert.That(HealthCadence.ShouldLog(2, false), Is.True);
            Assert.That(HealthCadence.ShouldLog(31, false), Is.True);
        }

        [Test]
        public void HealthPingsRunOnlyInLiveStates()
        {
            Assert.That(HealthCadence.ShouldPing(FleetState.Ready), Is.True);
            Assert.That(HealthCadence.ShouldPing(FleetState.InSession), Is.True);
            Assert.That(HealthCadence.ShouldPing(FleetState.Unreachable), Is.True, "a ping still tells the supervisor the game is alive");
            Assert.That(HealthCadence.ShouldPing(FleetState.ShuttingDown), Is.False);
            Assert.That(HealthCadence.ShouldPing(FleetState.Stopping), Is.False);
            Assert.That(HealthCadence.ShouldPing(FleetState.Inert), Is.False);
        }

        [Test]
        public void ThePortParserIsTheSharedOne()
        {
            // The table lives once, in LocalSdkPortTests; this proves the shim delegates to it.
            foreach (string raw in new[] { "9358", "0", "+80", "８０", null })
            {
                bool expected = global::PingCore.Core.LocalSdkPort.TryParse(raw, out int expectedPort);
                Assert.That(LocalSdkValues.TryParsePort(raw, out int port), Is.EqualTo(expected), raw ?? "null");
                Assert.That(port, Is.EqualTo(expectedPort));
            }

            Assert.That(LocalSdkValues.PortVariable, Is.EqualTo(global::PingCore.Core.LocalSdkPort.Variable));
        }

        [Test]
        public void TheInt64ParserReadsAgonesStringsAndRejectsEverythingElse()
        {
            Assert.That(LocalSdkValues.ParseInt64("3"), Is.EqualTo(3));
            Assert.That(LocalSdkValues.ParseInt64("0"), Is.EqualTo(0));
            Assert.That(LocalSdkValues.ParseInt64("-2"), Is.EqualTo(-2));
            Assert.That(LocalSdkValues.ParseInt64("9223372036854775807"), Is.EqualTo(long.MaxValue));
            foreach (string bad in new[] { null, string.Empty, "-", "x", "3.0", " 3", "1e3", "9223372036854775808", "+3" })
            {
                Assert.That(LocalSdkValues.ParseInt64(bad), Is.Null, bad ?? "null");
            }
        }

        [Test]
        public void StatusClassificationSeparatesRefusalsFromMissingRoutes()
        {
            Assert.That(LocalSdkCaller.Classify(200, "{}").Outcome, Is.EqualTo(FleetCallOutcome.Ok));
            Assert.That(LocalSdkCaller.Classify(204, null).Outcome, Is.EqualTo(FleetCallOutcome.Ok));
            (FleetCallOutcome outcome, string message) = LocalSdkCaller.Classify(400, "{\"message\":\"openSeats must be a non-negative integer\"}");
            Assert.That(outcome, Is.EqualTo(FleetCallOutcome.Rejected));
            Assert.That(message, Does.Contain("openSeats"));
            Assert.That(LocalSdkCaller.Classify(404, "{\"message\":\"counter x not found\"}").Outcome, Is.EqualTo(FleetCallOutcome.Rejected));
            Assert.That(LocalSdkCaller.Classify(404, "<html>Cannot GET</html>").Outcome, Is.EqualTo(FleetCallOutcome.Unsupported));
            Assert.That(LocalSdkCaller.Classify(501, "{\"error\":\"not_implemented\"}"), Is.EqualTo((FleetCallOutcome.Unsupported, "not_implemented")));
            Assert.That(LocalSdkCaller.Classify(500, "{\"error\":\"internal_error\",\"message\":\"internal error\"}"), Is.EqualTo((FleetCallOutcome.Rejected, "internal error")));
            Assert.That(LocalSdkCaller.Classify(503, null), Is.EqualTo((FleetCallOutcome.Rejected, "HTTP 503")));
        }
    }
}
