using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>
    /// When a failed call is <see cref="FleetCallOutcome.EndpointClosed"/> (info, expected during a stop) and when it
    /// is <see cref="FleetCallOutcome.Unreachable"/> (an error). The fake's <see cref="FakeLocalSdkEndpoint.CloseEndpoint"/>
    /// is Node's <c>server.close()</c>: new connections are refused and the watch stays open, as on a real container stop.
    /// </summary>
    public sealed class FleetShimEndpointClosedTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        [Test]
        public async Task AfterTheProcessIsStoppingARefusedCallIsEndpointClosedAtInfo()
        {
            using (var h = new FleetShimHarness(TimeSpan.FromSeconds(2)))
            {
                await h.Sdk.StartAsync(None);
                await h.Sdk.ReadyAsync(None);
                h.Sdk.NotifyProcessStopping();
                Assert.That(h.Sdk.State, Is.EqualTo(FleetState.Stopping));
                h.Fake.CloseEndpoint();

                CounterResult refused = await h.Sdk.SetCounterAsync("players", 0, None);
                Assert.That(refused.Outcome, Is.EqualTo(FleetCallOutcome.EndpointClosed), refused.Message);
                Assert.That(h.Logs.For("counter").Single().Level, Is.EqualTo(FleetLogLevel.Info));
                Assert.That(h.Logs.Entries.Where(e => e.Level == FleetLogLevel.Error), Is.Empty, h.Logs.Dump());
                h.Sdk.NotifyProcessStopping();
                Assert.That(h.States.Count(s => s.To == FleetState.Stopping), Is.EqualTo(1), "idempotent");
                Assert.That(await h.Sdk.StartAsync(None), Is.False, "nothing starts once the process is stopping");
            }
        }

        [Test]
        public async Task ARefusedCallAfterTheEndpointClosesIsEndpointClosedWhileTheWatchStaysOpen()
        {
            using (var h = new FleetShimHarness())
            {
                await h.Sdk.StartAsync(None);
                await h.Sdk.ReadyAsync(None);
                await Wait.Until(() => h.Fake.OpenWatches == 1, "the watch");

                // The production stop before SIGTERM reaches the game: refused, the watch still open, no NotifyProcessStopping.
                h.Fake.CloseEndpoint();
                CounterResult soon = await h.Sdk.SetCounterAsync("players", 1, None);
                Assert.That(soon.Outcome, Is.EqualTo(FleetCallOutcome.EndpointClosed), soon.Message);
                Assert.That(h.Fake.OpenWatches, Is.EqualTo(1), "the watch stream is still open");
                Assert.That(h.Logs.For("watch").Any(e => e.Message.StartsWith("watch stream closed", StringComparison.Ordinal)), Is.False, "the watch never closed: " + h.Logs.Dump());

                // Not only the fake's bookkeeping: a frame pushed after the close still reaches the shim.
                h.Fake.Allocate("alloc-after-close", "{}");
                await Wait.Until(() => h.ReceivedCount == 1, "an allocation on the watch that stayed open");
                Assert.That(h.Received[0].AllocationId, Is.EqualTo("alloc-after-close"));

                // However long it takes, and whatever the watch does afterwards.
                h.Scheduler.Advance(TimeSpan.FromMinutes(5));
                CounterResult later = await h.Sdk.SetCounterAsync("players", 1, None);
                Assert.That(later.Outcome, Is.EqualTo(FleetCallOutcome.EndpointClosed), later.Message);
                h.Fake.DropWatchOnly();
                await Wait.Until(() => h.Logs.For("watch").Any(e => e.Message.StartsWith("watch reconnect", StringComparison.Ordinal)), "a refused reconnect");
                h.Scheduler.Advance(TimeSpan.FromMinutes(5));
                Assert.That((await h.Sdk.SetCounterAsync("players", 1, None)).Outcome, Is.EqualTo(FleetCallOutcome.EndpointClosed));

                Assert.That(h.Logs.For("counter").All(e => e.Level == FleetLogLevel.Info), Is.True, h.Logs.Dump());
                Assert.That(h.Logs.Entries.Where(e => e.Level == FleetLogLevel.Error), Is.Empty, h.Logs.Dump());
            }
        }

        [Test]
        public async Task ARefusedCallToAnEndpointThatNeverAnsweredIsUnreachable()
        {
            using (var h = new FleetShimHarness())
            {
                // Nothing has answered yet: refused from the first call is a missing endpoint, not a closing one.
                h.Fake.CloseEndpoint();
                CounterResult refused = await h.Sdk.SetCounterAsync("players", 0, None);
                Assert.That(refused.Outcome, Is.EqualTo(FleetCallOutcome.Unreachable), refused.Message);
                Assert.That(h.Logs.For("counter").Single().Level, Is.EqualTo(FleetLogLevel.Error));
            }
        }

        [Test]
        public async Task AnyFailureWithinFiveSecondsAfterTheWatchClosedIsEndpointClosedAndLaterATimeoutIsUnreachable()
        {
            var scheduler = new TestScheduler();
            var logs = new LogCollector();
            var transport = new ScriptedTransport();
            FleetSdk sdk = FleetSdk.Create(new FleetSdkOptions
            {
                GetEnvironmentVariable = name => name == FleetSdk.PortVariable ? 9358.ToString(CultureInfo.InvariantCulture) : null,
                Transport = transport,
                LineStream = transport,
                Scheduler = scheduler,
                Log = logs.Add,
                HealthInterval = TimeSpan.Zero,
            });
            try
            {
                Assert.That(await sdk.StartAsync(None), Is.True, logs.Dump());

                // The scripted watch sends one frame and ends cleanly; its reconnect then hangs, so the clock moves only by the 1 s backoff.
                await Wait.Until(() => transport.Watches == 2, "the watch to close and reconnect");
                CounterResult soon = await sdk.SetCounterAsync("players", 1, None);
                Assert.That(soon.Outcome, Is.EqualTo(FleetCallOutcome.EndpointClosed), "a timeout inside the window: " + soon.Message);

                scheduler.Advance(TimeSpan.FromSeconds(6));
                CounterResult later = await sdk.SetCounterAsync("players", 1, None);
                Assert.That(later.Outcome, Is.EqualTo(FleetCallOutcome.Unreachable), "outside the window a timeout is an error even though the endpoint answered before");
                Assert.That(logs.For("counter").Last().Level, Is.EqualTo(FleetLogLevel.Error));
            }
            finally
            {
                sdk.Dispose();
            }
        }
    }
}
