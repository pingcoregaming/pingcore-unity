using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Core.Handshake;
using Step = PingCore.Discovery.Client.Tests.Editor.FakeDiscoveryTransport.Step;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>Tickets: the local floors, the poll loop (cadence, Retry-After, 404, five failures, expiry), cancel and the join ticket.</summary>
    public sealed class TicketTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        private static TicketOptions Valid() => new TicketOptions { Queue = "rush-p2", SessionSize = 4, MinSessionSize = 2, RelaxAfterSeconds = 10 };

        public static IEnumerable<TestCaseData> FloorViolations()
        {
            yield return Case("party above session", o => { o.SessionSize = 2; o.MinSessionSize = null; o.RelaxAfterSeconds = null; o.PartySize = 3; }, DiscoveryReason.PartyTooLarge);
            yield return Case("min without relax", o => o.RelaxAfterSeconds = null, DiscoveryReason.RelaxationIncomplete);
            yield return Case("relax without min", o => o.MinSessionSize = null, DiscoveryReason.RelaxationIncomplete);
            yield return Case("min above session", o => o.MinSessionSize = 5, DiscoveryReason.MinSessionTooLarge);
            yield return Case("min below party", o => { o.PartySize = 3; o.MinSessionSize = 2; }, DiscoveryReason.MinSessionBelowParty);
            yield return Case("relax at the TTL", o => o.RelaxAfterSeconds = 300, DiscoveryReason.RelaxAfterTooLong);
            yield return Case("33 latency entries", o => o.Latency = Enumerable.Range(0, 33).ToDictionary(i => "l" + i, i => 20), DiscoveryReason.TooManyLatencyEntries);
            yield return Case("ceiling without a map", o => o.MaxLatencyMs = 80, DiscoveryReason.MaxLatencyWithoutMap);
            yield return Case("nothing within the ceiling", o => { o.Latency = new Dictionary<string, int> { ["eu-west-ams"] = 120 }; o.MaxLatencyMs = 80; }, DiscoveryReason.NoLocationWithinCeiling);
            yield return Case("context of 1025 bytes", o => o.Context = new JObject { ["pad"] = new string('x', 1015) }, DiscoveryReason.ContextTooLarge);
            yield return Case("party of 9", o => { o.SessionSize = 16; o.MinSessionSize = null; o.RelaxAfterSeconds = null; o.PartySize = 9; }, DiscoveryReason.PartyTooLargeForPlayer);
            yield return Case("solo session", o => { o.SessionSize = 1; o.MinSessionSize = null; o.RelaxAfterSeconds = null; }, DiscoveryReason.SessionTooSmallForPlayer);
            yield return Case("min session of 1", o => o.MinSessionSize = 1, DiscoveryReason.SessionTooSmallForPlayer);
            yield return Case("party fills the session", o => { o.PartySize = 4; o.MinSessionSize = 4; }, DiscoveryReason.PartyFillsSessionForPlayer);
            yield return Case("party fills the min session", o => { o.PartySize = 2; o.MinSessionSize = 2; }, DiscoveryReason.PartyFillsSessionForPlayer);
        }

        private static TestCaseData Case(string name, Action<TicketOptions> mutate, DiscoveryReason reason)
        {
            TicketOptions o = Valid();
            mutate(o);
            return new TestCaseData(o, reason).SetName("A ticket floor violation is refused locally: " + name);
        }

        [TestCaseSource(nameof(FloorViolations))]
        public async Task AFloorViolationIsRefusedLocallyWithTheServicesReasonAndSendsNothing(TicketOptions options, DiscoveryReason reason)
        {
            using (var h = new ClientHarness())
            {
                DiscoveryResult<TicketHandle> r = await h.Client.SubmitTicketAsync(options, None);
                Assert.That(r.Outcome, Is.EqualTo(DiscoveryOutcome.InvalidRequest));
                Assert.That(r.Status, Is.EqualTo(0));
                Assert.That(r.Reason, Is.EqualTo(reason));
                Assert.That(h.Http.Requests, Is.Empty);
            }
        }

        [Test]
        public async Task TheValidBaselineAndABoundaryContextAreSentAndSkipLocalFloorsLetsTheServiceAnswer()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk(), Step.Error(400, "Player tokens need a session of at least 2 players.", "session_too_small_for_player"));
                TicketOptions boundary = Valid();
                boundary.Context = new JObject { ["pad"] = new string('x', 1014) };
                DiscoveryResult<TicketHandle> ok = await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(boundary, None));
                Assert.That(ok.IsOk, Is.True, "1024 bytes is at the limit, not over it: " + ok);

                var solo = new TicketOptions { SessionSize = 1, SkipLocalFloors = true };
                DiscoveryResult<TicketHandle> service = await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(solo, None));
                Assert.That(service.Status, Is.EqualTo(400), "the service answered");
                Assert.That(service.Reason, Is.EqualTo(DiscoveryReason.SessionTooSmallForPlayer));
                JObject body = h.Http.To("POST", ClientHarness.TicketsPath)[0].Json;
                Assert.That(body.Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "ticketId", "queue", "sessionSize", "partySize", "context", "minSessionSize", "relaxAfterSeconds" }));
                Assert.That((string)body["ticketId"], Does.Match("^[A-Za-z0-9_-]{22}$"), "the SDK mints the ticket id");
            }
        }

        [Test]
        public async Task AQueuedTicketIsPolledEveryTwoSecondsUntilMatched()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk());
                h.Http.On("GET", ClientHarness.TicketPath, h.PollOk(false), h.PollOk(false), h.PollOk(true));
                TicketHandle ticket = (await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(Valid(), None))).Value;
                var changes = new List<TicketState>();
                ticket.Changed += t => changes.Add(t.State);
                DateTimeOffset submitted = h.Scheduler.UtcNow;

                await h.Scheduler.RunAsync(ticket.WaitAsync(None));

                Assert.That(ticket.State, Is.EqualTo(TicketState.Matched));
                var polls = h.Http.To("GET", ClientHarness.TicketPath);
                // Mutation: change PollInterval and these offsets move.
                Assert.That(polls.Select(p => p.At - submitted), Is.EqualTo(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6) }));
                Assert.That(polls.Select(p => p.Path).Distinct().Single(), Does.EndWith("/tickets/" + (string)h.Http.To("POST", ClientHarness.TicketsPath)[0].Json["ticketId"]));
                Assert.That(ticket.Match.AllocationId, Is.EqualTo("alloc-1"));
                Assert.That(ticket.Match.Ip, Is.EqualTo("203.0.113.10"));
                Assert.That(ticket.Match.Port, Is.EqualTo(27015));
                Assert.That(ticket.Match.Backfill, Is.False);
                Assert.That(ticket.Match.Location, Is.EqualTo("eu-west-ams"));
                Assert.That(changes, Is.EqualTo(new[] { TicketState.Matched }));
                Assert.That(ticket.TicketRef, Is.EqualTo(SecureIds.Ref((string)h.Http.To("POST", ClientHarness.TicketsPath)[0].Json["ticketId"])));
                Assert.That(string.Join("\n", h.Logs.Select(l => l.ToString())), Does.Not.Contain((string)h.Http.To("POST", ClientHarness.TicketsPath)[0].Json["ticketId"]), "logs carry the ref, never the id");
            }
        }

        [Test]
        public async Task A429WaitsTheLongerOfRetryAfterAndTwoSeconds()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk());
                h.Http.On("GET", ClientHarness.TicketPath, Step.Error(429, "slow", null, ("Retry-After", "7")), Step.Error(429, "slow", null, ("Retry-After", "1")), h.PollOk(true));
                TicketHandle ticket = (await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(Valid(), None))).Value;
                DateTimeOffset submitted = h.Scheduler.UtcNow;
                await h.Scheduler.RunAsync(ticket.WaitAsync(None));
                var polls = h.Http.To("GET", ClientHarness.TicketPath);
                Assert.That(polls.Select(p => p.At - submitted), Is.EqualTo(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(11) }));
                Assert.That(ticket.State, Is.EqualTo(TicketState.Matched));
            }
        }

        [Test]
        public async Task A404EndsTheTicketExpired()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk());
                h.Http.On("GET", ClientHarness.TicketPath, h.PollOk(false), Step.Error(404, "Unknown ticket (it may have expired)."));
                TicketHandle ticket = (await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(Valid(), None))).Value;
                await h.Scheduler.RunAsync(ticket.WaitAsync(None));
                Assert.That(ticket.State, Is.EqualTo(TicketState.Expired));
                Assert.That(ticket.LastError.Status, Is.EqualTo(404));
                Assert.That(h.Http.Count("GET", ClientHarness.TicketPath), Is.EqualTo(2));
            }
        }

        [Test]
        public async Task FiveFailuresInARowEndTheTicketFailedWithTheGovernorsBackoff()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk());
                h.Http.On("GET", ClientHarness.TicketPath, Step.Error(503, "degraded"), Step.Unreachable(), Step.Json(500, "oops"), Step.Error(503, "degraded"), Step.Error(503, "degraded"), h.PollOk(true));
                TicketHandle ticket = (await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(Valid(), None))).Value;
                DateTimeOffset submitted = h.Scheduler.UtcNow;
                await h.Scheduler.RunAsync(ticket.WaitAsync(None));

                Assert.That(ticket.State, Is.EqualTo(TicketState.Failed));
                var polls = h.Http.To("GET", ClientHarness.TicketPath);
                // Mutation: count only 503s, or reset on a transport failure, and the sixth (matched) poll is reached.
                Assert.That(polls.Count, Is.EqualTo(5));
                Assert.That(polls.Select(p => p.At - submitted), Is.EqualTo(new[] { 2, 4, 8, 16, 26 }.Select(s => TimeSpan.FromSeconds(s))), "2 s, then backoff 2, 4, 8, 10");
                Assert.That(ticket.LastError.Outcome, Is.EqualTo(DiscoveryOutcome.Degraded));
            }
        }

        [Test]
        public async Task ASuccessfulPollResetsTheFailureCount()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk());
                Step down = Step.Error(503, "degraded");
                h.Http.On("GET", ClientHarness.TicketPath, down, down, down, down, h.PollOk(false), down, down, down, down, h.PollOk(true));
                TicketHandle ticket = (await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(Valid(), None))).Value;
                await h.Scheduler.RunAsync(ticket.WaitAsync(None));
                Assert.That(ticket.State, Is.EqualTo(TicketState.Matched), "eight failures, never five in a row");
                Assert.That(h.Http.Count("GET", ClientHarness.TicketPath), Is.EqualTo(10));
            }
        }

        [Test]
        public async Task PollingStopsTenSecondsAfterExpiresAt()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk(ttlSeconds: 5));
                h.Http.On("GET", ClientHarness.TicketPath, h.PollOk(false, expiresAt: h.Scheduler.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds()));
                TicketHandle ticket = (await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(Valid(), None))).Value;
                DateTimeOffset submitted = h.Scheduler.UtcNow;
                await h.Scheduler.RunAsync(ticket.WaitAsync(None));

                Assert.That(ticket.State, Is.EqualTo(TicketState.Expired));
                var polls = h.Http.To("GET", ClientHarness.TicketPath);
                // expiresAt + 10 s is submit + 15 s: polls at 2..14, none at 16.
                Assert.That(polls.Select(p => (int)(p.At - submitted).TotalSeconds), Is.EqualTo(new[] { 2, 4, 6, 8, 10, 12, 14 }));
            }
        }

        [Test]
        public async Task AReplayedSubmitThatAnswersMatchedWithoutPlacementIsPolledOnceAtOnce()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk(status: "matched"));
                h.Http.On("GET", ClientHarness.TicketPath, h.PollOk(true, backfill: true));
                TicketHandle ticket = (await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(Valid(), None))).Value;
                Assert.That(ticket.State, Is.EqualTo(TicketState.Queued), "matched without placement is not yet usable");
                DateTimeOffset submitted = h.Scheduler.UtcNow;
                await h.Scheduler.RunAsync(ticket.WaitAsync(None));
                Assert.That(ticket.State, Is.EqualTo(TicketState.Matched));
                Assert.That(h.Http.To("GET", ClientHarness.TicketPath).Single().At, Is.EqualTo(submitted), "no 2 s wait");
                Assert.That(ticket.Match.Backfill, Is.True);
            }
        }

        [Test]
        public async Task Cancel200EndsCancelledAndStopsPolling()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk());
                h.Http.On("GET", ClientHarness.TicketPath, h.PollOk(false));
                h.Http.On("DELETE", ClientHarness.TicketPath, Step.Json(200, "{\"error\":false,\"ticketId\":\"echoed\",\"cancelled\":true}"));
                TicketHandle ticket = (await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(Valid(), None))).Value;
                Task<TicketHandle> waiting = ticket.WaitAsync(None);
                await TestScheduler.Until(() => h.Scheduler.PendingCount == 1, "the first poll delay");
                DiscoveryResult<Wire.CancelTicketResponse> cancel = await ticket.CancelAsync(None);
                Assert.That(cancel.IsOk, Is.True);
                await h.Scheduler.RunAsync(waiting);
                Assert.That(ticket.State, Is.EqualTo(TicketState.Cancelled));
                Assert.That(h.Http.Count("GET", ClientHarness.TicketPath), Is.EqualTo(0), "the pending poll never ran");
                Assert.That((await ticket.CancelAsync(None)).IsLocalRefusal, Is.True, "a terminal ticket sends nothing");
                Assert.That(h.Http.Count("DELETE", ClientHarness.TicketPath), Is.EqualTo(1));
            }
        }

        [Test]
        public async Task Cancel409MeansAlreadyMatchedSoOnePollReadsTheMatch()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk());
                h.Http.On("DELETE", ClientHarness.TicketPath, Step.Error(409, "This ticket already matched; release the allocation instead."));
                h.Http.On("GET", ClientHarness.TicketPath, h.PollOk(true));
                TicketHandle ticket = (await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(Valid(), None))).Value;
                DiscoveryResult<Wire.CancelTicketResponse> cancel = await h.Scheduler.RunAsync(ticket.CancelAsync(None));
                Assert.That(cancel.Outcome, Is.EqualTo(DiscoveryOutcome.Conflict));
                Assert.That(ticket.State, Is.EqualTo(TicketState.Matched));
                Assert.That(h.Http.Count("GET", ClientHarness.TicketPath), Is.EqualTo(1));
                Assert.That(ticket.Match.AllocationId, Is.EqualTo("alloc-1"));
            }
        }

        [Test]
        public async Task Cancel404MeansExpired()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk());
                h.Http.On("DELETE", ClientHarness.TicketPath, Step.Error(404, "Unknown ticket (it may have expired)."));
                TicketHandle ticket = (await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(Valid(), None))).Value;
                DiscoveryResult<Wire.CancelTicketResponse> cancel = await h.Scheduler.RunAsync(ticket.CancelAsync(None));
                Assert.That(cancel.Outcome, Is.EqualTo(DiscoveryOutcome.NotFound));
                Assert.That(ticket.State, Is.EqualTo(TicketState.Expired));
            }
        }

        [Test]
        public async Task DisposingAQueuedTicketCancelsItBestEffort()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk());
                h.Http.On("DELETE", ClientHarness.TicketPath, Step.Json(200, "{\"error\":false,\"ticketId\":\"echoed\",\"cancelled\":true}"));
                TicketHandle ticket = (await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(Valid(), None))).Value;
                ticket.Dispose();
                await TestScheduler.Until(() => h.Http.Count("DELETE", ClientHarness.TicketPath) == 1, "the best-effort cancel");
            }
        }

        [Test]
        public async Task TheJoinTicketCarriesTheTicketAllocationAndPlayerAndItsKindFollowsBackfill()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk());
                h.Http.On("GET", ClientHarness.TicketPath, h.PollOk(true, backfill: true));
                TicketHandle ticket = (await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(Valid(), None))).Value;
                Assert.Throws<InvalidOperationException>(() => ticket.CreateJoinTicket(2, "Ada"), "no match yet");
                await h.Scheduler.RunAsync(ticket.WaitAsync(None));

                JoinTicket join = ticket.CreateJoinTicket(2, "Ada");
                Assert.That(join.Kind, Is.EqualTo(JoinTicketKind.Backfill));
                Assert.That(join.TicketId, Is.EqualTo((string)h.Http.To("POST", ClientHarness.TicketsPath)[0].Json["ticketId"]));
                Assert.That(join.AllocationId, Is.EqualTo("alloc-1"));
                Assert.That(join.PlayerId, Is.EqualTo(ClientHarness.AnonPlayer(1)));
                Assert.That(join.ProtocolVersion, Is.EqualTo(2));
                Assert.That(join.DisplayName, Is.EqualTo("Ada"));
            }
        }
    }
}
