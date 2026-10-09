using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Unity;
using Step = PingCore.Discovery.Client.Tests.Editor.FakeDiscoveryTransport.Step;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>The anonymous token cache: single flight, persistence, the refresh margin, issuance pacing and the typed refusals.</summary>
    public sealed class PlayerTokenTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        [Test]
        public async Task ConcurrentCallersShareOneIssueRequest()
        {
            using (var h = new ClientHarness())
            {
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk().HeldBy(release));

                Task<DiscoveryResult<PlayerToken>> first = h.Client.Tokens.GetAsync(None);
                Task<DiscoveryResult<PlayerToken>> second = h.Client.Tokens.GetAsync(None);
                Task<DiscoveryResult<PlayerToken>> third = h.Client.Tokens.GetAsync(None);
                await TestScheduler.Until(() => h.Http.Count("POST", ClientHarness.IssuePath) == 1, "the first issue request");
                release.SetResult(true);
                DiscoveryResult<PlayerToken>[] all = await Task.WhenAll(first, second, third);

                // Mutation: drop the in-flight check in GetAsync and three requests go out.
                Assert.That(h.Http.Count("POST", ClientHarness.IssuePath), Is.EqualTo(1), "single flight");
                Assert.That(all.Select(r => r.IsOk), Is.All.True);
                Assert.That(all.Select(r => r.Value.PlayerId).Distinct(), Is.EqualTo(new[] { ClientHarness.AnonPlayer(1) }));
                Assert.That(h.Changes, Is.EqualTo(new[] { PlayerTokenChange.Issued }));
                Assert.That(h.Client.Tokens.Current.Kind, Is.EqualTo(PlayerTokenKind.Anonymous));
            }
        }

        [Test]
        public async Task AnIssuedTokenIsPersistedUnderTheProfileKeyAndARestartLoadsItWithoutIssuing()
        {
            var store = new MemoryTokenStore();
            using (var first = new ClientHarness(profile: "client-1", store: store))
            {
                first.Http.On("POST", ClientHarness.IssuePath, first.IssueOk());
                Assert.That((await first.Client.Tokens.GetAsync(None)).IsOk, Is.True);
                Assert.That(store.TryLoad("pingcore.playerToken.client-1." + ClientHarness.AppId, out StoredPlayerToken saved), Is.True, "the documented key");
                Assert.That(saved.Token, Is.EqualTo(ClientHarness.AnonToken(1)));
                Assert.That(saved.PlayerId, Is.EqualTo(ClientHarness.AnonPlayer(1)));
                Assert.That(first.Client.Tokens.StoreKey, Is.EqualTo("pingcore.playerToken.client-1." + ClientHarness.AppId));
            }

            // A restart with the same store and profile: the token is loaded, nothing is sent.
            using (var restart = new ClientHarness(profile: "client-1", store: store))
            {
                DiscoveryResult<PlayerToken> loaded = await restart.Client.Tokens.GetAsync(None);
                Assert.That(loaded.IsOk, Is.True);
                Assert.That(loaded.Value.PlayerId, Is.EqualTo(ClientHarness.AnonPlayer(1)));
                Assert.That(restart.Http.Requests, Is.Empty, "no issue after a restart");
                Assert.That(restart.Changes, Is.EqualTo(new[] { PlayerTokenChange.Loaded }));
            }

            // Another profile on the same store is another player: it issues its own token.
            using (var other = new ClientHarness(profile: "client-2", store: store))
            {
                other.Http.On("POST", ClientHarness.IssuePath, other.IssueOk());
                Assert.That((await other.Client.Tokens.GetAsync(None)).IsOk, Is.True);
                Assert.That(other.Http.Count("POST", ClientHarness.IssuePath), Is.EqualTo(1), "a separate profile does not share the player id");
                Assert.That(store.Count, Is.EqualTo(2));
            }
        }

        [Test]
        public async Task AStoredTokenIsUsedWhileMoreThanFiveMinutesRemainAndReplacedOnceLess()
        {
            var store = new MemoryTokenStore();
            var scheduler = new TestScheduler();
            string key = PlayerTokenStoreKeys.For(string.Empty, ClientHarness.AppId);
            store.Save(key, new StoredPlayerToken("stored-jwt", "anon:stored", scheduler.UtcNow.AddMinutes(5).AddSeconds(1).ToUnixTimeMilliseconds()));
            using (var h = new ClientHarness(store: store, scheduler: scheduler))
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                DiscoveryResult<PlayerToken> kept = await h.Client.Tokens.GetAsync(None);
                Assert.That(kept.Value.PlayerId, Is.EqualTo("anon:stored"), "5 min 1 s left: still usable");
                Assert.That(h.Http.Requests, Is.Empty);

                // Two seconds later less than five minutes remain: the cache issues a new token.
                scheduler.Advance(TimeSpan.FromSeconds(2));
                DiscoveryResult<PlayerToken> renewed = await h.Client.Tokens.GetAsync(None);
                Assert.That(renewed.Value.PlayerId, Is.EqualTo(ClientHarness.AnonPlayer(1)));
                Assert.That(h.Http.Count("POST", ClientHarness.IssuePath), Is.EqualTo(1));
                Assert.That(h.Changes, Is.EqualTo(new[] { PlayerTokenChange.Loaded, PlayerTokenChange.Refreshed, PlayerTokenChange.IdentityChanged }), "a new anonymous token is a new player id");
                Assert.That(h.TokenEvents[2].PreviousPlayerId, Is.EqualTo("anon:stored"));
            }
        }

        [Test]
        public async Task AStoredTokenWithUnderFiveMinutesLeftIsDiscardedAtLoad()
        {
            var store = new MemoryTokenStore();
            var scheduler = new TestScheduler();
            string key = PlayerTokenStoreKeys.For(string.Empty, ClientHarness.AppId);
            store.Save(key, new StoredPlayerToken("stale-jwt", "anon:stale", scheduler.UtcNow.AddMinutes(4).ToUnixTimeMilliseconds()));
            using (var h = new ClientHarness(store: store, scheduler: scheduler))
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                DiscoveryResult<PlayerToken> token = await h.Client.Tokens.GetAsync(None);
                Assert.That(token.Value.PlayerId, Is.EqualTo(ClientHarness.AnonPlayer(1)));
                Assert.That(h.Changes, Is.EqualTo(new[] { PlayerTokenChange.Issued }), "the stale token is never offered");
            }
        }

        [Test]
        public async Task A429IsSurfacedWithItsRetryAfterAndNothingIsSentBeforeItPasses()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, Step.Error(429, "Too many player-token requests from this address.", null, ("Retry-After", "30")), h.IssueOk());

                DiscoveryResult<PlayerToken> limited = await h.Client.Tokens.GetAsync(None);
                Assert.That(limited.Outcome, Is.EqualTo(DiscoveryOutcome.RateLimited));
                Assert.That(limited.Status, Is.EqualTo(429));
                Assert.That(limited.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(30)));

                // Inside the window the cache answers locally and sends nothing.
                h.Scheduler.Advance(TimeSpan.FromSeconds(29));
                DiscoveryResult<PlayerToken> held = await h.Client.Tokens.GetAsync(None);
                Assert.That(held.Outcome, Is.EqualTo(DiscoveryOutcome.RateLimited));
                Assert.That(held.Status, Is.EqualTo(0), "a local answer");
                Assert.That(held.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(1)), "the time left");
                // Mutation: skip the BlockedFor check and a second request goes out here.
                Assert.That(h.Http.Count("POST", ClientHarness.IssuePath), Is.EqualTo(1));

                h.Scheduler.Advance(TimeSpan.FromSeconds(1));
                DiscoveryResult<PlayerToken> issued = await h.Client.Tokens.GetAsync(None);
                Assert.That(issued.IsOk, Is.True);
                Assert.That(h.Http.Count("POST", ClientHarness.IssuePath), Is.EqualTo(2));
            }
        }

        [Test]
        public async Task TwoIssuesInOneProcessAreAtLeastSixSecondsApartEvenAcrossClients()
        {
            var gate = new IssuanceGate();
            var scheduler = new TestScheduler();
            using (var a = new ClientHarness(profile: "a", gate: gate, scheduler: scheduler))
            using (var b = new ClientHarness(profile: "b", gate: gate, scheduler: scheduler))
            {
                a.Http.On("POST", ClientHarness.IssuePath, a.IssueOk());
                b.Http.On("POST", ClientHarness.IssuePath, b.IssueOk());
                Assert.That((await a.Client.Tokens.GetAsync(None)).IsOk, Is.True);
                Assert.That(scheduler.Delays, Is.Empty, "the first issue is not delayed");

                scheduler.Advance(TimeSpan.FromSeconds(2));
                DiscoveryResult<PlayerToken> second = await scheduler.RunAsync(b.Client.Tokens.GetAsync(None));
                Assert.That(second.IsOk, Is.True);
                // Mutation: make IssuanceGate.Reserve return zero and the delay list stays empty.
                Assert.That(scheduler.Delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(4) }), "paced to 6 s after the first");
                Assert.That(b.Http.To("POST", ClientHarness.IssuePath).Single().At, Is.EqualTo(a.Http.To("POST", ClientHarness.IssuePath).Single().At.AddSeconds(6)));
            }
        }

        [Test]
        public async Task AnonymousTokensDisabledIsATypedForbiddenAndNeverRetried()
        {
            using (var h = new ClientHarness(maxAttempts: 3))
            {
                h.Http.On("POST", ClientHarness.IssuePath, Step.Error(403, "Anonymous player tokens are turned off for this app.", "anonymous_tokens_disabled"));
                DiscoveryResult<Wire.ReservationResponse> r = await h.Scheduler.RunAsync(h.Client.ReserveAsync("agent-a-1", null, None));
                Assert.That(r.Outcome, Is.EqualTo(DiscoveryOutcome.Forbidden));
                Assert.That(r.Reason, Is.EqualTo(DiscoveryReason.AnonymousTokensDisabled));
                Assert.That(h.Http.Count("POST", ClientHarness.IssuePath), Is.EqualTo(1));
                Assert.That(h.Http.Count("POST", ClientHarness.ReservePath), Is.EqualTo(0));
            }
        }

        [Test]
        public async Task AnonymousTokensUnavailableIsATypedDegradedAndNeverRetriedWhileAPlain503Is()
        {
            using (var h = new ClientHarness(maxAttempts: 3))
            {
                h.Http.On("POST", ClientHarness.IssuePath, Step.Error(503, "Anonymous player tokens are unavailable on this deployment.", "anonymous_tokens_unavailable"));
                DiscoveryResult<Wire.ReservationResponse> r = await h.Scheduler.RunAsync(h.Client.ReserveAsync("agent-a-1", null, None));
                Assert.That(r.Outcome, Is.EqualTo(DiscoveryOutcome.Degraded));
                Assert.That(r.Reason, Is.EqualTo(DiscoveryReason.AnonymousTokensUnavailable));
                // Mutation: drop IsFinalPlayerTokenRefusal from the retry loop and this is 3.
                Assert.That(h.Http.Count("POST", ClientHarness.IssuePath), Is.EqualTo(1));
            }

            // Control: a 503 without that reason (registry not loaded) IS retried, so the count above can fail.
            using (var h = new ClientHarness(maxAttempts: 3))
            {
                h.Http.On("POST", ClientHarness.IssuePath, Step.Error(503, "Discovery is starting up and has not loaded its registry yet. Retry shortly."));
                DiscoveryResult<Wire.ReservationResponse> r = await h.Scheduler.RunAsync(h.Client.ReserveAsync("agent-a-1", null, None));
                Assert.That(r.Outcome, Is.EqualTo(DiscoveryOutcome.Degraded));
                Assert.That(h.Http.Count("POST", ClientHarness.IssuePath), Is.EqualTo(3));
            }
        }

        [Test]
        public async Task ASignedTokenIsUsedAsTheBearerAndAProviderReplacesItAfterA401()
        {
            using (var h = new ClientHarness())
            {
                DateTimeOffset exp = h.Scheduler.UtcNow.AddMinutes(30);
                string first = TestJwt.Make("studio:alice", exp, "c2lnLXNlY3JldC0x");
                string second = TestJwt.Make("studio:alice", exp, "c2lnLXNlY3JldC0y");
                int provided = 0;
                h.Client.Tokens.SetSignedToken(first);
                h.Client.Tokens.SetSignedTokenProvider(_ => { provided++; return Task.FromResult(second); });
                Assert.That(h.Client.Tokens.Current.Kind, Is.EqualTo(PlayerTokenKind.Signed));
                Assert.That(h.Client.Tokens.Current.PlayerId, Is.EqualTo("studio:alice"));
                Assert.That(h.Client.Tokens.Current.ToString(), Does.Not.Contain(first), "never printed");

                h.Http.On("GET", ClientHarness.ReservationPath, Step.Error(401, "The token signature does not verify.", "bad_signature"), Step.Error(404, "Unknown reservation (it may have expired)."));
                DiscoveryResult<Wire.ReservationRecord> r = await h.Scheduler.RunAsync(h.Client.GetReservationAsync("own-1", None));

                Assert.That(r.Outcome, Is.EqualTo(DiscoveryOutcome.NotFound));
                Assert.That(provided, Is.EqualTo(1));
                var sent = h.Http.To("GET", ClientHarness.ReservationPath);
                Assert.That(sent.Select(s => s.Authorization), Is.EqualTo(new[] { "Bearer " + first, "Bearer " + second }));
                Assert.That(h.Http.Count("POST", ClientHarness.IssuePath), Is.EqualTo(0), "with a provider the cache never issues an anonymous token");
                Assert.That(h.Changes, Is.EqualTo(new[] { PlayerTokenChange.SignedSet, PlayerTokenChange.Rejected, PlayerTokenChange.SignedSet }), "the same sub: no identity change, so the owner-bound read was resent");
                Assert.That(h.TokenEvents[1].Reason, Is.EqualTo(DiscoveryReason.BadSignature));
                Assert.That(r.IdentityChanged, Is.False);

                // Signed tokens are never stored, and neither JWT (nor its payload or signature) is printed.
                Assert.That(h.Store.Count, Is.EqualTo(0), "the provider-after-401 path stored nothing");
                // Mutation: append the token to the Rejected log line and this fails.
                h.AssertPrintedNoToken(first, second);
                Assert.That(ClientHarness.Leaks(new[] { r.ToString(), r.Message ?? string.Empty }, first, second), Is.Empty);
            }
        }

        [Test]
        public async Task SettingAProviderDropsTheAnonymousTokenInHandAndNeverIssuesAnother()
        {
            using (var h = new ClientHarness())
            {
                h.Http.On("POST", ClientHarness.IssuePath, h.IssueOk());
                Assert.That((await h.Client.Tokens.GetAsync(None)).Value.Kind, Is.EqualTo(PlayerTokenKind.Anonymous));
                string signed = TestJwt.Make("studio:bob", h.Scheduler.UtcNow.AddMinutes(30));
                h.Client.Tokens.SetSignedTokenProvider(_ => Task.FromResult(signed));

                DiscoveryResult<PlayerToken> token = await h.Client.Tokens.GetAsync(None);
                Assert.That(token.Value.Kind, Is.EqualTo(PlayerTokenKind.Signed));
                Assert.That(token.Value.PlayerId, Is.EqualTo("studio:bob"));
                Assert.That(h.Http.Count("POST", ClientHarness.IssuePath), Is.EqualTo(1));
                // Mutation: save the signed token in FromProviderAsync and the stored value is the JWT.
                Assert.That(h.Store.Count, Is.EqualTo(1), "the stored anonymous token is kept");
                Assert.That(h.Store.TryLoad(h.Client.Tokens.StoreKey, out StoredPlayerToken kept), Is.True);
                Assert.That(kept.Token, Is.EqualTo(ClientHarness.AnonToken(1)), "still the anonymous token, not the signed one");
                Assert.That(kept.PlayerId, Is.EqualTo(ClientHarness.AnonPlayer(1)));
            }
        }

        [Test]
        public async Task AProviderThatHasNoTokenOrThrowsIsATypedResultNotAThrow()
        {
            using (var h = new ClientHarness())
            {
                h.Client.Tokens.SetSignedTokenProvider(_ => Task.FromResult<string>(null));
                DiscoveryResult<PlayerToken> none = await h.Client.Tokens.GetAsync(None);
                Assert.That(none.Outcome, Is.EqualTo(DiscoveryOutcome.Unauthorized));
                Assert.That(none.Status, Is.EqualTo(0));

                h.Client.Tokens.SetSignedTokenProvider(_ => throw new InvalidOperationException("backend down"));
                DiscoveryResult<PlayerToken> thrown = await h.Client.Tokens.GetAsync(None);
                Assert.That(thrown.Outcome, Is.EqualTo(DiscoveryOutcome.Unexpected));
                Assert.That(thrown.Message, Does.Not.Contain("backend down"), "the provider's message is not repeated");
                Assert.That(h.Http.Requests, Is.Empty);
            }
        }

        [Test]
        public void AnUnreadableSignedTokenIsRefusedWithoutQuotingIt()
        {
            using (var h = new ClientHarness())
            {
                var e = Assert.Throws<ArgumentException>(() => h.Client.Tokens.SetSignedToken("not.a-jwt-secret-value"));
                Assert.That(e.Message, Does.Not.Contain("secret"));
                Assert.That(h.Client.Tokens.Current, Is.Null);
            }
        }

        [Test]
        public void TheClaimReaderReadsSubAndExpFromALiteralJwtAndRefusesMalformedOnes()
        {
            // Header and payload base64url-encoded independently with Node (Buffer.toString('base64url')).
            const string jwt = "eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCIsImtpZCI6ImsxIn0.eyJzdWIiOiJzdHVkaW86YWxpY2UiLCJhdWQiOiJkc2NwX3Rlc3QiLCJleHAiOjE3OTEyMDAwMDAsImlhdCI6MTc5MTE5NjQwMH0.c2ln";
            Assert.That(JwtClaims.TryRead(jwt, out string sub, out DateTimeOffset exp), Is.True);
            Assert.That(sub, Is.EqualTo("studio:alice"));
            Assert.That(exp.ToUnixTimeSeconds(), Is.EqualTo(1791200000));
            foreach (string bad in new[] { null, string.Empty, "a.b", "a.b.c.d", "eyJ.!!!.c2ln", "eyJhbGciOiJFUzI1NiJ9.eyJleHAiOjF9.c2ln" })
            {
                Assert.That(JwtClaims.TryRead(bad, out _, out _), Is.False, bad ?? "null");
            }
        }

        [Test]
        public void ThePlayerPrefsFormRoundTripsAndRefusesAnythingElse()
        {
            var token = new StoredPlayerToken("jwt-value", "anon:1", 1791200000000);
            string stored = PlayerPrefsTokenStore.Serialize(token);
            Assert.That(stored, Is.EqualTo("{\"v\":1,\"token\":\"jwt-value\",\"playerId\":\"anon:1\",\"expiresAtMs\":1791200000000}"));
            Assert.That(PlayerPrefsTokenStore.TryParse(stored, out StoredPlayerToken back), Is.True);
            Assert.That(back.Token, Is.EqualTo("jwt-value"));
            Assert.That(back.PlayerId, Is.EqualTo("anon:1"));
            Assert.That(back.ExpiresAtMs, Is.EqualTo(1791200000000));
            foreach (string bad in new[] { null, string.Empty, "x", "{}", "{\"v\":2,\"token\":\"t\",\"playerId\":\"p\",\"expiresAtMs\":1}", "{\"v\":1,\"token\":\"\",\"playerId\":\"p\",\"expiresAtMs\":1}", "{\"v\":1,\"token\":\"t\",\"playerId\":\"p\",\"expiresAtMs\":\"soon\"}" })
            {
                Assert.That(PlayerPrefsTokenStore.TryParse(bad, out _), Is.False, bad ?? "null");
            }
        }
    }
}
