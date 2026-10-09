using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Fleet.Sessions;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>
    /// <see cref="IFleetSdk.AllocateSelfAsync"/> against <see cref="FakeLocalSdkEndpoint"/> through the real shim: the
    /// <c>POST /allocate</c>, the frame that confirms it, the local refusal while an allocation is current (the
    /// supervisor would replace it), one request for concurrent callers, every way it can fail, the hold-back after an
    /// unconfirmed request, and the warnings for an allocation replaced in either direction.
    /// </summary>
    public sealed class FleetShimSelfAllocationTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        private static async Task<FleetShimHarness> ReadyHarness()
        {
            var h = new FleetShimHarness();
            await h.Sdk.StartAsync(None);
            await h.Sdk.ReadyAsync(None);
            return h;
        }

        [Test]
        public async Task AnIdleGameServerSelfAllocatesAndTheWatchFrameMovesTheShimToInSession()
        {
            using (FleetShimHarness h = await ReadyHarness())
            {
                SelfAllocationResult result = await h.Sdk.AllocateSelfAsync(None);
                Assert.That(result.Outcome, Is.EqualTo(FleetCallOutcome.Ok), result.Message);
                Assert.That(result.Status, Is.EqualTo(200));
                Assert.That(result.IsConfirmed, Is.True);
                Assert.That(result.AlreadyAllocated, Is.False);
                Assert.That(result.Allocation.IsSelfAllocated, Is.True);
                Assert.That(result.Allocation.AllocationId, Does.StartWith("self-"));
                Assert.That(long.TryParse(result.Allocation.AllocationId.Substring(5), NumberStyles.None, CultureInfo.InvariantCulture, out _), Is.True,
                    "the supervisor's clock in milliseconds");
                Assert.That(result.Allocation.Context.Count, Is.EqualTo(0), "a self-allocation carries an empty context");
                Assert.That(h.Sdk.State, Is.EqualTo(FleetState.InSession));
                Assert.That(h.Sdk.CurrentAllocation.AllocationId, Is.EqualTo(result.Allocation.AllocationId));
                Assert.That(h.ReceivedCount, Is.EqualTo(1), "AllocationReceived is raised once, like any allocation");
                Assert.That(h.Fake.Count("POST", "/allocate"), Is.EqualTo(1));
                Assert.That(h.Fake.Requests.Single(r => r.Path == "/allocate").Body, Is.EqualTo("{}"));
                Assert.That(h.Fake.Integrated, Is.True, "a write integrates the game");
            }
        }

        [Test]
        public async Task NothingIsSentWhileAnAllocationIsCurrentBecauseTheSupervisorWouldReplaceIt()
        {
            using (FleetShimHarness h = await ReadyHarness())
            {
                h.Fake.Allocate("alloc-match", "{\"matchmaker\":true,\"roster\":[]}");
                await Wait.Until(() => h.ReceivedCount == 1, "the platform's allocation");

                // Mutation: drop the "before != null" refusal in FleetSdk.SelfAllocateOnceAsync and the fake replaces
                // alloc-match with a self-allocation, so CurrentAllocation and the request count both fail.
                SelfAllocationResult result = await h.Sdk.AllocateSelfAsync(None);
                Assert.That(result.Outcome, Is.EqualTo(FleetCallOutcome.Rejected));
                Assert.That(result.Status, Is.EqualTo(0), "refused locally, nothing sent");
                Assert.That(result.AlreadyAllocated, Is.True);
                Assert.That(result.IsConfirmed, Is.False);
                Assert.That(result.Allocation.AllocationId, Is.EqualTo("alloc-match"), "the allocation that was already current");
                Assert.That(h.Fake.Count("POST", "/allocate"), Is.EqualTo(0));
                await Wait.For(100);
                Assert.That(h.Sdk.CurrentAllocation.AllocationId, Is.EqualTo("alloc-match"));
            }
        }

        [Test]
        public async Task ConcurrentCallersShareOneRequestAndOneSelfAllocation()
        {
            using (FleetShimHarness h = await ReadyHarness())
            {
                Task<SelfAllocationResult> first = h.Sdk.AllocateSelfAsync(None);
                Task<SelfAllocationResult> second = h.Sdk.AllocateSelfAsync(None);
                SelfAllocationResult[] both = await Task.WhenAll(first, second);
                Assert.That(both.All(r => r.IsConfirmed), Is.True);
                Assert.That(both[0].Allocation.AllocationId, Is.EqualTo(both[1].Allocation.AllocationId));
                Assert.That(h.Fake.Count("POST", "/allocate"), Is.EqualTo(1));

                // A later caller finds the self-allocation current: refused locally, naming it, still one request.
                SelfAllocationResult later = await h.Sdk.AllocateSelfAsync(None);
                Assert.That(later.AlreadyAllocated, Is.True);
                Assert.That(later.Allocation.IsSelfAllocated, Is.True);
                Assert.That(h.Fake.Count("POST", "/allocate"), Is.EqualTo(1));
            }
        }

        [Test]
        public async Task ARefusalOrAMissingRouteIsReturnedAndNeverConfirmed()
        {
            using (FleetShimHarness h = await ReadyHarness())
            {
                h.Fake.FailNext("/allocate", 500, "{\"message\":\"boom\"}");
                SelfAllocationResult refused = await h.Sdk.AllocateSelfAsync(None);
                Assert.That(refused.Outcome, Is.EqualTo(FleetCallOutcome.Rejected));
                Assert.That(refused.Status, Is.EqualTo(500));
                Assert.That(refused.Message, Is.EqualTo("boom"));
                Assert.That(refused.IsConfirmed, Is.False);
                Assert.That(refused.AlreadyAllocated, Is.False);

                h.Fake.FailNext("/allocate", 501, "{\"error\":\"not_implemented\"}");
                SelfAllocationResult missing = await h.Sdk.AllocateSelfAsync(None);
                Assert.That(missing.Outcome, Is.EqualTo(FleetCallOutcome.Unsupported));
                Assert.That(missing.IsConfirmed, Is.False);
                Assert.That(h.Sdk.CurrentAllocation, Is.Null);
            }
        }

        [Test]
        public async Task ALostAnswerIsUnreachableAndTheLaterFrameStillOpensTheSession()
        {
            using (FleetShimHarness h = await ReadyHarness())
            {
                h.Fake.AbortAnswerNext("/allocate");
                SelfAllocationResult lost = await h.Sdk.AllocateSelfAsync(None);
                Assert.That(lost.Outcome, Is.EqualTo(FleetCallOutcome.Unreachable));
                Assert.That(lost.IsConfirmed, Is.False);

                h.Fake.PushView();
                await Wait.Until(() => h.ReceivedCount == 1, "the self-allocation's frame");
                Assert.That(h.Sdk.CurrentAllocation.IsSelfAllocated, Is.True);
                Assert.That(h.Sdk.State, Is.EqualTo(FleetState.InSession));
            }
        }

        [Test]
        public async Task AnAcceptedCallWhoseFrameNeverArrivesIsOkButUnconfirmedAfterTheWait()
        {
            using (FleetShimHarness h = await ReadyHarness())
            {
                h.Fake.HoldFrameNext("/allocate");
                var start = h.Scheduler.UtcNow;
                SelfAllocationResult result = await h.Sdk.AllocateSelfAsync(None);
                Assert.That(result.Outcome, Is.EqualTo(FleetCallOutcome.Ok));
                Assert.That(result.IsConfirmed, Is.False);
                Assert.That(result.Allocation, Is.Null);
                Assert.That(result.Message, Does.Contain("no watch frame"));
                Assert.That(h.Scheduler.UtcNow - start, Is.GreaterThanOrEqualTo(new FleetSdkOptions().SelfAllocationWait));
                Assert.That(h.Logs.For("allocate").Any(e => e.Level == FleetLogLevel.Warning), Is.True, h.Logs.Dump());

                // The supervisor did self-allocate (only the frame was lost): a second request would replace that session.
                SelfAllocationResult again = await h.Sdk.AllocateSelfAsync(None);
                Assert.That(again.AwaitingEarlier, Is.True, again.Message);
                Assert.That(again.Outcome, Is.EqualTo(FleetCallOutcome.Rejected));
                Assert.That(again.Status, Is.EqualTo(0));
                Assert.That(h.Fake.Count("POST", "/allocate"), Is.EqualTo(1), "held back while the first is unconfirmed");
            }
        }

        [Test]
        public async Task AfterALostAnswerNoSecondAllocateIsSentUntilTheGracePasses()
        {
            using (FleetShimHarness h = await ReadyHarness())
            {
                // Mutation: drop HoldBackSelfAllocation on Unreachable in FleetSdk.SelfAllocateOnceAsync and the second call
                // sends a second POST /allocate.
                h.Fake.AbortAnswerNext("/allocate");
                SelfAllocationResult lost = await h.Sdk.AllocateSelfAsync(None);
                Assert.That(lost.Outcome, Is.EqualTo(FleetCallOutcome.Unreachable));

                SelfAllocationResult held = await h.Sdk.AllocateSelfAsync(None);
                Assert.That(held.AwaitingEarlier, Is.True, held.Message);
                Assert.That(held.IsConfirmed, Is.False);
                Assert.That(held.AlreadyAllocated, Is.False);
                Assert.That(held.Message, Does.Contain("not confirmed yet"));
                Assert.That(h.Fake.Count("POST", "/allocate"), Is.EqualTo(1));
                Assert.That(HostedAdmissionEvidence.MapSelfAllocation(held).Outcome, Is.EqualTo(PingCore.Core.Handshake.SessionClaimOutcome.Failed),
                    "a joiner who meets the hold-back is refused, never seated on an unconfirmed claim");

                h.Scheduler.Advance(new FleetSdkOptions().SelfAllocationGrace);
                SelfAllocationResult after = await h.Sdk.AllocateSelfAsync(None);
                Assert.That(after.IsConfirmed, Is.True, after.Message);
                Assert.That(h.Fake.Count("POST", "/allocate"), Is.EqualTo(2), "sent again once the grace passed");
            }
        }

        [Test]
        public async Task AfterALostAnswerTheLateFrameEndsTheHoldBackAndNothingIsSentAgain()
        {
            using (FleetShimHarness h = await ReadyHarness())
            {
                h.Fake.AbortAnswerNext("/allocate");
                Assert.That((await h.Sdk.AllocateSelfAsync(None)).Outcome, Is.EqualTo(FleetCallOutcome.Unreachable));
                h.Fake.PushView();
                await Wait.Until(() => h.ReceivedCount == 1, "the self-allocation's late frame");

                SelfAllocationResult later = await h.Sdk.AllocateSelfAsync(None);
                Assert.That(later.AlreadyAllocated, Is.True, "the session the lost answer belonged to is current");
                Assert.That(later.Allocation.IsSelfAllocated, Is.True);
                Assert.That(later.AwaitingEarlier, Is.False);
                Assert.That(h.Fake.Count("POST", "/allocate"), Is.EqualTo(1));

                // Once that session ends, the hold-back is gone: the next claim is sent at once.
                Assert.That((await h.Sdk.EndSessionAsync(later.Allocation.AllocationId, None)).IsOk, Is.True);
                await Wait.Until(() => h.Sdk.CurrentAllocation == null, "the session's end");
                Assert.That((await h.Sdk.AllocateSelfAsync(None)).IsConfirmed, Is.True);
                Assert.That(h.Fake.Count("POST", "/allocate"), Is.EqualTo(2));
            }
        }

        [Test]
        public async Task APlatformAllocationThatLandsDuringThePostIsReplacedAndLoggedAsAWarning()
        {
            using (FleetShimHarness h = await ReadyHarness())
            {
                // Discovery's allocation frame reaches the supervisor while the POST /allocate is in flight; the supervisor
                // replaces it with the self-allocation. Mutation: drop the "received - receivedBefore > 1" warning.
                h.Fake.BeforeRouteNext("/allocate", () => h.Fake.Allocate("alloc-match", "{\"matchmaker\":true,\"roster\":[]}"));
                SelfAllocationResult result = await h.Sdk.AllocateSelfAsync(None);
                Assert.That(result.IsConfirmed, Is.True, result.Message);
                lock (h.Received)
                {
                    Assert.That(h.Received.Select(a => a.AllocationId).First(), Is.EqualTo("alloc-match"));
                    Assert.That(h.Received.Last().IsSelfAllocated, Is.True);
                }

                Assert.That(h.Logs.For("allocate").Any(e => e.Level == FleetLogLevel.Warning && e.Message.Contains("replaced it with the self-allocation")), Is.True,
                    h.Logs.Dump());
            }
        }

        [Test]
        public async Task APlatformAllocationThatReplacesTheSelfAllocationIsLoggedAsAWarning()
        {
            using (FleetShimHarness h = await ReadyHarness())
            {
                SelfAllocationResult self = await h.Sdk.AllocateSelfAsync(None);
                Assert.That(self.IsConfirmed, Is.True, self.Message);

                // The supervisor delivers an allocation without looking at what is current.
                // Mutation: drop the selfReplaced warning in FleetSdk.ApplyView.
                h.Fake.Allocate("alloc-match", "{\"matchmaker\":true,\"roster\":[]}");
                await Wait.Until(() => h.Sdk.CurrentAllocation?.AllocationId == "alloc-match", "the platform's allocation");
                Assert.That(h.Logs.For("allocation").Count(e => e.Level == FleetLogLevel.Warning && e.Message.Contains("replaced this game server's own self-allocation")),
                    Is.EqualTo(1), h.Logs.Dump());
                lock (h.Cleared)
                {
                    Assert.That(h.Cleared.Single().AllocationId, Is.EqualTo(self.Allocation.AllocationId));
                    Assert.That(h.Cleared.Single().Reason, Is.EqualTo(AllocationClearedReason.ClearedByPlatform));
                }
            }
        }

        [Test]
        public async Task AMatchAfterTheGameEndedItsSelfAllocationIsNoWarning()
        {
            using (FleetShimHarness h = await ReadyHarness())
            {
                SelfAllocationResult self = await h.Sdk.AllocateSelfAsync(None);
                Assert.That((await h.Sdk.EndSessionAsync(self.Allocation.AllocationId, None)).IsOk, Is.True);
                await Wait.Until(() => h.Sdk.CurrentAllocation == null, "the session's end");
                h.Fake.Allocate("alloc-match", "{\"matchmaker\":true,\"roster\":[]}");
                await Wait.Until(() => h.Sdk.CurrentAllocation?.AllocationId == "alloc-match", "the platform's allocation");
                Assert.That(h.Logs.Entries.Any(e => e.Level == FleetLogLevel.Warning && (e.Call == "allocation" || e.Call == "allocate")), Is.False, h.Logs.Dump());
            }
        }

        [Test]
        public async Task AnInertShimSendsNothing()
        {
            var forbidden = new ForbiddenTransport();
            using (FleetSdk sdk = FleetSdk.Create(new FleetSdkOptions
            {
                GetEnvironmentVariable = _ => null,
                Transport = forbidden,
                LineStream = forbidden,
                Log = _ => { },
            }))
            {
                SelfAllocationResult result = await sdk.AllocateSelfAsync(None);
                Assert.That(result.Outcome, Is.EqualTo(FleetCallOutcome.Inert));
                Assert.That(result.IsConfirmed, Is.False);
                Assert.That(forbidden.Calls, Is.EqualTo(0));
            }
        }
    }
}
