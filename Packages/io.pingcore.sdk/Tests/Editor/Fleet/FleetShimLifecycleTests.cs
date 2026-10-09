using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>
    /// The whole shim (real loopback transport, real watch reader) against <see cref="FakeLocalSdkEndpoint"/>,
    /// on a virtual clock: creation, start, ready and the health pings, shutdown.
    /// </summary>
    public sealed class FleetShimLifecycleTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        [Test]
        public async Task AnUnsetOrInvalidPortGivesAnInertShimThatSendsNothing()
        {
            foreach (string raw in new[] { null, string.Empty, "0", "65536", "abc", " 9358" })
            {
                var forbidden = new ForbiddenTransport();
                var logs = new LogCollector();
                FleetSdk sdk = FleetSdk.Create(new FleetSdkOptions { GetEnvironmentVariable = _ => raw, Transport = forbidden, LineStream = forbidden, Scheduler = new TestScheduler(), Log = logs.Add });
                string label = raw ?? "null";
                Assert.That(sdk.IsHosted, Is.False, label);
                Assert.That(sdk.State, Is.EqualTo(FleetState.Inert), label);
                Assert.That(await sdk.StartAsync(None), Is.False, label);
                Assert.That((await sdk.ReadyAsync(None)).Outcome, Is.EqualTo(FleetCallOutcome.Inert), label);
                Assert.That((await sdk.SetCounterAsync("players", 1, None)).Outcome, Is.EqualTo(FleetCallOutcome.Inert), label);
                Assert.That((await sdk.GetCounterAsync("players", None)).Outcome, Is.EqualTo(FleetCallOutcome.Inert), label);
                Assert.That((await sdk.EndSessionAsync("a1", None)).Outcome, Is.EqualTo(FleetCallOutcome.Inert), label);
                Assert.That((await sdk.PublishJoinableAsync("a1", new JoinableSessionRequest { OpenSeats = 1 }, None)).Outcome, Is.EqualTo(FleetCallOutcome.Inert), label);
                Assert.That((await sdk.WithdrawJoinableAsync("a1", None)).Outcome, Is.EqualTo(FleetCallOutcome.Inert), label);
                Assert.That((await sdk.GetBackfillsAsync(None)).Outcome, Is.EqualTo(FleetCallOutcome.Inert), label);
                Assert.That((await sdk.GetReservationAsync("r1", None)).Status, Is.EqualTo(ReservationLookupStatus.Inert), label);
                Assert.That((await sdk.ListReservationsAsync(None)).Outcome, Is.EqualTo(FleetCallOutcome.Inert), label);
                Assert.That((await sdk.ShutdownAsync(None)).Outcome, Is.EqualTo(FleetCallOutcome.Inert), label);
                sdk.NotifyProcessStopping();
                Assert.That(sdk.State, Is.EqualTo(FleetState.Inert), label);
                Assert.That(forbidden.Calls, Is.EqualTo(0), label);
                Assert.That(logs.Entries.Count(e => e.Level == FleetLogLevel.Warning), Is.EqualTo(string.IsNullOrEmpty(raw) ? 0 : 1), label + ": only a set-but-invalid port warns");
                sdk.Dispose();
            }

            // The same seam with a valid port is hosted, so the check above can fail.
            FleetSdk hosted = FleetSdk.Create(new FleetSdkOptions { GetEnvironmentVariable = _ => "9358", Transport = new ForbiddenTransport(), LineStream = new ForbiddenTransport(), Scheduler = new TestScheduler(), Log = _ => { } });
            Assert.That(hosted.IsHosted, Is.True);
            Assert.That(hosted.State, Is.EqualTo(FleetState.Starting));
            hosted.Dispose();
        }

        [Test]
        public async Task StartReadsTheViewAndOpensTheWatchWithoutIntegratingAndTheFirstWriteIntegrates()
        {
            using (var h = new FleetShimHarness())
            {
                Assert.That(await h.Sdk.StartAsync(None), Is.True, h.Logs.Dump());
                Assert.That(h.Sdk.State, Is.EqualTo(FleetState.NotReady));
                Assert.That(h.Sdk.Current.Name, Is.EqualTo("gameserver-42"));
                Assert.That(h.Sdk.Current.Counters["players"].Capacity, Is.EqualTo(8));
                await Wait.Until(() => h.Fake.OpenWatches == 1, "the watch stream to open");
                Assert.That(h.Fake.Requests.All(r => !r.IsWrite), Is.True, "start sends GETs only: " + string.Join(", ", h.Fake.Requests));
                Assert.That(h.Fake.Integrated, Is.False);
                Assert.That(await h.Sdk.StartAsync(None), Is.True, "a second start is a no-op");
                Assert.That(h.Fake.Count("GET", "/gameserver"), Is.EqualTo(1));

                // Integrate before listening (Beacon Rush's default boot order): the counter write integrates, so the fake can flag it.
                CounterResult counter = await h.Sdk.SetCounterAsync("players", 0, None);
                Assert.That(counter.Outcome, Is.EqualTo(FleetCallOutcome.Ok));
                Assert.That(h.Fake.Integrated, Is.True);
                Assert.That(h.Sdk.State, Is.EqualTo(FleetState.NotReady), "integrating is not Ready");
            }
        }

        [Test]
        public async Task ReadyMovesToReadyAndStartsHealthPingsOnlyAfterIt()
        {
            using (var h = new FleetShimHarness(TimeSpan.FromSeconds(2)))
            {
                await h.Sdk.StartAsync(None);
                await Wait.For(100);
                Assert.That(h.Fake.Count("POST", "/health"), Is.EqualTo(0), "no ping before Ready: a ping integrates");

                FleetCallResult ready = await h.Sdk.ReadyAsync(None);
                Assert.That(ready.IsOk, Is.True);
                Assert.That(h.Sdk.State, Is.EqualTo(FleetState.Ready));
                Assert.That(h.Fake.Ready, Is.True);
                await Wait.Until(() => h.Fake.Count("POST", "/health") >= 3, "three health pings");
                int readyIndex = h.Fake.Requests.FindIndex(r => r.Path == "/ready");
                int firstPing = h.Fake.Requests.FindIndex(r => r.Path == "/health");
                Assert.That(firstPing, Is.GreaterThan(readyIndex));
                Assert.That(h.Scheduler.Delays, Has.Some.EqualTo(TimeSpan.FromSeconds(2)), "pings wait the health interval");
                Assert.That(h.Logs.For("health").Count(e => e.Outcome == FleetCallOutcome.Ok), Is.EqualTo(1), "only the first success is logged until the 30th: " + h.Logs.Dump());

                h.Fake.FailNext("/health", 500, "{\"error\":\"internal_error\"}");
                await Wait.Until(() => h.Logs.For("health").Any(e => e.Outcome == FleetCallOutcome.Rejected), "a failed ping is logged");
                Assert.That(h.Logs.For("health").Single(e => e.Outcome == FleetCallOutcome.Rejected).Level, Is.EqualTo(FleetLogLevel.Warning));
            }
        }

        [Test]
        public async Task TheReadyFrameBeforeTheAnswerLandsInReadyOnceWithNoOtherTransition()
        {
            using (var h = new FleetShimHarness(TimeSpan.FromSeconds(2)))
            {
                // The supervisor's order (it emits the frame inside the route, before the answer),
                // held apart so the frame is surely handled first.
                h.Fake.FrameBeforeAnswer = true;
                h.Fake.AnswerDelayAfterFrame = TimeSpan.FromMilliseconds(250);
                await h.Sdk.StartAsync(None);
                await Wait.Until(() => h.Fake.OpenWatches == 1, "the watch");

                FleetCallResult ready = await h.Sdk.ReadyAsync(None);
                Assert.That(ready.IsOk, Is.True, ready.Message);
                Assert.That(h.Sdk.State, Is.EqualTo(FleetState.Ready));
                Assert.That(h.States.Select(s => s.To + "/" + s.Cause), Is.EqualTo(new[] { "NotReady/start", "Ready/watch" }),
                    "the frame moved it to Ready before the answer arrived, and the answer added no transition");

                await Wait.Until(() => h.Fake.Count("POST", "/health") >= 1, "health pings start after the answer");
                await Wait.For(100);
                Assert.That(h.States.Count, Is.EqualTo(2), "still no other transition: " + string.Join(", ", h.States.Select(s => s.To)));
            }
        }

        [Test]
        public async Task ShutdownMovesToShuttingDownAndStopsThePings()
        {
            using (var h = new FleetShimHarness(TimeSpan.FromSeconds(2)))
            {
                await h.Sdk.StartAsync(None);
                await h.Sdk.ReadyAsync(None);
                await Wait.Until(() => h.Fake.Count("POST", "/health") >= 1, "a ping");

                FleetCallResult shutdown = await h.Sdk.ShutdownAsync(None);
                Assert.That(shutdown.IsOk, Is.True);
                Assert.That(h.Sdk.State, Is.EqualTo(FleetState.ShuttingDown));
                Assert.That(h.Fake.ShutdownRequested, Is.True);
                await Wait.Until(() => h.Sdk.Current?.AgonesState == "Shutdown", "the Shutdown frame");
                Assert.That(h.Sdk.State, Is.EqualTo(FleetState.ShuttingDown), "the frame keeps it");
                await Wait.For(100);
                int pings = h.Fake.Count("POST", "/health");
                await Wait.For(200);
                Assert.That(h.Fake.Count("POST", "/health"), Is.EqualTo(pings), "no ping after shutdown");
            }
        }

        [Test]
        public async Task StartGivesUpAfterThirtyFailedReadsAndCanBeRetried()
        {
            var scheduler = new TestScheduler();
            var logs = new LogCollector();
            var transport = new RefusingTransport();
            FleetSdk sdk = FleetSdk.Create(new FleetSdkOptions { GetEnvironmentVariable = _ => "9358", Transport = transport, LineStream = transport, Scheduler = scheduler, Log = logs.Add });
            try
            {
                Assert.That(await sdk.StartAsync(None), Is.False);
                Assert.That(sdk.State, Is.EqualTo(FleetState.Unreachable));
                Assert.That(transport.Calls, Is.EqualTo(30));
                Assert.That(scheduler.Delays, Is.EqualTo(Enumerable.Repeat(TimeSpan.FromSeconds(1), 29)), "30 reads, one second apart");
                Assert.That(logs.Entries.Count(e => e.Level == FleetLogLevel.Error && e.Call == "gameserver"), Is.EqualTo(1));
                Assert.That(logs.Entries.Count(e => e.Call == "gameserver" && e.Level == FleetLogLevel.Warning), Is.EqualTo(4), "attempts 1, 10, 20 and 30 are logged, not every one");

                Assert.That(await sdk.StartAsync(None), Is.False, "a failed start may be retried");
                Assert.That(transport.Calls, Is.EqualTo(60));
            }
            finally
            {
                sdk.Dispose();
            }
        }
    }
}
