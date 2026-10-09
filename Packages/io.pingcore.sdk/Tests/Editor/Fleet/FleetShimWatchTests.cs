using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>The whole shim against <see cref="FakeLocalSdkEndpoint"/>: the watch stream's reconnects, backoff and bad lines.</summary>
    public sealed class FleetShimWatchTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        [Test]
        public async Task ThreeFailedReconnectsMakeTheShimUnreachableAndTheFirstFrameAfterReconnectResyncs()
        {
            using (var h = new FleetShimHarness())
            {
                await h.Sdk.StartAsync(None);
                await h.Sdk.ReadyAsync(None);

                // The first frame READ, not only the stream opened: a stream dropped before its first frame
                // counts as a failed reconnect, so Unreachable would come one refused reconnect early.
                await Wait.Within(h.ViewsApplied.ReachedAsync(2), "first watch frame");

                h.Fake.RefuseWatch = true;
                h.Fake.DropWatchOnly();
                await Wait.Until(() => h.Sdk.State == FleetState.Unreachable, "Unreachable after three failed reconnects");
                Assert.That(h.Fake.WatchRequests, Is.GreaterThanOrEqualTo(4), "the first watch plus three refused reconnects");
                Assert.That(h.Scheduler.Delays.Take(3), Is.EqualTo(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }), "1 s doubling");

                // Allocated while the watch was down: the first frame after the reconnect says so.
                h.Fake.Allocate("alloc-3", "{}");
                h.Fake.RefuseWatch = false;
                await Wait.Until(() => h.Sdk.State == FleetState.InSession, "re-synchronised from the first frame");
                Assert.That(h.ReceivedCount, Is.EqualTo(1));
                Assert.That(h.States.Select(s => s.To), Is.EqualTo(new[] { FleetState.NotReady, FleetState.Ready, FleetState.Unreachable, FleetState.InSession }));
            }
        }

        [Test]
        public async Task TheReconnectBackoffDoublesToTenSecondsAndResetsAfterAFrame()
        {
            // Step by step, with no real time: every reconnect delay is held on the virtual clock until the
            // test releases it, and each step awaits an event (the shim applying a watch frame, the scheduler
            // being asked for a delay, the shim's log). Wait.Within is only a guard against a hang.
            using (var h = new FleetShimHarness(holdDelays: true))
            {
                await h.Sdk.StartAsync(None);

                // The shim must have READ the first frame, not only had the stream opened: a stream dropped
                // before its first frame never counts as connected, so the later reconnect would log nothing.
                await Wait.Within(h.ViewsApplied.ReachedAsync(2), "first watch frame");
                h.Fake.RefuseWatch = true;
                h.Fake.DropWatchOnly();

                var backoff = new List<TimeSpan>();
                HeldDelay parked = null;
                for (int i = 1; i <= 6; i++)
                {
                    parked?.Release();
                    parked = await Wait.Within(h.Scheduler.NextHeldDelayAsync(), "reconnect delay " + i);
                    backoff.Add(parked.Delay);
                }

                Assert.That(backoff, Is.EqualTo(new[] { 1, 2, 4, 8, 10, 10 }.Select(s => TimeSpan.FromSeconds(s))));
                Assert.That(h.Fake.WatchRequests, Is.EqualTo(6), "the first watch plus five refused reconnects; the sixth waits on its delay");
                Assert.That(h.Sdk.State, Is.EqualTo(FleetState.NotReady), "NotReady does not become Unreachable");

                // Let the parked reconnect through and wait until the shim has read its first frame (the log
                // comes after the backoff reset); a drop before that frame would leave the backoff at 10 s.
                Task<FleetLogEntry> reconnected = h.Logs.WaitForAsync(e => e.Call == "watch" && e.Message.StartsWith("watch stream reconnected", StringComparison.Ordinal));
                h.Fake.RefuseWatch = false;
                parked.Release();
                await Wait.Within(reconnected, "reconnect and its first frame");

                h.Fake.DropWatchOnly();
                HeldDelay afterFrame = await Wait.Within(h.Scheduler.NextHeldDelayAsync(), "delay after the dropped stream");
                Assert.That(afterFrame.Delay, Is.EqualTo(TimeSpan.FromSeconds(1)), "reset after a frame");
                Assert.That(h.Scheduler.Delays.Count, Is.EqualTo(7), "every delay was the watch's");
            }
        }

        [Test]
        public async Task ABadWatchLineIsSkippedAndAnOverLongLineReconnects()
        {
            using (var h = new FleetShimHarness(maxLineBytes: 64 * 1024))
            {
                await h.Sdk.StartAsync(None);
                await h.Sdk.ReadyAsync(None);
                await Wait.Until(() => h.Fake.OpenWatches == 1, "the watch");
                h.Fake.InjectWatchLine("{not json");
                await Wait.Until(() => h.Logs.For("watch").Any(e => e.Message.Contains("undecodable")), "the bad line is logged");
                h.Fake.Allocate("alloc-4", "{}");
                await Wait.Until(() => h.ReceivedCount == 1, "the stream continues after a bad line");

                int watches = h.Fake.WatchRequests;
                h.Fake.InjectWatchLine(new string('x', 64 * 1024 + 1));
                await Wait.Until(() => h.Fake.WatchRequests > watches, "an over-long line ends the stream and the shim reconnects");
                Assert.That(h.Logs.For("watch").Any(e => e.Message.Contains("exceeded")), Is.True, h.Logs.Dump());
                Assert.That(h.ReceivedCount, Is.EqualTo(1));
            }
        }
    }
}
