using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>The whole shim against <see cref="FakeLocalSdkEndpoint"/>: allocations arriving, ending and clearing, and the ending mark.</summary>
    public sealed class FleetShimAllocationTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        [Test]
        public async Task AnAllocationArrivesOnceAndEndingItReturnsToReadyAsEndedByTheGame()
        {
            using (var h = new FleetShimHarness())
            {
                await h.Sdk.StartAsync(None);
                await h.Sdk.ReadyAsync(None);
                h.Fake.Allocate("alloc-1", "{\"mode\":\"duel\",\"tuning\":{\"lobbySeconds\":10}}");
                await Wait.Until(() => h.ReceivedCount == 1, "the allocation");
                Assert.That(h.Sdk.State, Is.EqualTo(FleetState.InSession));
                AllocationInfo info = h.Sdk.CurrentAllocation;
                Assert.That(info.AllocationId, Is.EqualTo("alloc-1"));
                Assert.That((string)info.Context["mode"], Is.EqualTo("duel"));
                Assert.That((int)info.Context["tuning"]["lobbySeconds"], Is.EqualTo(10));

                // A reconnect replays the view; the allocation is not raised again.
                int watches = h.Fake.WatchRequests;
                h.Fake.DropWatchOnly();
                await Wait.Until(() => h.Fake.WatchRequests > watches && h.Fake.OpenWatches == 1, "the watch to reconnect");
                await Wait.For(100);
                Assert.That(h.ReceivedCount, Is.EqualTo(1));
                Assert.That(h.Sdk.State, Is.EqualTo(FleetState.InSession));

                FleetCallResult end = await h.Sdk.EndSessionAsync("alloc-1", None);
                Assert.That(end.IsOk, Is.True);
                await Wait.Until(() => h.ClearedCount == 1, "the allocation to clear");
                Assert.That(h.Cleared[0].Reason, Is.EqualTo(AllocationClearedReason.EndedByGame));
                Assert.That(h.Sdk.State, Is.EqualTo(FleetState.Ready));
                Assert.That(h.Sdk.CurrentAllocation, Is.Null);
            }
        }

        [Test]
        public async Task TheClearingFrameBeforeTheEndedAnswerIsStillEndedByTheGame()
        {
            // Mutation note: moving allocations.MarkEnding in FleetSdk.EndSessionAsync after the await of the
            // /ended call fails this test. The frame is handled 250 ms before the answer arrives, so the tracker
            // would see the allocation leave with no mark and report ClearedByPlatform.
            using (var h = new FleetShimHarness())
            {
                h.Fake.FrameBeforeAnswer = true;
                h.Fake.AnswerDelayAfterFrame = TimeSpan.FromMilliseconds(250);
                await h.Sdk.StartAsync(None);
                await h.Sdk.ReadyAsync(None);
                h.Fake.Allocate("alloc-f", "{}");
                await Wait.Until(() => h.ReceivedCount == 1, "the allocation");

                FleetCallResult end = await h.Sdk.EndSessionAsync("alloc-f", None);
                Assert.That(end.IsOk, Is.True, end.Message);
                Assert.That(h.ClearedCount, Is.EqualTo(1), "the clearing frame was handled before the answer returned");
                Assert.That(h.Cleared[0].Reason, Is.EqualTo(AllocationClearedReason.EndedByGame));
                Assert.That(h.Sdk.State, Is.EqualTo(FleetState.Ready));
            }
        }

        [Test]
        public async Task AnEndCallWhoseAnswerIsLostKeepsTheMarkSoTheLaterFrameIsEndedByTheGame()
        {
            using (var h = new FleetShimHarness())
            {
                await h.Sdk.StartAsync(None);
                await h.Sdk.ReadyAsync(None);
                h.Fake.Allocate("alloc-l", "{}");
                await Wait.Until(() => h.ReceivedCount == 1, "the allocation");

                // The supervisor ends the session but the connection resets before the answer, and the frame comes later.
                h.Fake.AbortAnswerNext("/v1/sessions/alloc-l/ended");
                FleetCallResult end = await h.Sdk.EndSessionAsync("alloc-l", None);
                Assert.That(end.IsOk, Is.False, "the answer was lost");
                Assert.That(end.Status, Is.EqualTo(0));
                Assert.That(end.Outcome, Is.EqualTo(FleetCallOutcome.Unreachable),
                    "a reset after the endpoint answered, outside a stop, is not EndpointClosed: " + end.Message);
                Assert.That(h.Logs.For("sessionEnded").Last().Level, Is.EqualTo(FleetLogLevel.Error), h.Logs.Dump());
                Assert.That(h.ClearedCount, Is.EqualTo(0), "no frame yet");

                h.Fake.PushView();
                await Wait.Until(() => h.ClearedCount == 1, "the late clearing frame");
                Assert.That(h.Cleared[0].Reason, Is.EqualTo(AllocationClearedReason.EndedByGame), "a transport failure does not drop the mark");
            }
        }

        [Test]
        public async Task ARefusedEndCallDropsTheMarkSoALaterClearIsThePlatforms()
        {
            using (var h = new FleetShimHarness())
            {
                await h.Sdk.StartAsync(None);
                await h.Sdk.ReadyAsync(None);
                h.Fake.Allocate("alloc-r", "{}");
                await Wait.Until(() => h.ReceivedCount == 1, "the allocation");

                h.Fake.FailNext("/v1/sessions/alloc-r/ended", 409, "{\"message\":\"not this session\"}");
                FleetCallResult end = await h.Sdk.EndSessionAsync("alloc-r", None);
                Assert.That(end.Outcome, Is.EqualTo(FleetCallOutcome.Rejected));

                h.Fake.ClearAllocation();
                await Wait.Until(() => h.ClearedCount == 1, "the clear");
                Assert.That(h.Cleared[0].Reason, Is.EqualTo(AllocationClearedReason.ClearedByPlatform));
            }
        }

        [Test]
        public async Task AnAllocationClearedWithoutAnEndCallIsClearedByThePlatform()
        {
            using (var h = new FleetShimHarness())
            {
                await h.Sdk.StartAsync(None);
                await h.Sdk.ReadyAsync(None);
                h.Fake.Allocate("alloc-2", "{}");
                await Wait.Until(() => h.ReceivedCount == 1, "the allocation");
                h.Fake.ClearAllocation();
                await Wait.Until(() => h.ClearedCount == 1, "the clear");
                Assert.That(h.Cleared[0].Reason, Is.EqualTo(AllocationClearedReason.ClearedByPlatform));
                Assert.That(h.Sdk.State, Is.EqualTo(FleetState.Ready));
            }
        }
    }
}
