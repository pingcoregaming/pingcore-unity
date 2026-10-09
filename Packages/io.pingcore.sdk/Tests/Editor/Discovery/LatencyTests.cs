using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Core.Discovery;
using PingCore.Discovery.Client.Wire;
using Step = PingCore.Discovery.Client.Tests.Editor.FakeDiscoveryTransport.Step;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>
    /// The latency rule: median of 5 after a discarded warm-up, sub-millisecond reported as 1,
    /// failures omitted (never 0). The median cases are the reference answers of the Discovery latency
    /// procedure, ported value for value.
    /// </summary>
    public sealed class LatencyTests
    {
        [Test]
        public void TheMedianIsTheMiddleSortedSampleRounded()
        {
            Assert.That(LatencyMath.TryMedian(new[] { 40, 10, 30.6, 50, 20 }, 5, out int odd, out bool clamped), Is.True);
            Assert.That(odd, Is.EqualTo(31));
            Assert.That(clamped, Is.False);

            // Sorted 10, 21, 30, 41: (21 + 30) / 2 = 25.5, rounded half away from zero (Go's math.Round).
            Assert.That(LatencyMath.TryMedian(new double[] { 41, 10, 30, 21 }, 4, out int even, out _), Is.True);
            Assert.That(even, Is.EqualTo(26), "banker's rounding would give 26 here too; 24.5 below pins the rule");
            Assert.That(LatencyMath.TryMedian(new[] { 24.5 }, 1, out int half, out _), Is.True);
            Assert.That(half, Is.EqualTo(25), "half away from zero, not to even");
        }

        [Test]
        public void IncompleteSamplesFailTheLocationSoItIsOmitted()
        {
            Assert.That(LatencyMath.TryMedian(new double[] { 10, 20, 30, 40 }, 5, out _, out _), Is.False, "4 of 5 samples");
            Assert.That(LatencyMath.TryMedian(Array.Empty<double>(), 5, out _, out _), Is.False);
            Assert.That(LatencyMath.TryMedian(null, 5, out _, out _), Is.False);
        }

        [Test]
        public void ASubMillisecondMedianIsReportedAsOneAndClamped()
        {
            Assert.That(LatencyMath.TryMedian(new[] { 0.1, 0.2, 0.3, 0.2, 0.1 }, 5, out int ms, out bool clamped), Is.True);
            Assert.That(ms, Is.EqualTo(1));
            Assert.That(clamped, Is.True);
        }

        [Test]
        public void TheLatencyMapOmitsFailuresAndZeros()
        {
            Dictionary<string, int> map = LatencyMath.Map(new[]
            {
                new LocationLatency("a", 12, false, null),
                new LocationLatency("b", null, false, "timeout"),
                new LocationLatency("c", 0, false, null),
                null,
            });
            Assert.That(map, Is.EqualTo(new Dictionary<string, int> { ["a"] = 12 }));
        }

        [Test]
        public async Task TheProbeDiscardsTheWarmUpTakesFiveSamplesAndNeverReportsZero()
        {
            var scheduler = new TestScheduler();
            var echo = new FakeEchoTransport();
            echo.Behave("wss://ping.test/ams", EchoMode.Instant);
            LatencyResult result = await scheduler.RunAsync(new LatencyProbe(scheduler).MeasureAsync(Locations(("eu-west-ams", "wss://ping.test/ams")), new LatencyProbeOptions { Transport = echo }, CancellationToken.None));

            Assert.That(result.Outcome, Is.EqualTo(DiscoveryOutcome.Ok));
            Assert.That(echo.Pings("wss://ping.test/ams"), Is.EqualTo(6), "one warm-up plus five samples");
            Assert.That(result.Medians["eu-west-ams"], Is.GreaterThanOrEqualTo(1), "never 0");
            LocationLatency detail = result.Detail.Single();
            Assert.That(detail.Failure, Is.Null);
            Assert.That(echo.Disposed("wss://ping.test/ams"), Is.True, "the socket is closed");
        }

        [Test]
        public async Task ADelayedPongIsMeasuredInRealMilliseconds()
        {
            var scheduler = new TestScheduler();
            var echo = new FakeEchoTransport();
            echo.Behave("wss://ping.test/slow", EchoMode.Delayed20Ms);
            // Not driven: no timeout may fire while the real 20 ms waits run.
            LatencyResult result = await new LatencyProbe(scheduler).MeasureAsync(Locations(("slow", "wss://ping.test/slow")), new LatencyProbeOptions { Transport = echo }, CancellationToken.None);
            Assert.That(result.Medians["slow"], Is.GreaterThanOrEqualTo(18), "the stopwatch measured the real wait");
            Assert.That(result.Detail.Single().Clamped, Is.False);
        }

        [Test]
        public async Task FailedTimedOutClosedAndBeaconlessLocationsAreOmittedNeverZero()
        {
            var scheduler = new TestScheduler();
            var echo = new FakeEchoTransport();
            echo.Behave("wss://ping.test/ok", EchoMode.NoiseThenPong);
            echo.Behave("wss://ping.test/refused", EchoMode.ConnectFails);
            echo.Behave("wss://ping.test/silent", EchoMode.NeverAnswers);
            echo.Behave("wss://ping.test/closes", EchoMode.ClosesAfterWarmUp);
            echo.Behave("wss://ping.test/hangs", EchoMode.ConnectHangs);
            IReadOnlyList<Location> locations = Locations(
                ("ok", "wss://ping.test/ok"),
                ("refused", "wss://ping.test/refused"),
                ("silent", "wss://ping.test/silent"),
                ("closes", "wss://ping.test/closes"),
                ("hangs", "wss://ping.test/hangs"),
                ("nobeacon", null));

            LatencyResult result = await scheduler.RunAsync(new LatencyProbe(scheduler).MeasureAsync(locations, new LatencyProbeOptions { Transport = echo, MaxConcurrency = 2 }, CancellationToken.None), idleTicks: 20);

            Assert.That(result.Outcome, Is.EqualTo(DiscoveryOutcome.Ok));
            // Mutation: put a failed location in the map as 0 and this has five keys.
            Assert.That(result.Medians.Keys, Is.EquivalentTo(new[] { "ok" }));
            Assert.That(result.Detail.Select(d => d.Id), Is.EquivalentTo(new[] { "ok", "refused", "silent", "closes", "hangs" }), "no beacon, no entry");
            Assert.That(result.Detail.Where(d => d.Id != "ok").Select(d => d.MedianMs), Has.All.Null);
            Assert.That(result.Detail.Single(d => d.Id == "silent").Failure, Does.Contain("sample timed out after 2000"));
            Assert.That(result.Detail.Single(d => d.Id == "hangs").Failure, Does.Contain("connect timed out after 5000"));
            Assert.That(scheduler.Delays, Does.Contain(TimeSpan.FromSeconds(2)), "sample timeouts run on the scheduler");
            Assert.That(echo.MaxOpen, Is.LessThanOrEqualTo(2), "MaxConcurrency bounds the open sockets");
        }

        [Test]
        public async Task EveryBeaconFailingIsUnreachable()
        {
            var scheduler = new TestScheduler();
            var echo = new FakeEchoTransport();
            echo.Behave("wss://ping.test/refused", EchoMode.ConnectFails);
            LatencyResult result = await scheduler.RunAsync(new LatencyProbe(scheduler).MeasureAsync(Locations(("refused", "wss://ping.test/refused")), new LatencyProbeOptions { Transport = echo }, CancellationToken.None));
            Assert.That(result.Outcome, Is.EqualTo(DiscoveryOutcome.Unreachable));
            Assert.That(result.Medians, Is.Empty);
        }

        [Test]
        public async Task TheClientMeasuresTheLocationsAndCachesASuccessForTenMinutes()
        {
            using (var h = new ClientHarness())
            {
                var echo = new FakeEchoTransport();
                echo.Behave("wss://ping.test/ams", EchoMode.Instant);
                h.Http.On("GET", ClientHarness.LocationsPath, Step.Json(200, "{\"error\":false,\"locations\":[{\"id\":\"eu-west-ams\",\"name\":\"Amsterdam\",\"pingUrl\":\"wss://ping.test/ams\",\"enabled\":true},{\"id\":\"us-east-nyc\",\"name\":\"New York\",\"pingUrl\":null,\"enabled\":true}],\"returned\":2}"));
                var options = new LatencyProbeOptions { Transport = echo };
                LatencyResult first = await h.Scheduler.RunAsync(h.Client.MeasureLatencyAsync(options, CancellationToken.None));
                Assert.That(first.Medians.Keys, Is.EqualTo(new[] { "eu-west-ams" }));
                h.Scheduler.Advance(TimeSpan.FromMinutes(9));
                LatencyResult cached = await h.Client.MeasureLatencyAsync(options, CancellationToken.None);
                Assert.That(cached, Is.SameAs(first));
                Assert.That(echo.Pings("wss://ping.test/ams"), Is.EqualTo(6), "no second measurement inside ten minutes");
            }
        }

        private static IReadOnlyList<Location> Locations(params (string Id, string Url)[] rows)
        {
            return rows.Select(r => new Location { Id = r.Id, Name = r.Id, PingUrl = r.Url, Enabled = true }).ToList();
        }
    }

    internal enum EchoMode
    {
        Instant,
        Delayed20Ms,
        NoiseThenPong,
        ConnectFails,
        ConnectHangs,
        NeverAnswers,
        ClosesAfterWarmUp,
    }

    /// <summary>A scripted beacon per URL; counts pings and open sockets.</summary>
    internal sealed class FakeEchoTransport : IWebSocketEchoTransport
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, EchoMode> modes = new Dictionary<string, EchoMode>();
        private readonly Dictionary<string, int> pings = new Dictionary<string, int>();
        private readonly HashSet<string> disposed = new HashSet<string>();
        private int open;

        public int MaxOpen { get; private set; }

        public void Behave(string url, EchoMode mode) => modes[url] = mode;

        public int Pings(string url)
        {
            lock (gate)
            {
                return pings.TryGetValue(url, out int n) ? n : 0;
            }
        }

        public bool Disposed(string url)
        {
            lock (gate)
            {
                return disposed.Contains(url);
            }
        }

        public async Task<IWebSocketEchoSession> ConnectAsync(string url, CancellationToken cancellationToken)
        {
            EchoMode mode = modes[url];
            if (mode == EchoMode.ConnectFails)
            {
                throw new InvalidOperationException("connection refused");
            }

            if (mode == EchoMode.ConnectHangs)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            lock (gate)
            {
                open++;
                MaxOpen = Math.Max(MaxOpen, open);
            }

            return new Session(this, url, mode);
        }

        private void Ping(string url)
        {
            lock (gate)
            {
                pings[url] = (pings.TryGetValue(url, out int n) ? n : 0) + 1;
            }
        }

        private void Close(string url)
        {
            lock (gate)
            {
                open--;
                disposed.Add(url);
            }
        }

        private sealed class Session : IWebSocketEchoSession
        {
            private readonly FakeEchoTransport owner;
            private readonly string url;
            private readonly EchoMode mode;
            private readonly Queue<string> replies = new Queue<string>();
            private TaskCompletionSource<EchoMessage> waiting;
            private int sent;

            public Session(FakeEchoTransport owner, string url, EchoMode mode)
            {
                this.owner = owner;
                this.url = url;
                this.mode = mode;
            }

            public Task SendTextAsync(string text, CancellationToken cancellationToken)
            {
                Assert.That(text, Is.EqualTo("ping"));
                owner.Ping(url);
                sent++;
                if (mode == EchoMode.NoiseThenPong)
                {
                    replies.Enqueue("hello");
                }

                replies.Enqueue("pong");
                if (waiting != null)
                {
                    // Completes the receive the probe started before sending, inline, as a socket
                    // thread would: no hop through the main thread.
                    TaskCompletionSource<EchoMessage> receive = waiting;
                    waiting = null;
                    receive.TrySetResult(new EchoMessage(replies.Dequeue(), Stopwatch.GetTimestamp()));
                }

                return Task.CompletedTask;
            }

            /// <summary>The probe starts this before it sends; the send completes it.</summary>
            public Task<EchoMessage> ReceiveTextAsync(CancellationToken cancellationToken)
            {
                switch (mode)
                {
                    case EchoMode.NeverAnswers:
                        return NeverAsync(cancellationToken);
                    case EchoMode.ClosesAfterWarmUp when sent >= 1:
                        return Task.FromException<EchoMessage>(new System.Net.WebSockets.WebSocketException("the beacon closed the connection"));
                    case EchoMode.Delayed20Ms:
                        return DelayedAsync(cancellationToken);
                }

                if (replies.Count > 0)
                {
                    return Task.FromResult(new EchoMessage(replies.Dequeue(), Stopwatch.GetTimestamp()));
                }

                waiting = new TaskCompletionSource<EchoMessage>();
                return waiting.Task;
            }

            private static async Task<EchoMessage> NeverAsync(CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return default;
            }

            /// <summary>Started before the send, so the 20 ms covers the measured interval.</summary>
            private async Task<EchoMessage> DelayedAsync(CancellationToken cancellationToken)
            {
                await Task.Delay(20, cancellationToken);
                return new EchoMessage(replies.Dequeue(), Stopwatch.GetTimestamp());
            }

            public void Dispose() => owner.Close(url);
        }
    }
}
