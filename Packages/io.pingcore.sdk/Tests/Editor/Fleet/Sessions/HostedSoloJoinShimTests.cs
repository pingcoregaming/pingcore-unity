using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core.Handshake;
using PingCore.Fleet.Sessions;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet.Tests.Editor.Sessions
{
    /// <summary>
    /// The solo join (option A) end to end through <see cref="FakeLocalSdkEndpoint"/>, the real shim,
    /// <see cref="HostedAdmissionEvidence"/> and <see cref="AdmissionPipeline"/>: without
    /// <see cref="ApprovalOptions.ClaimIdleSessions"/> nothing is ever self-allocated; opted in, a hold pushed to an idle game server,
    /// the reservation join accepted on it, the self-allocation made before the approval (so the game server is
    /// <c>in_session</c>), a second joiner into the same session with no second self-allocation, the self-allocation's id
    /// still refused as a <c>match</c> join, and the race where the platform allocates the game server between the hold
    /// and the claim. Plus <see cref="HostedAdmissionEvidence.MapSelfAllocation"/> row by row.
    /// </summary>
    public sealed class HostedSoloJoinShimTests
    {
        private const string TicketId = "H8sJ2dQe5uWm0aZr6tLc3A";
        private static readonly CancellationToken None = CancellationToken.None;

        private static JObject Hold(FleetShimHarness h, string reservationId, string playerId) => new JObject
        {
            ["reservationId"] = reservationId, ["serverId"] = "4211", ["seats"] = 1, ["playerIds"] = new JArray(playerId),
            ["context"] = null, ["expiresAt"] = h.Scheduler.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(), ["ownerKind"] = "player", ["ownerPlayerId"] = playerId,
        };

        private static Task<AdmissionDecision> Reserve(AdmissionPipeline pipeline, ulong connection, string reservationId, string playerId) =>
            pipeline.AdmitAsync(connection, JoinTicketCodec.Encode(JoinTicket.ForReservation(reservationId, playerId, 3)), None);

        private static async Task<FleetShimHarness> Idle()
        {
            var h = new FleetShimHarness();
            await h.Sdk.StartAsync(None);
            await h.Sdk.ReadyAsync(None);
            return h;
        }

        [Test]
        public async Task AHoldOnAnIdleGameServerSelfAllocatesBeforeTheApprovalAndTheGameServerIsInSession()
        {
            using (FleetShimHarness h = await Idle())
            {
                var gate = new FleetSessionShimTests.CountingGate();
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = FleetSessionShimTests.Hosted(h, gate, claimIdleSessions: true);
                using (watcher)
                {
                    h.Fake.AddReservation(Hold(h, "rsv-solo", "anon:solo"));
                    AdmissionDecision decision = await Reserve(pipeline, 1, "rsv-solo", "anon:solo");
                    Assert.That(decision.Approved, Is.True, decision.Detail);
                    Assert.That(decision.SessionClaim, Is.EqualTo(SessionClaimOutcome.Claimed));
                    Assert.That(decision.AllocationId, Is.Null, "admitted on the hold");

                    // By the time the approval is returned the self-allocation is current: the game server reads in_session.
                    Assert.That(h.Sdk.State, Is.EqualTo(FleetState.InSession));
                    Assert.That(h.Sdk.CurrentAllocation.IsSelfAllocated, Is.True);
                    Assert.That(h.ReceivedCount, Is.EqualTo(1));

                    var paths = h.Fake.Requests.Select(r => r.Method + " " + r.Path).ToList();
                    int lookup = paths.LastIndexOf("GET /pingcore/reservations/rsv-solo");
                    int allocate = paths.IndexOf("POST /allocate");
                    Assert.That(lookup, Is.GreaterThanOrEqualTo(0));
                    Assert.That(allocate, Is.GreaterThan(lookup), "the hold is read before the game server claims itself");
                    Assert.That(paths.Count(p => p == "POST /allocate"), Is.EqualTo(1));
                    Assert.That(h.Fake.Requests.Any(r => r.Path.Contains("verify")), Is.False, "the hosted path never calls verify");
                    Assert.That(gate.Calls, Is.EqualTo(1));
                }
            }
        }

        [Test]
        public async Task WithoutTheOptInAHoldOnAnIdleGameServerIsAdmittedAndNothingIsSelfAllocated()
        {
            using (FleetShimHarness h = await Idle())
            {
                // The default options, as a game that never heard of the claim builds them.
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = FleetSessionShimTests.Hosted(h);
                using (watcher)
                {
                    h.Fake.AddReservation(Hold(h, "rsv-solo", "anon:solo"));
                    AdmissionDecision decision = await Reserve(pipeline, 1, "rsv-solo", "anon:solo");
                    Assert.That(decision.Approved, Is.True, "the default gate admits, as before the claim existed: " + decision.Detail);
                    Assert.That(decision.SessionClaim, Is.EqualTo(SessionClaimOutcome.None));
                    await Wait.For(100);
                    Assert.That(h.Fake.Count("POST", "/allocate"), Is.EqualTo(0), "no session the game would have to end");
                    Assert.That(h.Sdk.CurrentAllocation, Is.Null);
                    Assert.That(h.Sdk.State, Is.EqualTo(FleetState.Ready));
                }
            }
        }

        [Test]
        public async Task ASecondJoinerEntersTheClaimedSessionWithoutASecondSelfAllocation()
        {
            using (FleetShimHarness h = await Idle())
            {
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = FleetSessionShimTests.Hosted(h, claimIdleSessions: true);
                using (watcher)
                {
                    h.Fake.AddReservation(Hold(h, "rsv-a", "anon:a"));
                    h.Fake.AddReservation(Hold(h, "rsv-b", "anon:b"));
                    AdmissionDecision first = await Reserve(pipeline, 1, "rsv-a", "anon:a");
                    AdmissionDecision second = await Reserve(pipeline, 2, "rsv-b", "anon:b");
                    Assert.That(first.SessionClaim, Is.EqualTo(SessionClaimOutcome.Claimed), first.Detail);
                    Assert.That(second.Approved, Is.True, second.Detail);
                    Assert.That(second.SessionClaim, Is.EqualTo(SessionClaimOutcome.None), "the session was already open");
                    Assert.That(h.Fake.Count("POST", "/allocate"), Is.EqualTo(1));
                }
            }
        }

        [Test]
        public async Task TwoJoinersOnTheIdleGameServerAtOnceShareOneSelfAllocation()
        {
            using (FleetShimHarness h = await Idle())
            {
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = FleetSessionShimTests.Hosted(h, claimIdleSessions: true);
                using (watcher)
                {
                    h.Fake.AddReservation(Hold(h, "rsv-a", "anon:a"));
                    h.Fake.AddReservation(Hold(h, "rsv-b", "anon:b"));
                    AdmissionDecision[] both = await Task.WhenAll(Reserve(pipeline, 1, "rsv-a", "anon:a"), Reserve(pipeline, 2, "rsv-b", "anon:b"));
                    Assert.That(both.All(d => d.Approved), Is.True, string.Join("; ", both.Select(d => d.Detail)));
                    Assert.That(both.All(d => d.SessionClaim == SessionClaimOutcome.Claimed), Is.True);
                    Assert.That(h.Fake.Count("POST", "/allocate"), Is.EqualTo(1));
                    Assert.That(h.ReceivedCount, Is.EqualTo(1));
                }
            }
        }

        [Test]
        public async Task TheClaimedSelfAllocationsIdStillAdmitsNoMatchJoin()
        {
            using (FleetShimHarness h = await Idle())
            {
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = FleetSessionShimTests.Hosted(h, claimIdleSessions: true);
                using (watcher)
                {
                    h.Fake.AddReservation(Hold(h, "rsv-solo", "anon:solo"));
                    Assert.That((await Reserve(pipeline, 1, "rsv-solo", "anon:solo")).Approved, Is.True);
                    string self = h.Sdk.CurrentAllocation.AllocationId;
                    AdmissionDecision guessed = await pipeline.AdmitAsync(2, JoinTicketCodec.Encode(JoinTicket.ForMatch(TicketId, self, "anon:guess", 3)), None);
                    Assert.That(guessed.ReasonWire, Is.EqualTo("not_in_roster"), guessed.Detail);
                    Assert.That(pipeline.Ledger.Count, Is.EqualTo(1));
                }
            }
        }

        [Test]
        public async Task AnUnknownHoldOnAnIdleGameServerNeverClaimsIt()
        {
            using (FleetShimHarness h = await Idle())
            {
                (AdmissionPipeline pipeline, BackfillWatcher watcher) = FleetSessionShimTests.Hosted(h, claimIdleSessions: true);
                using (watcher)
                {
                    AdmissionDecision made = await Reserve(pipeline, 1, "rsv-made-up", "anon:x");
                    Assert.That(made.ReasonWire, Is.EqualTo("reservation_invalid"));
                    Assert.That(h.Fake.Count("POST", "/allocate"), Is.EqualTo(0));
                    Assert.That(h.Sdk.State, Is.EqualTo(FleetState.Ready));
                }
            }
        }

        [TestCase(false, "refused_by_game", TestName = "The platform allocates between the hold and the claim: a game without mixed sessions refuses")]
        [TestCase(true, null, TestName = "The platform allocates between the hold and the claim: a game with mixed sessions admits into it")]
        public async Task ThePlatformAllocatingBetweenTheHoldAndTheClaimIsTheGatesCall(bool mixed, string expected)
        {
            using (FleetShimHarness h = await Idle())
            {
                // The match lands just before the claim's self-allocation: the shim refuses locally (the supervisor would replace it).
                var racing = new MatchBeforeClaimFleet(h);
                var gate = new MixedGate(mixed);
                var options = new ApprovalOptions { ProtocolVersion = 3, Mode = HostingMode.Hosted, ClaimIdleSessions = true };
                using (var watcher = new BackfillWatcher(h.Sdk, h.Scheduler))
                {
                    var pipeline = new AdmissionPipeline(options, new HostedAdmissionEvidence(racing, watcher), gate, new FleetSessionShimTests.NoDeadline(h.Scheduler));
                    h.Fake.AddReservation(Hold(h, "rsv-solo", "anon:solo"));
                    AdmissionDecision decision = await Reserve(pipeline, 1, "rsv-solo", "anon:solo");
                    Assert.That(decision.Approved ? null : decision.ReasonWire, Is.EqualTo(expected), decision.Detail);
                    Assert.That(decision.SessionClaim, Is.EqualTo(SessionClaimOutcome.AllocatedMeanwhile));
                    Assert.That(gate.SecondSayAllocation, Is.EqualTo("alloc-match"));
                    Assert.That(h.Fake.Count("POST", "/allocate"), Is.EqualTo(0), "the match is never replaced by a self-allocation");
                    Assert.That(h.Sdk.CurrentAllocation.AllocationId, Is.EqualTo("alloc-match"));
                    Assert.That(pipeline.Ledger.Count, Is.EqualTo(mixed ? 1 : 0));
                }
            }
        }

        private static readonly AllocationInfo Platform = new AllocationInfo("alloc-match", new JObject(), DateTimeOffset.UnixEpoch);
        private static readonly AllocationInfo Self = new AllocationInfo("self-1791200000000", new JObject(), DateTimeOffset.UnixEpoch);

        [Test]
        public void ASelfAllocationMapsOntoTheClaimRowByRow()
        {
            Assert.That(HostedAdmissionEvidence.MapSelfAllocation(new SelfAllocationResult(FleetCallOutcome.Ok, 200, null, Self, false)),
                Is.EqualTo(SessionClaimResult.Claimed(Self.AllocationId)), "confirmed");
            Assert.That(HostedAdmissionEvidence.MapSelfAllocation(new SelfAllocationResult(FleetCallOutcome.Rejected, 0, "x", Self, true)),
                Is.EqualTo(SessionClaimResult.Claimed(Self.AllocationId)), "already in the game server's own self-allocation");
            Assert.That(HostedAdmissionEvidence.MapSelfAllocation(new SelfAllocationResult(FleetCallOutcome.Rejected, 0, "x", Platform, true)),
                Is.EqualTo(SessionClaimResult.AllocatedMeanwhile(Platform.AllocationId)), "the platform's allocation came first");
            foreach (SelfAllocationResult failed in new[]
            {
                new SelfAllocationResult(FleetCallOutcome.Ok, 200, "no frame", null, false),
                new SelfAllocationResult(FleetCallOutcome.Ok, 200, null, Platform, false),
                new SelfAllocationResult(FleetCallOutcome.Rejected, 500, "boom", null, false),
                new SelfAllocationResult(FleetCallOutcome.Unreachable, 0, "timeout", null, false),
                new SelfAllocationResult(FleetCallOutcome.Unsupported, 501, null, null, false),
                new SelfAllocationResult(FleetCallOutcome.EndpointClosed, 0, null, null, false),
                new SelfAllocationResult(FleetCallOutcome.Cancelled, 0, null, null, false),
                new SelfAllocationResult(FleetCallOutcome.Inert, 0, null, null, false),
                new SelfAllocationResult(FleetCallOutcome.Rejected, 0, "x", null, true),
                null,
            })
            {
                Assert.That(HostedAdmissionEvidence.MapSelfAllocation(failed).Outcome, Is.EqualTo(SessionClaimOutcome.Failed), failed?.ToString() ?? "null");
            }
        }

        private sealed class MixedGate : IAdmissionGate
        {
            private readonly bool mixed;

            public MixedGate(bool mixed) => this.mixed = mixed;

            public string SecondSayAllocation { get; private set; }

            public AdmissionGateResult CanAdmit(JoinTicket ticket, AdmissionFacts facts)
            {
                if (facts.SessionClaim != SessionClaimOutcome.AllocatedMeanwhile)
                {
                    return AdmissionGateResult.Admit;
                }

                SecondSayAllocation = facts.CurrentAllocationId;
                return mixed ? AdmissionGateResult.Admit : AdmissionGateResult.Reject(JoinRejectReason.RefusedByGame, "no mixed sessions");
            }
        }

        /// <summary>The real shim, except that a platform allocation lands (and the shim reads it) right before the self-allocation.</summary>
        private sealed class MatchBeforeClaimFleet : IFleetSdk
        {
            private readonly FleetShimHarness h;

            public MatchBeforeClaimFleet(FleetShimHarness h) => this.h = h;

            private IFleetSdk Real => h.Sdk;

            public bool IsHosted => Real.IsHosted;

            public FleetState State => Real.State;

            public GameServerSnapshot Current => Real.Current;

            public AllocationInfo CurrentAllocation => Real.CurrentAllocation;

            public event Action<FleetStateChange> StateChanged { add => Real.StateChanged += value; remove => Real.StateChanged -= value; }

            public event Action<GameServerSnapshot> GameServerChanged { add => Real.GameServerChanged += value; remove => Real.GameServerChanged -= value; }

            public event Action<AllocationInfo> AllocationReceived { add => Real.AllocationReceived += value; remove => Real.AllocationReceived -= value; }

            public event Action<AllocationCleared> AllocationCleared { add => Real.AllocationCleared += value; remove => Real.AllocationCleared -= value; }

            public async Task<SelfAllocationResult> AllocateSelfAsync(CancellationToken cancellationToken)
            {
                h.Fake.Allocate("alloc-match", "{\"matchmaker\":true,\"roster\":[]}");
                await Wait.Until(() => Real.CurrentAllocation != null, "the platform's allocation");
                return await Real.AllocateSelfAsync(cancellationToken);
            }

            public Task<bool> StartAsync(CancellationToken cancellationToken) => Real.StartAsync(cancellationToken);

            public Task<FleetCallResult> ReadyAsync(CancellationToken cancellationToken) => Real.ReadyAsync(cancellationToken);

            public Task<CounterResult> SetCounterAsync(string name, long count, CancellationToken cancellationToken) => Real.SetCounterAsync(name, count, cancellationToken);

            public Task<CounterResult> GetCounterAsync(string name, CancellationToken cancellationToken) => Real.GetCounterAsync(name, cancellationToken);

            public Task<FleetCallResult> EndSessionAsync(string allocationId, CancellationToken cancellationToken) => Real.EndSessionAsync(allocationId, cancellationToken);

            public Task<JoinablePublishResult> PublishJoinableAsync(string allocationId, JoinableSessionRequest request, CancellationToken cancellationToken) =>
                Real.PublishJoinableAsync(allocationId, request, cancellationToken);

            public Task<FleetCallResult> WithdrawJoinableAsync(string allocationId, CancellationToken cancellationToken) => Real.WithdrawJoinableAsync(allocationId, cancellationToken);

            public Task<BackfillsResult> GetBackfillsAsync(CancellationToken cancellationToken) => Real.GetBackfillsAsync(cancellationToken);

            public Task<ReservationLookup> GetReservationAsync(string reservationId, CancellationToken cancellationToken) => Real.GetReservationAsync(reservationId, cancellationToken);

            public Task<ReservationsResult> ListReservationsAsync(CancellationToken cancellationToken) => Real.ListReservationsAsync(cancellationToken);

            public Task<FleetCallResult> ShutdownAsync(CancellationToken cancellationToken) => Real.ShutdownAsync(cancellationToken);

            public void NotifyProcessStopping() => Real.NotifyProcessStopping();

            public void Dispose()
            {
            }
        }
    }
}
