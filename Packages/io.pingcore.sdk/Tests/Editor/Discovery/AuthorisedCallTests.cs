using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Discovery.Client.Wire;
using Step = PingCore.Discovery.Client.Tests.Editor.FakeDiscoveryTransport.Step;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>The one authorised-call wrapper (401 re-issue once) and the idempotency anchors reused on the SDK's own retry.</summary>
    public sealed class AuthorisedCallTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        [Test]
        public async Task A401WithAPlayerTokenReasonDropsTheTokenReissuesOnceAndResendsTheSameReservation()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk(), h.IssueOk());
                h.Http.On("POST", ClientHarness.ReservePath, Step.Error(401, "The player token has expired.", "token_expired"), h.ReservationOk("r-1"));

                DiscoveryResult<ReservationResponse> r = await h.Scheduler.RunAsync(h.Client.ReserveAsync("agent-a-1", new ReserveOptions { ReservationId = "r-1" }, None));

                Assert.That(r.IsOk, Is.True, r.ToString());
                var sent = h.Http.To("POST", ClientHarness.ReservePath);
                Assert.That(sent.Count, Is.EqualTo(2));
                Assert.That(sent.Select(s => (string)s.Json["reservationId"]), Is.EqualTo(new[] { "r-1", "r-1" }), "the same reservation id");
                Assert.That(sent[0].Authorization, Is.EqualTo("Bearer " + ClientHarness.AnonToken(1)));
                Assert.That(sent[1].Authorization, Is.EqualTo("Bearer " + ClientHarness.AnonToken(2)), "the new token");
                Assert.That(h.Http.Count("POST", ClientHarness.IssuePath), Is.EqualTo(2));
                Assert.That(h.Changes, Is.EqualTo(new[] { PlayerTokenChange.Issued, PlayerTokenChange.Rejected, PlayerTokenChange.Refreshed, PlayerTokenChange.IdentityChanged }));
                Assert.That(h.TokenEvents[3].PreviousPlayerId, Is.EqualTo(ClientHarness.AnonPlayer(1)));
                Assert.That(h.TokenEvents[3].PlayerId, Is.EqualTo(ClientHarness.AnonPlayer(2)));
                Assert.That(sent[1].Json["playerIds"].ToObject<string[]>(), Is.EqualTo(new[] { ClientHarness.AnonPlayer(2) }), "a 401 on the first attempt: resent under the new player id");
                Assert.That(h.Store.TryLoad(h.Client.Tokens.StoreKey, out StoredPlayerToken stored), Is.True);
                Assert.That(stored.Token, Is.EqualTo(ClientHarness.AnonToken(2)), "the rejected token was replaced in the store");

                // Neither token value reaches a log line, an event or a ToString.
                // Mutation: add the token to the Rejected log line and this fails.
                h.AssertPrintedNoToken(ClientHarness.AnonToken(1), ClientHarness.AnonToken(2));
                Assert.That(ClientHarness.Leaks(new[] { r.ToString(), r.Message ?? string.Empty }, ClientHarness.AnonToken(1), ClientHarness.AnonToken(2)), Is.Empty);
            }
        }

        [Test]
        public async Task ASecond401IsUnauthorizedWithNoThirdAttempt()
        {
            using (var h = new ClientHarness(maxAttempts: 3))
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk(), h.IssueOk(), h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, Step.Error(401, "The token audience does not match this app.", "audience_mismatch"));

                DiscoveryResult<TicketHandle> r = await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(new TicketOptions { SessionSize = 4 }, None));

                Assert.That(r.Outcome, Is.EqualTo(DiscoveryOutcome.Unauthorized));
                Assert.That(r.Reason, Is.EqualTo(DiscoveryReason.AudienceMismatch));
                // Mutation: loop the re-issue instead of doing it once and this is 3 (or the retry budget).
                Assert.That(h.Http.Count("POST", ClientHarness.TicketsPath), Is.EqualTo(2));
                Assert.That(h.Http.Count("POST", ClientHarness.IssuePath), Is.EqualTo(2));
                var tickets = h.Http.To("POST", ClientHarness.TicketsPath);
                Assert.That((string)tickets[1].Json["ticketId"], Is.EqualTo((string)tickets[0].Json["ticketId"]), "the resend carries the same ticket id");
            }
        }

        [Test]
        public async Task A401WithoutAPlayerTokenReasonIsReturnedWithoutReissuing()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("GET", ClientHarness.ReservationPath, Step.Error(401, "Missing bearer token."));
                DiscoveryResult<ReservationRecord> r = await h.Scheduler.RunAsync(h.Client.GetReservationAsync("own-1", None));
                Assert.That(r.Outcome, Is.EqualTo(DiscoveryOutcome.Unauthorized));
                Assert.That(h.Http.Count("GET", ClientHarness.ReservationPath), Is.EqualTo(1));
                Assert.That(h.Http.Count("POST", ClientHarness.IssuePath), Is.EqualTo(1));
                Assert.That(h.Client.Tokens.Current, Is.Not.Null, "the token was not dropped");
            }
        }

        [Test]
        public async Task AReserveRetriedAfterALostAnswerReusesTheMintedReservationId()
        {
            using (var h = new ClientHarness(maxAttempts: 3))
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.ReservePath, Step.Unreachable("connection reset"), h.ReservationOk("echoed", replayed: true));

                DiscoveryResult<ReservationResponse> r = await h.Scheduler.RunAsync(h.Client.ReserveAsync("agent-a-1", new ReserveOptions(), None));

                Assert.That(r.IsOk, Is.True);
                Assert.That(r.Value.Replayed, Is.True);
                var sent = h.Http.To("POST", ClientHarness.ReservePath);
                Assert.That(sent.Count, Is.EqualTo(2));
                string first = (string)sent[0].Json["reservationId"];
                Assert.That(first, Does.Match("^[A-Za-z0-9_-]{22}$"), "minted by the SDK");
                // Mutation: mint the id inside the request builder and the retry carries a new one.
                Assert.That((string)sent[1].Json["reservationId"], Is.EqualTo(first));
                Assert.That(sent[1].At - sent[0].At, Is.EqualTo(TimeSpan.FromSeconds(2)), "the governor's first backoff");
                Assert.That(sent[0].Json["playerIds"].ToObject<string[]>(), Is.EqualTo(new[] { ClientHarness.AnonPlayer(1) }), "one seat names the token's own player");
                Assert.That((int)sent[0].Json["seats"], Is.EqualTo(1));
            }
        }

        [Test]
        public async Task AQuickJoinRetriedAfterA503ReusesTheMintedIdempotencyKey()
        {
            using (var h = new ClientHarness(maxAttempts: 3))
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.QuickJoinPath, Step.Error(503, "Rate limiting is temporarily unavailable. Retry shortly."), Step.Error(503, "again"), h.ReservationOk("qj-hold", replayed: true));

                DiscoveryResult<ReservationResponse> r = await h.Scheduler.RunAsync(h.Client.QuickJoinAsync(new QuickJoinOptions
                {
                    Latency = new System.Collections.Generic.Dictionary<string, int> { ["eu-west-ams"] = 20 },
                    MaxLatencyMs = 80,
                    Filters = new QuickJoinFilters { Version = "1.0.0" }.WithMeta("proto", 2L).WithMetaWhere("xp", MetaOp.Gt, 10L),
                }, None));

                Assert.That(r.IsOk, Is.True);
                var sent = h.Http.To("POST", ClientHarness.QuickJoinPath);
                Assert.That(sent.Count, Is.EqualTo(3));
                string key = (string)sent[0].Json["idempotencyKey"];
                Assert.That(sent.Select(s => (string)s.Json["idempotencyKey"]), Is.EqualTo(new[] { key, key, key }));
                Assert.That(sent[2].At - sent[0].At, Is.EqualTo(TimeSpan.FromSeconds(6)), "2 s then 4 s");
                Assert.That(JToken.DeepEquals(sent[0].Json["filters"], JObject.Parse("{\"version\":\"1.0.0\",\"meta\":{\"proto\":2,\"xp\":{\"gt\":10}}}")), Is.True, sent[0].Body);
                Assert.That(JToken.DeepEquals(sent[0].Json["latency"], JObject.Parse("{\"eu-west-ams\":20}")), Is.True);
                Assert.That((int)sent[0].Json["maxLatencyMs"], Is.EqualTo(80));
            }
        }

        [Test]
        public async Task AGivenIdempotencyKeyReplaysAndA429LongerThanTheCeilingIsSurfacedUntried()
        {
            using (var h = new ClientHarness(maxAttempts: 3))
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", ClientHarness.QuickJoinPath, Step.Error(429, "slow down", null, ("Retry-After", "45")));
                DiscoveryResult<ReservationResponse> r = await h.Scheduler.RunAsync(h.Client.QuickJoinAsync(new QuickJoinOptions { IdempotencyKey = "join-77" }, None));
                Assert.That(r.Outcome, Is.EqualTo(DiscoveryOutcome.RateLimited));
                Assert.That(r.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(45)));
                Assert.That(h.Http.Count("POST", ClientHarness.QuickJoinPath), Is.EqualTo(1), "45 s is past the 10 s retry ceiling");
                Assert.That((string)h.Http.To("POST", ClientHarness.QuickJoinPath)[0].Json["idempotencyKey"], Is.EqualTo("join-77"));
            }
        }

        [TestCase(9, 1, "Player tokens may reserve at most 8 seats per reservation.")]
        [TestCase(2, 1, "\"playerIds\" has 1 entries but \"seats\" is 2; they must match.")]
        public async Task SeatChecksAreLocalAndSendNothing(int seats, int ids, string message)
        {
            using (var h = new ClientHarness())
            {
                var options = new ReserveOptions { Seats = seats, PlayerIds = Enumerable.Range(0, ids).Select(i => "p" + i).ToList() };
                DiscoveryResult<ReservationResponse> r = await h.Client.ReserveAsync("agent-a-1", options, None);
                Assert.That(r.IsLocalRefusal, Is.True);
                Assert.That(r.Message, Is.EqualTo(message));
                Assert.That(h.Http.Requests, Is.Empty);
            }
        }

        [Test]
        public async Task QuickJoinWithACeilingButNoMapIsRefusedLocallyWithTheServicesReason()
        {
            using (var h = new ClientHarness())
            {
                DiscoveryResult<ReservationResponse> r = await h.Client.QuickJoinAsync(new QuickJoinOptions { MaxLatencyMs = 80 }, None);
                Assert.That(r.IsLocalRefusal, Is.True);
                Assert.That(r.Reason, Is.EqualTo(DiscoveryReason.MaxLatencyWithoutMap));
                Assert.That(h.Http.Requests, Is.Empty);
            }
        }

        [Test]
        public async Task TheServerIdIsEscapedInThePathBecauseASelfHostedIdHoldsAColon()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("POST", "/v1/apps/dscp_a+/servers/203\\.0\\.113\\.7%3A7777/reservations", h.ReservationOk("x"));
                DiscoveryResult<ReservationResponse> r = await h.Scheduler.RunAsync(h.Client.ReserveAsync("203.0.113.7:7777", null, None));
                Assert.That(r.IsOk, Is.True, r.ToString());
                Assert.That(h.Http.Unscripted, Is.Empty);
            }
        }

        [Test]
        public async Task ReservationReadAndReleaseUseTheirRoutes()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                h.Http.On("GET", "/v1/apps/dscp_a+/reservations/own-1", Step.Json(200, "{\"error\":false,\"reservationId\":\"own-1\",\"serverId\":\"agent-a-1\",\"status\":\"pending\",\"seats\":1,\"playerIds\":null,\"createdAt\":1,\"expiresAt\":2,\"ownerKind\":\"player\",\"ownerPlayerId\":\"alice\"}"));
                h.Http.On("DELETE", "/v1/apps/dscp_a+/reservations/own-1", Step.Json(200, "{\"error\":false,\"reservationId\":\"own-1\",\"released\":true}"));
                DiscoveryResult<ReservationRecord> read = await h.Scheduler.RunAsync(h.Client.GetReservationAsync("own-1", None));
                Assert.That(read.IsOk, Is.True);
                Assert.That(read.Value.PlayerIdsSpecified, Is.True);
                Assert.That(read.Value.PlayerIds, Is.Null);
                DiscoveryResult<ReleaseReservationResponse> released = await h.Scheduler.RunAsync(h.Client.ReleaseReservationAsync("own-1", None));
                Assert.That(released.Value.Released, Is.True);
                Assert.That((await h.Client.GetReservationAsync("bad id", None)).IsLocalRefusal, Is.True);
            }
        }
    }
}
