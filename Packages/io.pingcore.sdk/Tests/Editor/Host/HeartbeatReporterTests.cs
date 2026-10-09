using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Core.Discovery;

namespace PingCore.Discovery.Host.Tests.Editor
{
    public sealed class HeartbeatReporterTests
    {
        private static JObject Body(PingCoreHttpRequest request) => JObject.Parse(request.Body);

        [Test]
        public async Task TheFirstHeartbeatCarriesTheGameServerAndTheBearerAndStartsTheLoop()
        {
            var h = new ReporterHarness();
            HeartbeatReporter reporter = h.Create(o => { o.Version = "2"; o.Meta = new JObject { ["mode"] = "rush" }; });

            HeartbeatStartResult start = await reporter.StartAsync(CancellationToken.None);

            Assert.That(start.Outcome, Is.EqualTo(HeartbeatStartOutcome.Started), start.ToString());
            Assert.That(reporter.ServerId, Is.EqualTo(HostResponses.ServerId));
            PingCoreHttpRequest first = h.Transport.Requests.Single();
            Assert.That(first.Method, Is.EqualTo("POST"));
            Assert.That(first.Url, Is.EqualTo(HostResponses.BaseUrl + "/v1/heartbeat"));
            Assert.That(first.Headers["Authorization"], Is.EqualTo("Bearer " + HostResponses.Token));
            JObject body = Body(first);
            Assert.That(body.Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "name", "port", "queryPort", "players", "maxPlayers", "version", "meta" }));
            Assert.That((int)body["port"], Is.EqualTo(7777));
            Assert.That((int)body["queryPort"], Is.EqualTo(7778), "queryPort defaults to the game port plus one");
            Assert.That((string)body["meta"]["mode"], Is.EqualTo("rush"));
            Assert.That(reporter.Status.VerificationMode, Is.EqualTo("udp-echo"));
            Assert.That(reporter.Status.Verified, Is.EqualTo("pending"));
            await h.UntilWaiting(1);
            reporter.Dispose();
        }

        [Test]
        public async Task TheLoopBeatsOnTheJitteredCadenceAndSendsTheRecordedServerId()
        {
            var h = new ReporterHarness();
            foreach (double unit in new[] { 0.0, 0.999999, 0.5, 0.25 })
            {
                h.Units.Enqueue(unit);
            }

            HeartbeatReporter reporter = h.Create();
            await reporter.StartAsync(CancellationToken.None);
            await h.UntilWaiting(1);
            for (int sends = 2; sends <= 4; sends++)
            {
                h.Scheduler.Advance(h.LastDelay);
                await h.UntilWaiting(sends);
            }

            double[] delays = h.Scheduler.Delays.Select(d => d.TotalSeconds).ToArray();
            Assert.That(delays.Length, Is.EqualTo(4));
            Assert.That(delays[0], Is.EqualTo(27.0).Within(1e-6));
            Assert.That(delays[1], Is.GreaterThan(32.99).And.LessThan(33.0));
            Assert.That(delays[2], Is.EqualTo(30.0).Within(1e-6));
            Assert.That(delays[3], Is.EqualTo(28.5).Within(1e-6));
            Assert.That(delays, Has.All.InRange(27.0, 33.0));
            foreach (PingCoreHttpRequest beat in h.Transport.Requests.Skip(1))
            {
                Assert.That((string)Body(beat)["serverId"], Is.EqualTo(HostResponses.ServerId), "later beats send the id Discovery recorded");
            }

            Assert.That(h.Beats.Count, Is.EqualTo(4));
            Assert.That(h.Beats.All(b => b.Accepted && !b.Stopped), Is.True);
            reporter.Dispose();
        }

        [TestCase("ip_cap", 10, DiscoveryReason.IpCap)]
        [TestCase("self_hosted_cap", 0, DiscoveryReason.SelfHostedCap)]
        public async Task ARefusedNewGameServerReturnsTheReasonAndLimitAndSendsNothingMore(string reason, int limit, DiscoveryReason expected)
        {
            var h = new ReporterHarness();
            h.Transport.Enqueue(HostResponses.Refusal(reason, limit));
            HeartbeatReporter reporter = h.Create();

            HeartbeatStartResult start = await reporter.StartAsync(CancellationToken.None);

            Assert.That(start.Outcome, Is.EqualTo(HeartbeatStartOutcome.Refused));
            Assert.That(start.Reason, Is.EqualTo(expected));
            Assert.That(start.Limit, Is.EqualTo(limit));
            Assert.That(reporter.Status.Stopped, Is.True);
            h.Scheduler.Advance(TimeSpan.FromMinutes(10));
            await HostWait.Settle();
            Assert.That(h.Transport.Count, Is.EqualTo(1), "nothing is sent after a refusal");
            Assert.That(h.Scheduler.PendingCount, Is.Zero);
            HeartbeatStartResult again = await reporter.StartAsync(CancellationToken.None);
            Assert.That(again.Outcome, Is.EqualTo(HeartbeatStartOutcome.Failed));
            Assert.That(h.Transport.Count, Is.EqualTo(1), "a refused reporter does not start again");
        }

        [Test]
        public async Task ARefusalInTheLoopStopsItAndTheBeatSaysSo()
        {
            var h = new ReporterHarness();
            HeartbeatReporter reporter = h.Create();
            await reporter.StartAsync(CancellationToken.None);
            await h.UntilWaiting(1);
            h.Transport.Enqueue(HostResponses.Refusal("ip_cap", 10));

            h.Scheduler.Advance(h.LastDelay);
            await HostWait.Until(() => h.Beats.Count == 2, "the refused beat");

            HeartbeatResult refused = h.Beats.Last();
            Assert.That(refused.Stopped, Is.True);
            Assert.That(refused.Call.Reason, Is.EqualTo(DiscoveryReason.IpCap));
            Assert.That(refused.Call.Error.Limit, Is.EqualTo(10));
            Assert.That(reporter.Status.Stopped, Is.True);
            h.Scheduler.Advance(TimeSpan.FromMinutes(10));
            await HostWait.Settle();
            Assert.That(h.Transport.Count, Is.EqualTo(2));
            Assert.That(h.Scheduler.PendingCount, Is.Zero);
        }

        [Test]
        public async Task ARateLimitedBeatWaitsRetryAfterAndAChangeDoesNotCutItShort()
        {
            var h = new ReporterHarness();
            HeartbeatReporter reporter = h.Create();
            await reporter.StartAsync(CancellationToken.None);
            await h.UntilWaiting(1);
            h.Transport.Enqueue(HostResponses.RateLimited(42));

            h.Scheduler.Advance(h.LastDelay);
            await h.UntilWaiting(2);
            Assert.That(h.LastDelay, Is.EqualTo(TimeSpan.FromSeconds(42)));

            reporter.SetPlayers(3);
            await HostWait.Until(() => h.Scheduler.Delays.Count == 3 && h.Scheduler.PendingCount == 1, "the wait recomputed after the change");
            Assert.That(h.LastDelay, Is.EqualTo(TimeSpan.FromSeconds(42)), "still the whole Retry-After");
            h.Scheduler.Advance(TimeSpan.FromSeconds(41));
            await HostWait.Settle();
            Assert.That(h.Transport.Count, Is.EqualTo(2), "nothing before Retry-After");
            h.Scheduler.Advance(TimeSpan.FromSeconds(1));
            await h.UntilWaiting(3);
            Assert.That((int)Body(h.Transport.Requests.Last())["players"], Is.EqualTo(3));
            reporter.Dispose();
        }

        [Test]
        public async Task TransientFailuresRetryAtFiveTenAndTwentyWarnOnceAfterTwoMissesAndRecover()
        {
            var h = new ReporterHarness();
            HeartbeatReporter reporter = h.Create();
            await reporter.StartAsync(CancellationToken.None);
            await h.UntilWaiting(1);
            h.Transport.Enqueue(HostResponses.Degraded());
            h.Transport.Enqueue(_ => throw new PingCoreTransportException("connection refused"));
            h.Transport.Enqueue(HostResponses.Json(500, "{\"error\":true,\"message\":\"Failed to record heartbeat.\"}"));
            h.Transport.Enqueue(HostResponses.Degraded());

            int[] expected = { 5, 10, 20, 20 };
            for (int i = 0; i < expected.Length; i++)
            {
                h.Scheduler.Advance(h.LastDelay);
                await h.UntilWaiting(i + 2);
                Assert.That(h.LastDelay, Is.EqualTo(TimeSpan.FromSeconds(expected[i])), "retry " + (i + 1));
                Assert.That(reporter.Status.MissedBeats, Is.EqualTo(i + 1));
                Assert.That(h.Log.Warnings("in a row missed"), Is.EqualTo(i == 0 ? 0 : 1), "the warning comes once, at the second miss");
            }

            h.Scheduler.Advance(h.LastDelay);
            await h.UntilWaiting(6);
            Assert.That(h.LastDelay.TotalSeconds, Is.InRange(27.0, 33.0), "accepted again: back on the cadence");
            Assert.That(reporter.Status.MissedBeats, Is.Zero);
            Assert.That(h.Log.Entries.Count(e => e.Message.Contains("accepted again")), Is.EqualTo(1));
            reporter.Dispose();
        }

        [Test]
        public async Task AChangeIsSentEarlyAtMostOncePerFiveSeconds()
        {
            var h = new ReporterHarness();
            HeartbeatReporter reporter = h.Create();
            await reporter.StartAsync(CancellationToken.None);
            await h.UntilWaiting(1);

            reporter.SetPlayers(2);
            await HostWait.Until(() => h.Scheduler.Delays.Count == 2 && h.Scheduler.PendingCount == 1, "the early wait");
            Assert.That(h.LastDelay, Is.EqualTo(TimeSpan.FromSeconds(5)), "5 s after the first send");
            h.Scheduler.Advance(TimeSpan.FromSeconds(5));
            await h.UntilWaiting(2);
            Assert.That((int)Body(h.Transport.Requests[1])["players"], Is.EqualTo(2));

            reporter.SetPlayers(3);
            reporter.SetPlayers(4);
            await HostWait.Until(() => h.Scheduler.PendingCount == 1 && h.LastDelay == TimeSpan.FromSeconds(5), "the second early wait");
            h.Scheduler.Advance(TimeSpan.FromSeconds(4));
            await HostWait.Settle();
            Assert.That(h.Transport.Count, Is.EqualTo(2), "no second early send within 5 s");
            h.Scheduler.Advance(TimeSpan.FromSeconds(1));
            await h.UntilWaiting(3);
            Assert.That((int)Body(h.Transport.Requests[2])["players"], Is.EqualTo(4), "the latest count");

            reporter.SetPlayers(4);
            await HostWait.Settle();
            Assert.That(h.LastDelay.TotalSeconds, Is.InRange(27.0, 33.0), "an unchanged count schedules nothing early");
            reporter.Dispose();
        }

        [TestCase("9358")]
        [TestCase("1")]
        [TestCase("65535")]
        public async Task StartRefusesWithoutSendingWhenTheLocalSdkEndpointIsPresent(string port)
        {
            var h = new ReporterHarness { AgonesPort = port };
            HeartbeatReporter reporter = h.Create();

            HeartbeatStartResult start = await reporter.StartAsync(CancellationToken.None);

            Assert.That(start.Outcome, Is.EqualTo(HeartbeatStartOutcome.LocalSdkEndpointPresent));
            Assert.That(h.Transport.Count, Is.Zero);
            Assert.That(h.EnvironmentReads.Distinct(), Is.EqualTo(new[] { "AGONES_SDK_HTTP_PORT" }), "the only variable Host reads");
            VerifyResult verify = await reporter.VerifyReservationAsync("rsv-1", "player-1", CancellationToken.None);
            Assert.That(verify.Verdict, Is.EqualTo(VerifyVerdict.Unavailable), "a hosted game server never verifies");
            Assert.That(h.Transport.Count, Is.Zero);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("abc")]
        [TestCase("0")]
        [TestCase("65536")]
        [TestCase(" 9358")]
        public async Task StartHeartbeatsWhenTheVariableNamesNoPort(string value)
        {
            var h = new ReporterHarness { AgonesPort = value };
            HeartbeatReporter reporter = h.Create();

            HeartbeatStartResult start = await reporter.StartAsync(CancellationToken.None);

            Assert.That(start.Outcome, Is.EqualTo(HeartbeatStartOutcome.Started), "control for the refusal test");
            Assert.That(h.Transport.Count, Is.EqualTo(1));
            reporter.Dispose();
        }

        [Test]
        public async Task BadOptionsAreRefusedLocallyAndTheTokenNeverReachesALogOrAResult()
        {
            var cases = new (Action<HeartbeatReporterOptions> configure, string why)[]
            {
                (o => o.Token = "usr" + "_" + "notadiscoverytoken", "a PingCore API key"), // built from fragments: no token-shaped literal in source
                (o => o.Token = null, "no token"),
                (o => o.Name = "  ", "a blank name"),
                (o => o.GamePort = 0, "game port 0"),
                (o => o.GamePort = 65535, "the default query port would be 65536"),
                (o => o.MaxPlayers = -1, "negative max players"),
                (o => o.ServerId = "bad id", "a serverId with a space"),
                (o => o.BaseUrl = "discovery.test", "a relative base URL"),
            };
            foreach (var c in cases)
            {
                var h = new ReporterHarness();
                HeartbeatReporter reporter = h.Create(c.configure);
                HeartbeatStartResult start = await reporter.StartAsync(CancellationToken.None);
                Assert.That(start.Outcome, Is.EqualTo(HeartbeatStartOutcome.Failed), c.why);
                Assert.That(start.Call.IsLocalRefusal, Is.True, c.why);
                Assert.That(h.Transport.Count, Is.Zero, c.why);
            }

            // The token must never be logged, even on the paths that log the most.
            var t = new ReporterHarness();
            t.Transport.Enqueue(HostResponses.Refusal("ip_cap", 10));
            HeartbeatReporter refused = t.Create();
            HeartbeatStartResult r = await refused.StartAsync(CancellationToken.None);
            Assert.That(t.Log.Entries, Is.Not.Empty, "the refusal is logged, so the scan below sees something");
            foreach (HeartbeatLogEntry entry in t.Log.Entries)
            {
                Assert.That(entry.ToString(), Does.Not.Contain(HostResponses.Token));
            }

            Assert.That(r.ToString(), Does.Not.Contain(HostResponses.Token));
            Assert.That(r.Call.ToString() + r.Call.Message, Does.Not.Contain(HostResponses.Token));
        }

        [Test]
        public async Task StopDelistsWithTheColonEncodedServerIdAndEndsTheLoop()
        {
            var h = new ReporterHarness();
            HeartbeatReporter reporter = h.Create();
            await reporter.StartAsync(CancellationToken.None);
            await h.UntilWaiting(1);
            h.Transport.Enqueue(HostResponses.Delisted());

            DiscoveryResult<PingCore.Discovery.Host.Wire.DelistResponse> stop = await reporter.StopAsync(CancellationToken.None);

            Assert.That(stop.IsOk, Is.True, stop.ToString());
            Assert.That(stop.Value.Removed, Is.True);
            PingCoreHttpRequest delete = h.Transport.Requests.Last();
            Assert.That(delete.Method, Is.EqualTo("DELETE"));
            Assert.That(delete.Url, Is.EqualTo(HostResponses.BaseUrl + "/v1/servers/203.0.113.10%3A27015"));
            Assert.That(delete.Headers["Authorization"], Is.EqualTo("Bearer " + HostResponses.Token));
            Assert.That(delete.Body, Is.Null);
            Assert.That(h.Scheduler.PendingCount, Is.Zero, "the loop's wait was cancelled");
            h.Scheduler.Advance(TimeSpan.FromMinutes(5));
            await HostWait.Settle();
            Assert.That(h.Transport.Count, Is.EqualTo(2), "no beat after the stop");
            Assert.That(reporter.Status.Stopped, Is.True);
        }

        [Test]
        public async Task StopBeforeAnAcceptedHeartbeatSendsNothing()
        {
            var h = new ReporterHarness();
            HeartbeatReporter reporter = h.Create();

            DiscoveryResult<PingCore.Discovery.Host.Wire.DelistResponse> stop = await reporter.StopAsync(CancellationToken.None);

            Assert.That(stop.IsLocalRefusal, Is.True);
            Assert.That(h.Transport.Count, Is.Zero);
        }

        [Test]
        public async Task VerifyAlwaysSendsTheOwnServerIdAndThePlayerIdInTheQuery()
        {
            var h = new ReporterHarness();
            HeartbeatReporter reporter = h.Create();
            await reporter.StartAsync(CancellationToken.None);
            h.Transport.Enqueue(HostResponses.Json(200, "{\"error\":false,\"valid\":true}"));

            VerifyResult result = await reporter.VerifyReservationAsync("rsv-1", "anon:player 7", CancellationToken.None);

            PingCoreHttpRequest verify = h.Transport.Requests.Last();
            Assert.That(verify.Method, Is.EqualTo("GET"));
            Assert.That(verify.Url, Is.EqualTo(HostResponses.BaseUrl + "/v1/reservations/verify/rsv-1?serverId=203.0.113.10%3A27015&playerId=anon%3Aplayer%207"));
            Assert.That(verify.Headers["Authorization"], Is.EqualTo("Bearer " + HostResponses.Token));
            Assert.That(result.Verdict, Is.EqualTo(VerifyVerdict.Valid));
            Assert.That(result.Detailed, Is.False);
            reporter.Dispose();
        }

        [Test]
        public async Task VerifyRefusesLocallyBeforeAHeartbeatOrWithoutAPlayerAndARateLimitIsUnavailable()
        {
            var h = new ReporterHarness();
            HeartbeatReporter reporter = h.Create();
            VerifyResult before = await reporter.VerifyReservationAsync("rsv-1", "player-1", CancellationToken.None);
            Assert.That(before.Verdict, Is.EqualTo(VerifyVerdict.Unavailable));
            Assert.That(h.Transport.Count, Is.Zero);

            await reporter.StartAsync(CancellationToken.None);
            foreach (string player in new[] { null, string.Empty, new string('p', 129) })
            {
                VerifyResult noPlayer = await reporter.VerifyReservationAsync("rsv-1", player, CancellationToken.None);
                Assert.That(noPlayer.Verdict, Is.EqualTo(VerifyVerdict.Invalid));
                Assert.That(noPlayer.Call.IsLocalRefusal, Is.True);
            }

            Assert.That(h.Transport.Count, Is.EqualTo(1), "only the heartbeat was sent");
            h.Transport.Enqueue(HostResponses.RateLimited(5));
            VerifyResult limited = await reporter.VerifyReservationAsync("rsv-1", "player-1", CancellationToken.None);
            Assert.That(limited.Verdict, Is.EqualTo(VerifyVerdict.Unavailable));
            Assert.That(limited.Call.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(5)));
            reporter.Dispose();
        }
    }
}
