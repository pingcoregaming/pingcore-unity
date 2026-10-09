using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Discovery.Client.Wire;
using Step = PingCore.Discovery.Client.Tests.Editor.FakeDiscoveryTransport.Step;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>
    /// A 401 re-issue that changes the player id: the calls bound to the old player (reservation read and
    /// release, ticket poll and cancel) are not resent, answer Unauthorized with
    /// <see cref="DiscoveryCallResult.IdentityChanged"/>, and the cache raises
    /// <see cref="PlayerTokenChange.IdentityChanged"/>. Reserve, quick join and submit are resent after a
    /// 401 on their first attempt, but not once an earlier attempt of the same call may have reached
    /// Discovery (a 503, an unexpected answer or no answer), because their idempotency anchors hold only
    /// under the same player.
    /// </summary>
    public sealed class TokenIdentityTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        private static Step Expired() => Step.Error(401, "The player token has expired.", "token_expired");

        private static void AssertIdentityChanged(ClientHarness h, DiscoveryCallResult r)
        {
            Assert.That(r.Outcome, Is.EqualTo(DiscoveryOutcome.Unauthorized), r.ToString());
            Assert.That(r.IdentityChanged, Is.True, r.ToString());
            Assert.That(r.Status, Is.EqualTo(401));
            Assert.That(r.Reason, Is.EqualTo(DiscoveryReason.TokenExpired), "the first 401's reason");
            Assert.That(r.ToString(), Does.Contain("identityChanged"));
            Assert.That(h.Changes, Is.EqualTo(new[] { PlayerTokenChange.Issued, PlayerTokenChange.Rejected, PlayerTokenChange.Refreshed, PlayerTokenChange.IdentityChanged }));
            PlayerTokenEvent changed = h.TokenEvents[3];
            Assert.That(changed.PreviousPlayerId, Is.EqualTo(ClientHarness.AnonPlayer(1)));
            Assert.That(changed.PlayerId, Is.EqualTo(ClientHarness.AnonPlayer(2)));
            Assert.That(changed.ToString(), Does.Contain("previousPlayerId=" + ClientHarness.AnonPlayer(1)));
            h.AssertPrintedNoToken(ClientHarness.AnonToken(1), ClientHarness.AnonToken(2));
        }

        private const string MayHaveLandedMessage = "an earlier attempt may already have reached Discovery";

        [Test]
        public async Task AReserveIsNotResentUnderTheNewPlayerAfterA503ThenA401()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk(), h.IssueOk());
                h.Http.On("POST", ClientHarness.ReservePath, Step.Error(503, "degraded"), Expired(), h.ReservationOk("r-1", playerId: ClientHarness.AnonPlayer(2)));

                DiscoveryResult<ReservationResponse> r = await h.Scheduler.RunAsync(h.Client.ReserveAsync("agent-a-1", new ReserveOptions { ReservationId = "r-1" }, None));

                // Mutation: give reserve no binding (resend whatever came before) and a third request goes out under
                // the new player and answers 200: a second hold, while the first, under the old player, may linger.
                var sent = h.Http.To("POST", ClientHarness.ReservePath);
                Assert.That(sent.Count, Is.EqualTo(2), "the 503 and the 401; nothing under the new player");
                Assert.That(sent.Select(s => s.Authorization), Is.EqualTo(new[] { "Bearer " + ClientHarness.AnonToken(1), "Bearer " + ClientHarness.AnonToken(1) }));
                Assert.That(sent.Select(s => (string)s.Json["reservationId"]), Is.EqualTo(new[] { "r-1", "r-1" }));
                Assert.That(h.Http.Count("POST", ClientHarness.IssuePath), Is.EqualTo(2), "the token was still re-issued");
                AssertIdentityChanged(h, r);
                Assert.That(r.Message, Does.Contain(MayHaveLandedMessage));
                Assert.That(r.Value, Is.Null);
            }
        }

        [Test]
        public async Task AQuickJoinIsNotResentUnderTheNewPlayerAfterA503ThenA401()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk(), h.IssueOk());
                h.Http.On("POST", ClientHarness.QuickJoinPath, Step.Error(503, "degraded"), Expired(), h.ReservationOk("qj-hold", playerId: ClientHarness.AnonPlayer(2)));

                DiscoveryResult<ReservationResponse> r = await h.Scheduler.RunAsync(h.Client.QuickJoinAsync(new QuickJoinOptions { IdempotencyKey = "join-1" }, None));

                // Mutation: give quick join no binding and the key, which Discovery scopes per player, takes a second
                // hold for the new player.
                var sent = h.Http.To("POST", ClientHarness.QuickJoinPath);
                Assert.That(sent.Count, Is.EqualTo(2), "the 503 and the 401; nothing under the new player");
                Assert.That(sent.Select(s => s.Authorization), Is.EqualTo(new[] { "Bearer " + ClientHarness.AnonToken(1), "Bearer " + ClientHarness.AnonToken(1) }));
                Assert.That(sent.Select(s => (string)s.Json["idempotencyKey"]), Is.EqualTo(new[] { "join-1", "join-1" }));
                AssertIdentityChanged(h, r);
                Assert.That(r.Message, Does.Contain(MayHaveLandedMessage));
            }
        }

        [Test]
        public async Task AQuickJoinIsNotResentUnderTheNewPlayerAfterNoAnswerThenA401()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk(), h.IssueOk());
                h.Http.On("POST", ClientHarness.QuickJoinPath, Step.Unreachable("connection reset"), Expired(), h.ReservationOk("qj-hold", playerId: ClientHarness.AnonPlayer(2)));

                DiscoveryResult<ReservationResponse> r = await h.Scheduler.RunAsync(h.Client.QuickJoinAsync(new QuickJoinOptions { IdempotencyKey = "join-1" }, None));

                // Mutation: count only a 503 as "may have landed" and a reset lets the resend through.
                Assert.That(h.Http.Count("POST", ClientHarness.QuickJoinPath), Is.EqualTo(2));
                AssertIdentityChanged(h, r);
            }
        }

        [Test]
        public async Task ASubmitIsNotResentUnderTheNewPlayerAfterA503ThenA401()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk(), h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, Step.Error(503, "degraded"), Expired(), h.SubmitOk());

                DiscoveryResult<TicketHandle> r = await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(new TicketOptions { SessionSize = 4 }, None));

                // Mutation: give submit no binding and the resend under the new player gets a handle here (in production
                // a 409 ticket_id_taken), while the old player's ticket may sit queued and match.
                var sent = h.Http.To("POST", ClientHarness.TicketsPath);
                Assert.That(sent.Count, Is.EqualTo(2), "the 503 and the 401; nothing under the new player");
                Assert.That(sent.Select(s => s.Authorization), Is.EqualTo(new[] { "Bearer " + ClientHarness.AnonToken(1), "Bearer " + ClientHarness.AnonToken(1) }));
                Assert.That((string)sent[1].Json["ticketId"], Is.EqualTo((string)sent[0].Json["ticketId"]));
                AssertIdentityChanged(h, r);
                Assert.That(r.Message, Does.Contain(MayHaveLandedMessage));
                Assert.That(r.Value, Is.Null, "no handle");
            }
        }

        [Test]
        public async Task AQuickJoinWhoseFirstAttemptIs401IsResentUnderTheNewPlayer()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk(), h.IssueOk());
                h.Http.On("POST", ClientHarness.QuickJoinPath, Expired(), h.ReservationOk("qj-hold", playerId: ClientHarness.AnonPlayer(2)));

                DiscoveryResult<ReservationResponse> r = await h.Scheduler.RunAsync(h.Client.QuickJoinAsync(new QuickJoinOptions { IdempotencyKey = "join-1" }, None));

                // Control for the tests above: a 401 first means nothing was acted on, so the resend is safe.
                // Mutation: bind quick join always and this is IdentityChanged after one request.
                Assert.That(r.IsOk, Is.True, r.ToString());
                Assert.That(r.IdentityChanged, Is.False);
                var sent = h.Http.To("POST", ClientHarness.QuickJoinPath);
                Assert.That(sent.Select(s => s.Authorization), Is.EqualTo(new[] { "Bearer " + ClientHarness.AnonToken(1), "Bearer " + ClientHarness.AnonToken(2) }));
                Assert.That(sent.Select(s => (string)s.Json["idempotencyKey"]), Is.EqualTo(new[] { "join-1", "join-1" }));
                Assert.That(h.Changes.Last(), Is.EqualTo(PlayerTokenChange.IdentityChanged), "the game is still told");
            }
        }

        [Test]
        public async Task AReserveAfterA429ThenA401IsResentBecauseA429WasNotActedOn()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk(), h.IssueOk());
                h.Http.On("POST", ClientHarness.ReservePath, Step.Error(429, "slow down", null, ("Retry-After", "1")), Expired(), h.ReservationOk("r-1", playerId: ClientHarness.AnonPlayer(2)));

                DiscoveryResult<ReservationResponse> r = await h.Scheduler.RunAsync(h.Client.ReserveAsync("agent-a-1", new ReserveOptions { ReservationId = "r-1" }, None));

                // Mutation: treat every retryable outcome, 429 included, as "may have landed" and this is IdentityChanged.
                Assert.That(r.IsOk, Is.True, r.ToString());
                var sent = h.Http.To("POST", ClientHarness.ReservePath);
                Assert.That(sent.Select(s => s.Authorization), Is.EqualTo(new[] { "Bearer " + ClientHarness.AnonToken(1), "Bearer " + ClientHarness.AnonToken(1), "Bearer " + ClientHarness.AnonToken(2) }));
            }
        }

        [TestCase(DiscoveryOutcome.Degraded, true)]
        [TestCase(DiscoveryOutcome.Unexpected, true)]
        [TestCase(DiscoveryOutcome.Unreachable, true)]
        [TestCase(DiscoveryOutcome.Ok, false)]
        [TestCase(DiscoveryOutcome.InvalidRequest, false)]
        [TestCase(DiscoveryOutcome.Unauthorized, false)]
        [TestCase(DiscoveryOutcome.Forbidden, false)]
        [TestCase(DiscoveryOutcome.NotFound, false)]
        [TestCase(DiscoveryOutcome.Conflict, false)]
        [TestCase(DiscoveryOutcome.RateLimited, false)]
        [TestCase(DiscoveryOutcome.Cancelled, false)]
        [TestCase(DiscoveryOutcome.Unsupported, false)]
        public void OnlyA503AnUnexpectedAnswerOrNoAnswerMayHaveLanded(DiscoveryOutcome outcome, bool expected)
        {
            Assert.That(DiscoveryClient.MayHaveLanded(outcome), Is.EqualTo(expected));
        }

        [Test]
        public async Task AReservationReadIsNotResentUnderTheNewAnonymousPlayer()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk(), h.IssueOk());
                h.Http.On("GET", ClientHarness.ReservationPath, Expired(), Step.Error(404, "Unknown reservation (it may have expired)."));

                DiscoveryResult<ReservationRecord> r = await h.Scheduler.RunAsync(h.Client.GetReservationAsync("own-1", None));

                // Mutation: pass ownerBound false for the read and it is resent, answering a misleading NotFound.
                Assert.That(h.Http.Count("GET", ClientHarness.ReservationPath), Is.EqualTo(1), "not resent");
                Assert.That(h.Http.Count("POST", ClientHarness.IssuePath), Is.EqualTo(2), "the token was still re-issued");
                AssertIdentityChanged(h, r);
                Assert.That(r.Value, Is.Null);
            }
        }

        [Test]
        public async Task AReservationReleaseIsNotResentUnderTheNewAnonymousPlayer()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk(), h.IssueOk());
                h.Http.On("DELETE", ClientHarness.ReservationPath, Expired(), Step.Json(200, "{\"error\":false,\"reservationId\":\"own-1\",\"released\":true}"));

                DiscoveryResult<ReleaseReservationResponse> r = await h.Scheduler.RunAsync(h.Client.ReleaseReservationAsync("own-1", None));

                // Mutation: pass ownerBound false and the resend answers a misleading 200 released.
                Assert.That(h.Http.Count("DELETE", ClientHarness.ReservationPath), Is.EqualTo(1));
                AssertIdentityChanged(h, r);
            }
        }

        [Test]
        public async Task ATicketPollAfterAnIdentityChangeEndsTheTicketFailed()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk(), h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk());
                h.Http.On("GET", ClientHarness.TicketPath, Expired(), h.PollOk(true));
                TicketHandle ticket = (await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(new TicketOptions { SessionSize = 4 }, None))).Value;

                await h.Scheduler.RunAsync(ticket.WaitAsync(None));

                Assert.That(h.Http.Count("GET", ClientHarness.TicketPath), Is.EqualTo(1), "the poll was not resent under the new player");
                Assert.That(ticket.State, Is.EqualTo(TicketState.Failed));
                Assert.That(ticket.Match, Is.Null);
                AssertIdentityChanged(h, ticket.LastError);
            }
        }

        [Test]
        public async Task ATicketCancelAfterAnIdentityChangeEndsTheTicketFailed()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk(), h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, h.SubmitOk());
                h.Http.On("DELETE", ClientHarness.TicketPath, Expired(), Step.Json(200, "{\"error\":false,\"ticketId\":\"echoed\",\"cancelled\":true}"));
                TicketHandle ticket = (await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(new TicketOptions { SessionSize = 4 }, None))).Value;

                DiscoveryResult<CancelTicketResponse> r = await h.Scheduler.RunAsync(ticket.CancelAsync(None));

                Assert.That(h.Http.Count("DELETE", ClientHarness.TicketPath), Is.EqualTo(1), "the cancel was not resent");
                // Mutation: drop the IdentityChanged case in CancelAsync and the ticket stays Queued.
                Assert.That(ticket.State, Is.EqualTo(TicketState.Failed));
                Assert.That(ticket.LastError, Is.SameAs(r));
                AssertIdentityChanged(h, r);
            }
        }

        [Test]
        public async Task AProviderTokenForAnotherSubIsAnIdentityChangeTooButTheSameSubIsResent()
        {
            using (var h = new ClientHarness())
            {
                DateTimeOffset exp = h.Scheduler.UtcNow.AddMinutes(30);
                string[] tokens = { TestJwt.Make("studio:alice", exp, "c2lnLWExYWxpY2Ux"), TestJwt.Make("studio:bob", exp, "c2lnLWIyYm9iYm9i") };
                int provided = 0;
                h.Client.Tokens.SetSignedTokenProvider(_ => Task.FromResult(tokens[Math.Min(provided++, 1)]));
                h.Http.On("GET", ClientHarness.ReservationPath, Step.Error(401, "The token signature does not verify.", "bad_signature"), Step.Error(404, "Unknown reservation (it may have expired)."));

                DiscoveryResult<ReservationRecord> r = await h.Scheduler.RunAsync(h.Client.GetReservationAsync("own-1", None));

                Assert.That(r.IdentityChanged, Is.True, r.ToString());
                Assert.That(h.Http.Count("GET", ClientHarness.ReservationPath), Is.EqualTo(1));
                Assert.That(h.Changes, Is.EqualTo(new[] { PlayerTokenChange.SignedSet, PlayerTokenChange.Rejected, PlayerTokenChange.SignedSet, PlayerTokenChange.IdentityChanged }));
                Assert.That(h.TokenEvents.Last().PreviousPlayerId, Is.EqualTo("studio:alice"));
                h.AssertPrintedNoToken(tokens);
            }
        }

        [Test]
        public async Task ASubmitWhoseFirstAttemptIs401IsResentWithTheSameTicketId()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk(), h.IssueOk());
                h.Http.On("POST", ClientHarness.TicketsPath, Expired(), h.SubmitOk());

                DiscoveryResult<TicketHandle> r = await h.Scheduler.RunAsync(h.Client.SubmitTicketAsync(new TicketOptions { SessionSize = 4 }, None));

                Assert.That(r.IsOk, Is.True, r.ToString());
                Assert.That(r.IdentityChanged, Is.False);
                var sent = h.Http.To("POST", ClientHarness.TicketsPath);
                Assert.That(sent.Count, Is.EqualTo(2), "a 401 on the first attempt: no ticket existed before it");
                Assert.That((string)sent[1].Json["ticketId"], Is.EqualTo((string)sent[0].Json["ticketId"]));
                Assert.That(r.Value.PlayerId, Is.EqualTo(ClientHarness.AnonPlayer(2)), "the handle names the player that holds the ticket");
                Assert.That(h.Changes.Last(), Is.EqualTo(PlayerTokenChange.IdentityChanged), "the game is still told");
            }
        }
    }
}
