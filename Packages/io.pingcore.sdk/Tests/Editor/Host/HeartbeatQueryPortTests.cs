using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core;

namespace PingCore.Discovery.Host.Tests.Editor
{
    /// <summary>
    /// <c>queryPort</c>: game port plus one by default (where the echo responder listens), not sent at all
    /// with <see cref="HeartbeatReporterOptions.OmitQueryPort"/> (so a <c>tcp</c> probe dials the game port),
    /// and one warning when Discovery answers <c>tcp</c> while a <c>queryPort</c> is sent.
    /// </summary>
    public sealed class HeartbeatQueryPortTests
    {
        private static PingCoreHttpResponse TcpOk() => HostResponses.Json(200,
            "{\"error\":false,\"serverId\":\"" + HostResponses.ServerId + "\",\"ip\":\"203.0.113.10\",\"expiresIn\":90,\"verificationMode\":\"tcp\",\"verified\":\"pending\",\"lastProbeError\":null}");

        [Test]
        public async Task OmitQueryPortSendsNoQueryPortSoDiscoveryProbesTheGamePort()
        {
            var h = new ReporterHarness();
            HeartbeatReporter reporter = h.Create(o => o.OmitQueryPort = true);
            HeartbeatStartResult start = await reporter.StartAsync(CancellationToken.None);

            Assert.That(start.Outcome, Is.EqualTo(HeartbeatStartOutcome.Started), start.ToString());
            JObject body = JObject.Parse(h.Transport.Requests.Single().Body);
            // Mutation: always set body.QueryPort and the key is sent (as null or 7778).
            Assert.That(body.Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "name", "port", "players", "maxPlayers" }), body.ToString());
            Assert.That(new HeartbeatReporterOptions { GamePort = 7777, OmitQueryPort = true }.SentQueryPort, Is.Null);
            Assert.That(new HeartbeatReporterOptions { GamePort = 7777 }.SentQueryPort, Is.EqualTo(7778));
            Assert.That(new HeartbeatReporterOptions { GamePort = 7777, OmitQueryPort = true }.EffectiveQueryPort, Is.EqualTo(7778), "the echo port is still game port plus one");
            reporter.Dispose();
        }

        [Test]
        public async Task QueryPortAndOmitQueryPortTogetherAreRefusedBeforeSending()
        {
            var h = new ReporterHarness();
            HeartbeatReporter reporter = h.Create(o => { o.OmitQueryPort = true; o.QueryPort = 9000; });
            HeartbeatStartResult start = await reporter.StartAsync(CancellationToken.None);
            Assert.That(start.Outcome, Is.EqualTo(HeartbeatStartOutcome.Failed));
            Assert.That(start.Call.Message, Does.Contain("not both"));
            Assert.That(h.Transport.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task ATcpAppWithAQueryPortSentWarnsOnceAcrossBeats()
        {
            var h = new ReporterHarness();
            h.Transport.Default = _ => TcpOk();
            HeartbeatReporter reporter = h.Create();
            await reporter.StartAsync(CancellationToken.None);
            await h.UntilWaiting(1);
            h.Scheduler.Advance(h.LastDelay);
            await h.UntilWaiting(2);

            // Mutation: drop the tcpQueryPortWarned latch and this is 2.
            Assert.That(h.Log.Warnings("verifies by tcp"), Is.EqualTo(1), string.Join("\n", h.Log.Entries.Select(e => e.ToString())));
            Assert.That(h.Log.Entries.Single(e => e.Message.Contains("verifies by tcp")).Message, Does.Contain("7778"));
            Assert.That(h.Log.Entries.Any(e => e.ToString().Contains(HostResponses.Token)), Is.False);
            reporter.Dispose();
        }

        [Test]
        public async Task ATcpAppWithoutAQueryPortAndAnUdpEchoAppWithOneDoNotWarn()
        {
            var tcp = new ReporterHarness();
            tcp.Transport.Default = _ => TcpOk();
            HeartbeatReporter omitted = tcp.Create(o => o.OmitQueryPort = true);
            await omitted.StartAsync(CancellationToken.None);
            Assert.That(tcp.Log.Warnings("verifies by tcp"), Is.EqualTo(0), "no queryPort sent: the probe dials the game port");
            omitted.Dispose();

            var echo = new ReporterHarness();
            HeartbeatReporter withEcho = echo.Create();
            await withEcho.StartAsync(CancellationToken.None);
            Assert.That(withEcho.Status.VerificationMode, Is.EqualTo("udp-echo"));
            Assert.That(echo.Log.Warnings("verifies by tcp"), Is.EqualTo(0), "udp-echo probes the queryPort the echo answers on");
            withEcho.Dispose();
        }
    }
}
