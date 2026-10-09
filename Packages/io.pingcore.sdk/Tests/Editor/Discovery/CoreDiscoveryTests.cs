using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Discovery.Client.Wire;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>The Core pieces: the caller's classification, the retry governor, the id minting and the rate-limit headers.</summary>
    public sealed class CoreDiscoveryTests
    {
        private static PingCoreHttpResponse Answer(int status, string body, params (string, string)[] headers)
        {
            var map = new Dictionary<string, string>();
            foreach ((string name, string value) in headers)
            {
                map[name] = value;
            }

            return new PingCoreHttpResponse(status, map, body);
        }

        [TestCase(200, DiscoveryOutcome.Ok)]
        [TestCase(400, DiscoveryOutcome.InvalidRequest)]
        [TestCase(401, DiscoveryOutcome.Unauthorized)]
        [TestCase(403, DiscoveryOutcome.Forbidden)]
        [TestCase(404, DiscoveryOutcome.NotFound)]
        [TestCase(409, DiscoveryOutcome.Conflict)]
        [TestCase(429, DiscoveryOutcome.RateLimited)]
        [TestCase(503, DiscoveryOutcome.Degraded)]
        [TestCase(500, DiscoveryOutcome.Unexpected)]
        [TestCase(502, DiscoveryOutcome.Unexpected)]
        [TestCase(302, DiscoveryOutcome.Unexpected)]
        public void EachStatusMapsToItsOutcome(int status, DiscoveryOutcome expected)
        {
            Assert.That(DiscoveryCaller.OutcomeForStatus(status), Is.EqualTo(expected));
        }

        [Test]
        public void AnErrorBodyCarriesItsReasonNumbersRetryAfterAndBudget()
        {
            DiscoveryResult<ReservationResponse> r = DiscoveryCaller.Classify<ReservationResponse>(Answer(
                429,
                "{\"error\":true,\"message\":\"slow down\",\"reason\":\"too_many_reservations\",\"limit\":2,\"active\":2}",
                ("retry-after", "17"),
                ("RateLimit-Limit", "60"),
                ("ratelimit-remaining", "0"),
                ("RateLimit-Reset", "17"),
                ("RateLimit-Policy", "60;w=60")));

            Assert.That(r.Outcome, Is.EqualTo(DiscoveryOutcome.RateLimited));
            Assert.That(r.Status, Is.EqualTo(429));
            Assert.That(r.Reason, Is.EqualTo(DiscoveryReason.TooManyReservations));
            Assert.That(r.ReasonWire, Is.EqualTo("too_many_reservations"));
            Assert.That(r.Message, Is.EqualTo("slow down"));
            Assert.That(r.Error.Limit, Is.EqualTo(2));
            Assert.That(r.Error.Active, Is.EqualTo(2));
            Assert.That(r.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(17)), "header names are case-insensitive");
            Assert.That(r.RateLimit.Limit, Is.EqualTo(60));
            Assert.That(r.RateLimit.Remaining, Is.EqualTo(0));
            Assert.That(r.RateLimit.ResetSeconds, Is.EqualTo(17));
            Assert.That(r.RateLimit.Policy, Is.EqualTo("60;w=60"));
            Assert.That(r.Value, Is.Null);
        }

        [Test]
        public void ASuccessStatusWhoseBodyIsNotTheEnvelopeIsUnexpectedNotOk()
        {
            // Mutation: accept any 2xx as Ok and the first two cases pass as Ok.
            Assert.That(DiscoveryCaller.Classify<CancelTicketResponse>(Answer(200, "{\"error\":true,\"message\":\"x\"}")).Outcome, Is.EqualTo(DiscoveryOutcome.Unexpected));
            Assert.That(DiscoveryCaller.Classify<CancelTicketResponse>(Answer(200, "<html>proxy</html>")).Outcome, Is.EqualTo(DiscoveryOutcome.Unexpected));
            Assert.That(DiscoveryCaller.Classify<CancelTicketResponse>(Answer(200, null)).Outcome, Is.EqualTo(DiscoveryOutcome.Unexpected));
            DiscoveryResult<CancelTicketResponse> ok = DiscoveryCaller.Classify<CancelTicketResponse>(Answer(200, "{\"error\":false,\"ticketId\":\"t\",\"cancelled\":true}"));
            Assert.That(ok.Outcome, Is.EqualTo(DiscoveryOutcome.Ok), "control: a well-formed body is Ok");
            Assert.That(ok.Value.TicketId, Is.EqualTo("t"));
        }

        [Test]
        public void AnUnparseableErrorBodyStillClassifiesByStatus()
        {
            DiscoveryResult<CancelTicketResponse> r = DiscoveryCaller.Classify<CancelTicketResponse>(Answer(503, "Service Unavailable"));
            Assert.That(r.Outcome, Is.EqualTo(DiscoveryOutcome.Degraded));
            Assert.That(r.Error, Is.Null);
            Assert.That(r.Reason, Is.EqualTo(DiscoveryReason.Unknown));
            Assert.That(r.Message, Is.EqualTo("HTTP 503"));
        }

        [Test]
        public async Task ATransportFailureIsUnreachableAndItsMessageNeverQuotesTheUrl()
        {
            // The engine's error text can quote the request URL, and a poll URL carries the ticket id.
            const string ticketId = "q3Vx9mKc0TzR1bN4wYp7Lg";
            string engineError = "Cannot connect to destination host https://discovery.test/v1/apps/dscp_a/tickets/" + ticketId + "?x=1 (connection refused)";
            var scheduler = new TestScheduler();
            var http = new FakeDiscoveryTransport(scheduler).On("GET", "/v1/apps/dscp_a/tickets/" + ticketId, FakeDiscoveryTransport.Step.Unreachable(engineError));
            var caller = new DiscoveryCaller(FakeDiscoveryTransport.BaseUrl, http);
            DiscoveryResult<TicketResponse> r = await caller.SendAsync<TicketResponse>(
                DiscoveryRequest.Create("GET", "/v1/apps/dscp_a/tickets/" + ticketId, "GET /v1/apps/{publicId}/tickets/{ticketId}"), CancellationToken.None);
            Assert.That(r.Outcome, Is.EqualTo(DiscoveryOutcome.Unreachable));
            Assert.That(r.Status, Is.EqualTo(0));
            // Mutation: forward e.Message verbatim (the old code) and the first two assertions fail.
            Assert.That(r.Message, Does.Not.Contain(ticketId));
            Assert.That(r.Message, Does.Not.Contain("discovery.test"));
            Assert.That(r.Message, Does.Not.Contain("/v1/"));
            Assert.That(r.Message, Does.Contain("Cannot connect to destination host"), "the generic phrase stays");
            Assert.That(r.Message, Does.Contain("PingCoreTransportException"), "the exception type stays");
            Assert.That(r.ToString(), Does.Not.Contain(ticketId));

            // A bare path, a host with a path and a relative path are scrubbed too; plain words are kept.
            foreach (string leaky in new[] { "GET /v1/apps/x/tickets/" + ticketId + " failed", "discovery.test:443/v1/apps/x/tickets/" + ticketId, "http://[::1]:8080/t/" + ticketId })
            {
                string scrubbed = DiscoveryCaller.ScrubTransportMessage(leaky);
                Assert.That(scrubbed, Does.Not.Contain(ticketId), leaky);
                Assert.That(scrubbed, Does.Contain("<url>"), leaky);
            }

            Assert.That(DiscoveryCaller.ScrubTransportMessage("Request timeout"), Is.EqualTo("Request timeout"), "a message with no URL is unchanged");
            Assert.That(DiscoveryCaller.ScrubTransportMessage(null), Is.EqualTo("no detail"));
            Assert.That(DiscoveryCaller.ScrubTransportMessage(new string('x', 500)).Length, Is.EqualTo(200), "bounded");

            // Any other exception type is reported by name only, because its message may quote the URL.
            DiscoveryResult<LocationsResponse> other = DiscoveryCaller.FromException<LocationsResponse>(new InvalidOperationException("GET https://discovery.test/v1/apps/x/tickets/secret failed"), CancellationToken.None);
            Assert.That(other.Outcome, Is.EqualTo(DiscoveryOutcome.Unreachable));
            Assert.That(other.Message, Does.Not.Contain("secret"));
            Assert.That(other.Message, Does.Contain("InvalidOperationException"));
        }

        [Test]
        public void CancellationOfTheCallersTokenIsCancelledButATimeoutIsUnreachable()
        {
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.That(DiscoveryCaller.FromException<LocationsResponse>(new OperationCanceledException(cts.Token), cts.Token).Outcome, Is.EqualTo(DiscoveryOutcome.Cancelled));
            }

            Assert.That(DiscoveryCaller.FromException<LocationsResponse>(new TaskCanceledException(), CancellationToken.None).Outcome, Is.EqualTo(DiscoveryOutcome.Unreachable), "an HttpClient-style timeout is not the caller's cancellation");
        }

        [Test]
        public void TheRequestCarriesTheBearerOnlyInItsHeadersAndItsLabelIsTheTemplate()
        {
            DiscoveryRequest request = DiscoveryRequest.Json("post", "/v1/apps/dscp_a/tickets", new SubmitTicketRequest { SessionSize = 4 }, "POST /v1/apps/{publicId}/tickets").WithBearer("player-jwt-value");
            Assert.That(request.Method, Is.EqualTo("POST"));
            Assert.That(request.Headers()["Authorization"], Is.EqualTo("Bearer player-jwt-value"));
            Assert.That(request.ToString(), Is.EqualTo("POST /v1/apps/{publicId}/tickets"));
            Assert.That(request.ToString(), Does.Not.Contain("player-jwt-value"));
            Assert.That(request.JsonBody, Is.EqualTo("{\"sessionSize\":4}"), "unset optional fields are not sent");
            Assert.That(request.WithBearer(null).Headers().ContainsKey("Authorization"), Is.False);
        }

        [Test]
        public void TheGovernorBacksOffTwoFourEightThenCapsAtTen()
        {
            Assert.That(new[] { 1, 2, 3, 4, 5, 9 }, Has.All.Matches<int>(n => RetryGovernor.Backoff(n) <= RetryGovernor.BackoffCap));
            Assert.That(RetryGovernor.Backoff(1), Is.EqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(RetryGovernor.Backoff(2), Is.EqualTo(TimeSpan.FromSeconds(4)));
            Assert.That(RetryGovernor.Backoff(3), Is.EqualTo(TimeSpan.FromSeconds(8)));
            Assert.That(RetryGovernor.Backoff(4), Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(RetryGovernor.Backoff(9), Is.EqualTo(TimeSpan.FromSeconds(10)));
        }

        [Test]
        public void TheGovernorHonoursRetryAfterOnlyFor429AndGivesUpPastTheCallersCeilingOrAttempts()
        {
            TimeSpan ten = TimeSpan.FromSeconds(10);
            Assert.That(RetryGovernor.TryGetDelay(DiscoveryOutcome.RateLimited, 1, TimeSpan.FromSeconds(7), 3, ten, out TimeSpan d1), Is.True);
            Assert.That(d1, Is.EqualTo(TimeSpan.FromSeconds(7)));
            Assert.That(RetryGovernor.TryGetDelay(DiscoveryOutcome.RateLimited, 1, null, 3, ten, out TimeSpan d2), Is.True);
            Assert.That(d2, Is.EqualTo(TimeSpan.FromSeconds(2)), "a 429 without Retry-After backs off");
            Assert.That(RetryGovernor.TryGetDelay(DiscoveryOutcome.Degraded, 2, TimeSpan.FromSeconds(7), 3, ten, out TimeSpan d3), Is.True);
            Assert.That(d3, Is.EqualTo(TimeSpan.FromSeconds(4)), "a 503 backs off whatever Retry-After says");
            Assert.That(RetryGovernor.TryGetDelay(DiscoveryOutcome.RateLimited, 1, TimeSpan.FromSeconds(30), 3, ten, out _), Is.False, "a wait longer than the caller accepts surfaces the 429");
            Assert.That(RetryGovernor.TryGetDelay(DiscoveryOutcome.Unreachable, 3, null, 3, ten, out _), Is.False, "the last attempt is final");
            foreach (DiscoveryOutcome final in new[] { DiscoveryOutcome.Ok, DiscoveryOutcome.InvalidRequest, DiscoveryOutcome.Unauthorized, DiscoveryOutcome.Forbidden, DiscoveryOutcome.NotFound, DiscoveryOutcome.Conflict, DiscoveryOutcome.Cancelled, DiscoveryOutcome.Unsupported })
            {
                Assert.That(RetryGovernor.TryGetDelay(final, 1, null, 3, ten, out _), Is.False, final.ToString());
            }

            foreach (DiscoveryOutcome retryable in new[] { DiscoveryOutcome.RateLimited, DiscoveryOutcome.Degraded, DiscoveryOutcome.Unexpected, DiscoveryOutcome.Unreachable })
            {
                Assert.That(RetryGovernor.TryGetDelay(retryable, 1, null, 3, ten, out _), Is.True, retryable.ToString());
            }
        }

        [Test]
        public void MintedIdsAre22Base64UrlCharactersThatDiscoveryAcceptsAndNeverRepeat()
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < 200; i++)
            {
                string id = SecureIds.NewId128();
                Assert.That(id, Does.Match("^[A-Za-z0-9_-]{22}$"));
                Assert.That(SecureIds.IsValidId(id), Is.True);
                Assert.That(seen.Add(id), Is.True, "an id repeated");
            }
        }

        [Test]
        public void TheTicketRefIsTheFirst12HexOfTheSha256()
        {
            // Literals computed independently with Node's crypto (createHash('sha256')), never by SecureIds.
            Assert.That(SecureIds.Ref("party-77"), Is.EqualTo("2fdccfd6f8c0"));
            Assert.That(SecureIds.Ref("alice-1"), Is.EqualTo("a42ac5108869"));
            Assert.That(SecureIds.Ref("ticket-é"), Is.EqualTo("c069032c1848"), "UTF-8, not UTF-16");
            Assert.That(SecureIds.Ref(null), Is.Empty);
        }

        [TestCase("own-1", true)]
        [TestCase("a.b:c_d-e", true)]
        [TestCase("", false)]
        [TestCase("has space", false)]
        [TestCase("slash/no", false)]
        public void TheIdPatternIsDiscoverysReservationAndTicketPattern(string id, bool valid)
        {
            Assert.That(SecureIds.IsValidId(id), Is.EqualTo(valid));
        }

        [Test]
        public void AnIdOf101CharactersIsRefused()
        {
            Assert.That(SecureIds.IsValidId(new string('a', 100)), Is.True);
            Assert.That(SecureIds.IsValidId(new string('a', 101)), Is.False);
        }

        [Test]
        public void TheStoreKeyNamesTheProfileAndTheApp()
        {
            Assert.That(PlayerTokenStoreKeys.For("p2", "dscp_x"), Is.EqualTo("pingcore.playerToken.p2.dscp_x"));
            Assert.That(PlayerTokenStoreKeys.For(string.Empty, "dscp_x"), Is.EqualTo("pingcore.playerToken..dscp_x"));
            Assert.That(PlayerTokenStoreKeys.IsValidProfile("client-2_b"), Is.True);
            Assert.That(PlayerTokenStoreKeys.IsValidProfile("a.b"), Is.False, "a dot would make keys ambiguous");
            Assert.That(PlayerTokenStoreKeys.IsValidProfile(new string('p', 33)), Is.False);
        }

        [Test]
        public void AStoredTokenNeverPrintsItsToken()
        {
            var stored = new StoredPlayerToken("secret-jwt-value", "anon:1", 1791200000000);
            Assert.That(stored.ToString(), Does.Not.Contain("secret-jwt-value"));
            Assert.That(stored.ToString(), Does.Contain("anon:1"));
        }
    }
}
