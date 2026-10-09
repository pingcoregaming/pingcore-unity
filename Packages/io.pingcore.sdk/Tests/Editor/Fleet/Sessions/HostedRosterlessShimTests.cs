using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Core.Handshake;
using PingCore.Fleet.Sessions;

namespace PingCore.Fleet.Tests.Editor.Sessions
{
    /// <summary>
    /// Which hosted allocations admit a <c>match</c> join on the allocation id alone, end to end through
    /// <see cref="FakeLocalSdkEndpoint"/>, the real shim, <see cref="HostedAdmissionEvidence"/> and
    /// <see cref="AdmissionPipeline"/>: a supervisor self-allocation only with
    /// <see cref="ApprovalOptions.AllowSelfAllocatedJoins"/>, a backend allocation without a roster always,
    /// and an allocation whose context annotation does not parse never.
    /// </summary>
    public sealed class HostedRosterlessShimTests
    {
        private const string TicketId = "H8sJ2dQe5uWm0aZr6tLc3A";
        private static readonly CancellationToken None = CancellationToken.None;

        private static async Task<FleetShimHarness> Allocated(string allocationId, string contextJson)
        {
            var h = new FleetShimHarness();
            await h.Sdk.StartAsync(None);
            await h.Sdk.ReadyAsync(None);
            h.Fake.Allocate(allocationId, contextJson);
            await Wait.Until(() => h.ReceivedCount == 1, "the allocation");
            return h;
        }

        private static Task<AdmissionDecision> Join(AdmissionPipeline pipeline, ulong connection, string allocationId, string playerId) =>
            pipeline.AdmitAsync(connection, JoinTicketCodec.Encode(JoinTicket.ForMatch(TicketId, allocationId, playerId, 3)), None);

        [Test]
        public async Task ASelfAllocationAdmitsNoMatchJoinByDefault()
        {
            const string self = "self-1791200000000";
            using (FleetShimHarness h = await Allocated(self, "{}"))
            {
                Assert.That(h.Sdk.CurrentAllocation.IsSelfAllocated, Is.True);
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = FleetSessionShimTests.Hosted(h);
                using (watcher)
                {
                    // Mutation: drop facts.SelfAllocation from HostedAdmissionEvidence and this join is admitted.
                    AdmissionDecision decision = await Join(pipeline, 1, self, "anon:p0");
                    Assert.That(decision.ReasonWire, Is.EqualTo("not_in_roster"), decision.Detail);
                    Assert.That(decision.Detail, Does.Contain("AllowSelfAllocatedJoins"));
                    Assert.That(pipeline.Ledger.Count, Is.EqualTo(0));
                }
            }
        }

        [Test]
        public async Task ASelfAllocationAdmitsAMatchJoinWithTheFlag()
        {
            const string self = "self-1791200000000";
            using (FleetShimHarness h = await Allocated(self, "{}"))
            {
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = FleetSessionShimTests.Hosted(h, allowSelfAllocatedJoins: true);
                using (watcher)
                {
                    AdmissionDecision decision = await Join(pipeline, 1, self, "anon:p0");
                    Assert.That(decision.Approved, Is.True, decision.Detail);
                    Assert.That(pipeline.Ledger.AdmittedForAllocation(self), Is.EqualTo(1));
                }
            }
        }

        [Test]
        public async Task ABackendAllocationWithoutARosterIsAdmittedOnItsIdWithoutTheFlag()
        {
            const string backend = "9b2f6c1e-4d7a-4f0e-8a3b-2c5d7e9f1a40";
            using (FleetShimHarness h = await Allocated(backend, "{\"mode\":\"duel\"}"))
            {
                Assert.That(h.Sdk.CurrentAllocation.IsSelfAllocated, Is.False);
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = FleetSessionShimTests.Hosted(h);
                using (watcher)
                {
                    AdmissionDecision decision = await Join(pipeline, 1, backend, "anon:p0");
                    Assert.That(decision.Approved, Is.True, decision.Detail);
                    Assert.That((await Join(pipeline, 2, "another-allocation", "anon:p1")).ReasonWire, Is.EqualTo("allocation_mismatch"));
                }
            }
        }

        [Test]
        public async Task ABackendAllocationWhoseContextAnnotationIsTruncatedFailsClosedAsNotInRoster()
        {
            const string backend = "9b2f6c1e-4d7a-4f0e-8a3b-2c5d7e9f1a40";
            // The annotation is cut off mid-object: it does not parse, so the shim's context is empty.
            using (FleetShimHarness h = await Allocated(backend, "{\"mode\":\"duel\",\"roster\":[{\"ticketId\":\"abc"))
            {
                AllocationInfo allocation = h.Sdk.CurrentAllocation;
                Assert.That(allocation.ContextInvalid, Is.True);
                Assert.That(allocation.Context.Count, Is.EqualTo(0));
                Assert.That(h.Logs.For("allocation").Any(e => e.Message.Contains("ContextInvalid")), Is.True, h.Logs.Dump());
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = FleetSessionShimTests.Hosted(h, allowSelfAllocatedJoins: true);
                using (watcher)
                {
                    // Mutation: drop contextInvalid from MatchContext.HasRoster and this join is admitted as rosterless.
                    AdmissionDecision decision = await Join(pipeline, 1, backend, "anon:p0");
                    Assert.That(decision.ReasonWire, Is.EqualTo("not_in_roster"), decision.Detail);
                    Assert.That(pipeline.Ledger.Count, Is.EqualTo(0));
                }
            }
        }
    }
}
