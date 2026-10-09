using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Core.Handshake;
using static PingCore.Sdk.Tests.Editor.Handshake.HandshakeTestData;

namespace PingCore.Sdk.Tests.Editor.Handshake
{
    /// <summary>
    /// The hosted reservation rule (solo join, option A), opt-in through <see cref="ApprovalOptions.ClaimIdleSessions"/>: a
    /// <c>reservation</c> join accepted on an idle PingCore-hosted game server is approved only after the game server claimed a
    /// session for it, and the joiner is admitted on the hold. Without the opt-in nothing is ever claimed.
    /// <see cref="JoinAdmission.NeedsSessionClaim"/> cell by cell, then the pipeline on a manual clock with a fake
    /// claiming evidence: the default that never claims, each claim outcome, the gate's second say when the platform
    /// allocated meanwhile, stopping during the claim, the deadline, a closed connection, a throwing claim, and an evidence
    /// that cannot claim.
    /// </summary>
    public sealed class AdmissionSessionClaimTests
    {
        private const string SelfId = "self-1791200000000";
        private const string MatchId = "9b2f6c1e-4d7a-4f0e-8a3b-2c5d7e9f1a40";

        private sealed class ClaimingEvidence : IAdmissionEvidence, ISessionClaimEvidence
        {
            public Action<AdmissionFacts> Gather { get; set; } = f => f.Reservation = ReservationEvidence.HoldFound;

            public Func<CancellationToken, Task<SessionClaimResult>> Claim { get; set; } = c => Task.FromResult(SessionClaimResult.Claimed(SelfId));

            public int Claims { get; private set; }

            public bool SawCancellation { get; private set; }

            public string Source => "fleet";

            public bool IsStopping => false;

            public Task GatherAsync(JoinTicket ticket, AdmissionFacts facts, ApprovalOptions options, CancellationToken cancellationToken)
            {
                facts.ReservationSeats = 2;
                Gather(facts);
                return Task.CompletedTask;
            }

            public async Task<SessionClaimResult> ClaimSessionAsync(JoinTicket ticket, AdmissionFacts facts, CancellationToken cancellationToken)
            {
                Claims++;
                try
                {
                    return await Claim(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    SawCancellation = true;
                    throw;
                }
            }
        }

        /// <summary>Hosted evidence that cannot claim (no <see cref="ISessionClaimEvidence"/>).</summary>
        private sealed class ReadOnlyEvidence : IAdmissionEvidence
        {
            public string Source => "fleet";

            public bool IsStopping => false;

            public Task GatherAsync(JoinTicket ticket, AdmissionFacts facts, ApprovalOptions options, CancellationToken cancellationToken)
            {
                facts.Reservation = ReservationEvidence.HoldFound;
                facts.ReservationSeats = 1;
                return Task.CompletedTask;
            }
        }

        private sealed class RecordingGate : IAdmissionGate
        {
            public List<(string AllocationId, SessionClaimOutcome Claim)> Asked { get; } = new List<(string, SessionClaimOutcome)>();

            public Func<AdmissionFacts, AdmissionGateResult> Answer { get; set; } = f => AdmissionGateResult.Admit;

            public AdmissionGateResult CanAdmit(JoinTicket ticket, AdmissionFacts facts)
            {
                Asked.Add((facts.CurrentAllocationId, facts.SessionClaim));
                return Answer(facts);
            }
        }

        private static (AdmissionPipeline Pipeline, ClaimingEvidence Evidence, RecordingGate Gate, ManualScheduler Clock) Make(bool claimIdleSessions = true)
        {
            var evidence = new ClaimingEvidence();
            var gate = new RecordingGate();
            var clock = new ManualScheduler();
            var options = new ApprovalOptions { ProtocolVersion = Protocol, Mode = HostingMode.Hosted, ClaimIdleSessions = claimIdleSessions };
            return (new AdmissionPipeline(options, evidence, gate, clock), evidence, gate, clock);
        }

        private static byte[] Encode(JoinTicket ticket) => JoinTicketCodec.Encode(ticket);

        /// <summary>The facts of a game that opted in to the claim.</summary>
        private static AdmissionFacts OptedIn(AdmissionFacts facts) => facts.With(f => f.ClaimIdleSessions = true);

        // ---- the pure rule ------------------------------------------------------------------------------

        private static IEnumerable<TestCaseData> ClaimCells()
        {
            yield return new TestCaseData(Reservation(), OptedIn(HostedHold(1)), true).SetName("hosted reservation, idle, opted in: claim");
            yield return new TestCaseData(Reservation(), HostedHold(1), false).SetName("hosted reservation, idle, not opted in (the default): no claim");
            yield return new TestCaseData(Reservation(), OptedIn(HostedHold(1)).With(f => f.CurrentAllocationId = MatchId), false).SetName("hosted reservation, in a session: no claim");
            yield return new TestCaseData(Reservation(), OptedIn(HostedHold(1)).With(f => f.CurrentAllocationId = SelfId), false).SetName("hosted reservation, in a self-allocated session: no claim");
            yield return new TestCaseData(Reservation(), OptedIn(HostedHold(1)).With(f => f.SessionClaim = SessionClaimOutcome.AllocatedMeanwhile), false).SetName("hosted reservation, already claimed once: no second claim");
            yield return new TestCaseData(Match(), OptedIn(Facts(HostingMode.Hosted)), false).SetName("hosted match, idle: no claim (allocation_mismatch instead)");
            yield return new TestCaseData(BackfillTicket(), OptedIn(Facts(HostingMode.Hosted)), false).SetName("hosted backfill, idle: no claim");
            yield return new TestCaseData(Reservation(), OptedIn(Verified(HostingMode.SelfHosted, ReservationEvidence.VerifyValid)), false).SetName("self-hosted reservation: no claim");
            yield return new TestCaseData(Reservation(), OptedIn(Verified(HostingMode.Listen, ReservationEvidence.VerifyValid)), false).SetName("online listen reservation: no claim");
            yield return new TestCaseData(Lan(), OptedIn(Facts(HostingMode.Listen, true)), false).SetName("LAN-only lan: no claim");
        }

        [TestCaseSource(nameof(ClaimCells))]
        public void OnlyAHostedReservationOnAnIdleGameServerNeedsASessionClaim(JoinTicket ticket, AdmissionFacts facts, bool expected)
        {
            Assert.That(JoinAdmission.NeedsSessionClaim(ticket, facts), Is.EqualTo(expected));
        }

        [Test]
        public void TheHostedReservationCellAcceptsAHoldWithOrWithoutAnOpenSession()
        {
            // Mutation: requiring CurrentAllocationId in JoinAdmission.HostedReservation fails the idle row.
            Assert.That(JoinAdmission.Decide(Reservation(), HostedHold(1)).Approved, Is.True, "idle: the gate and the claim decide");
            Assert.That(JoinAdmission.Decide(Reservation(), HostedHold(1).With(f => f.CurrentAllocationId = MatchId)).Approved, Is.True, "in a session");
            Assert.That(JoinAdmission.Decide(Reservation(), HostedHold(1).With(f => f.Reservation = ReservationEvidence.HoldNotFound)).ReasonWire,
                Is.EqualTo("reservation_invalid"), "the hold is still the evidence");
        }

        [Test]
        public void ASelfAllocationIdIsStillNeverAdmissibleAsAMatchJoin()
        {
            AdmissionFacts claimed = Facts(HostingMode.Hosted).With(f =>
            {
                f.CurrentAllocationId = SelfId;
                f.RosterlessAllocation = true;
                f.SelfAllocation = true;
                f.SessionClaim = SessionClaimOutcome.Claimed;
            });
            Assert.That(JoinAdmission.Decide(Match(allocationId: SelfId), claimed).ReasonWire, Is.EqualTo("not_in_roster"));
            Assert.That(JoinAdmission.NeedsSessionClaim(Match(allocationId: SelfId), OptedIn(claimed)), Is.False);
        }

        [Test]
        public void TheRuleRefusesNullArguments()
        {
            Assert.Throws<ArgumentNullException>(() => JoinAdmission.NeedsSessionClaim(null, HostedHold(1)));
            Assert.Throws<ArgumentNullException>(() => JoinAdmission.NeedsSessionClaim(Reservation(), null));
        }

        // ---- the pipeline -------------------------------------------------------------------------------

        [Test]
        public async Task ByDefaultAnIdleReservationIsNeverClaimedAndTheGateDecidesAlone()
        {
            // Mutation: drop facts.ClaimIdleSessions from JoinAdmission.NeedsSessionClaim (or default the option to true)
            // and both joins claim the game server.
            Assert.That(new ApprovalOptions().ClaimIdleSessions, Is.False, "the claim is opt-in");
            var (pipeline, evidence, gate, _) = Make(claimIdleSessions: false);
            AdmissionDecision admitted = await pipeline.AdmitAsync(1, Encode(Reservation()), CancellationToken.None);
            Assert.That(admitted.Approved, Is.True, admitted.Detail);
            Assert.That(admitted.SessionClaim, Is.EqualTo(SessionClaimOutcome.None), "approved as before the claim existed");

            Assert.That(pipeline.Release(1), Is.True, "the first join held its seat");
            gate.Answer = f => AdmissionGateResult.Reject(JoinRejectReason.NotInSession, "no open session");
            AdmissionDecision refused = await pipeline.AdmitAsync(2, Encode(Reservation()), CancellationToken.None);
            Assert.That(refused.ReasonWire, Is.EqualTo("not_in_session"));
            Assert.That(refused.SessionClaim, Is.EqualTo(SessionClaimOutcome.None));
            Assert.That(evidence.Claims, Is.EqualTo(0), "nothing is ever claimed without the opt-in");
            Assert.That(gate.Asked, Is.EqualTo(new[] { ((string)null, SessionClaimOutcome.None), ((string)null, SessionClaimOutcome.None) }));
        }

        [Test]
        public async Task ByDefaultAnEvidenceThatCannotClaimStillAdmitsAnIdleReservation()
        {
            var options = new ApprovalOptions { ProtocolVersion = Protocol, Mode = HostingMode.Hosted };
            var pipeline = new AdmissionPipeline(options, new ReadOnlyEvidence(), null, new ManualScheduler());
            AdmissionDecision decision = await pipeline.AdmitAsync(1, Encode(Reservation()), CancellationToken.None);
            Assert.That(decision.Approved, Is.True, decision.Detail);
            Assert.That(decision.SessionClaim, Is.EqualTo(SessionClaimOutcome.None));
        }

        [Test]
        public async Task AGameServerThatBeganStoppingDuringTheClaimRefusesTheJoinerStopping()
        {
            var (pipeline, evidence, _, _) = Make();
            evidence.Claim = c =>
            {
                pipeline.NotifyStopping();
                return Task.FromResult(SessionClaimResult.Claimed(SelfId));
            };
            AdmissionDecision decision = await pipeline.AdmitAsync(1, Encode(Reservation()), CancellationToken.None);
            Assert.That(decision.ReasonWire, Is.EqualTo("stopping"));
            Assert.That(decision.SessionClaim, Is.EqualTo(SessionClaimOutcome.Claimed), "the claim landed; the game ends that session itself");
            Assert.That(pipeline.Ledger.Count, Is.EqualTo(0), "the held seat is given back");
        }

        [Test]
        public async Task AnIdleReservationIsApprovedOnlyAfterTheClaimAndOnTheHold()
        {
            var (pipeline, evidence, gate, _) = Make();
            AdmissionDecision decision = await pipeline.AdmitAsync(7, Encode(Reservation()), CancellationToken.None);
            Assert.That(decision.Approved, Is.True, decision.Detail);
            Assert.That(decision.SessionClaim, Is.EqualTo(SessionClaimOutcome.Claimed));
            Assert.That(decision.Kind, Is.EqualTo(JoinTicketKind.Reservation));
            Assert.That(decision.ReservationId, Is.EqualTo("res-1"));
            Assert.That(decision.AllocationId, Is.Null, "admitted on the hold, never on the self-allocation id");
            Assert.That(evidence.Claims, Is.EqualTo(1));
            Assert.That(gate.Asked, Is.EqualTo(new[] { ((string)null, SessionClaimOutcome.None) }), "the gate is asked once, seeing the idle game server");
            Assert.That(pipeline.Ledger.AdmittedForReservation("res-1"), Is.EqualTo(1));
            Assert.That(decision.ToString(), Does.Contain("sessionClaim Claimed"));
        }

        [Test]
        public async Task TheClaimRunsAfterTheGateAndOnlyForAnAcceptedJoin()
        {
            var (pipeline, evidence, gate, _) = Make();
            gate.Answer = f => AdmissionGateResult.Reject(JoinRejectReason.NotInSession, "idle not allowed");
            AdmissionDecision refusedByGate = await pipeline.AdmitAsync(1, Encode(Reservation()), CancellationToken.None);
            Assert.That(refusedByGate.ReasonWire, Is.EqualTo("not_in_session"));

            evidence.Gather = f => f.Reservation = ReservationEvidence.HoldNotFound;
            AdmissionDecision refusedByTable = await pipeline.AdmitAsync(2, Encode(Reservation()), CancellationToken.None);
            Assert.That(refusedByTable.ReasonWire, Is.EqualTo("reservation_invalid"));
            Assert.That(evidence.Claims, Is.EqualTo(0), "a refused join never claims the game server");
            Assert.That(refusedByGate.SessionClaim, Is.EqualTo(SessionClaimOutcome.None));
        }

        [Test]
        public async Task AReservationIntoARunningSessionNeedsNoClaim()
        {
            var (pipeline, evidence, _, _) = Make();
            evidence.Gather = f =>
            {
                f.Reservation = ReservationEvidence.HoldFound;
                f.CurrentAllocationId = MatchId;
            };
            AdmissionDecision decision = await pipeline.AdmitAsync(1, Encode(Reservation()), CancellationToken.None);
            Assert.That(decision.Approved, Is.True, decision.Detail);
            Assert.That(decision.SessionClaim, Is.EqualTo(SessionClaimOutcome.None));
            Assert.That(evidence.Claims, Is.EqualTo(0));
        }

        [Test]
        public async Task WhenThePlatformAllocatedMeanwhileTheGateIsAskedAgainAndARefusalGivesTheSeatBack()
        {
            var (pipeline, evidence, gate, _) = Make();
            evidence.Claim = c => Task.FromResult(SessionClaimResult.AllocatedMeanwhile(MatchId));
            gate.Answer = f => f.SessionClaim == SessionClaimOutcome.AllocatedMeanwhile
                ? AdmissionGateResult.Reject(JoinRejectReason.RefusedByGame, "no mixed sessions")
                : AdmissionGateResult.Admit;
            AdmissionDecision decision = await pipeline.AdmitAsync(1, Encode(Reservation()), CancellationToken.None);
            Assert.That(decision.ReasonWire, Is.EqualTo("refused_by_game"));
            Assert.That(decision.Detail, Is.EqualTo("no mixed sessions"));
            Assert.That(decision.SessionClaim, Is.EqualTo(SessionClaimOutcome.AllocatedMeanwhile));
            Assert.That(gate.Asked, Is.EqualTo(new[] { ((string)null, SessionClaimOutcome.None), (MatchId, SessionClaimOutcome.AllocatedMeanwhile) }),
                "asked first on the idle game server, then on the platform's allocation");
            Assert.That(pipeline.Ledger.Count, Is.EqualTo(0), "the held seat is given back");
        }

        [Test]
        public async Task AGameThatAllowsMixedSessionsAdmitsTheJoinerIntoThePlatformsAllocation()
        {
            var (pipeline, evidence, gate, _) = Make();
            evidence.Claim = c => Task.FromResult(SessionClaimResult.AllocatedMeanwhile(MatchId));
            AdmissionDecision decision = await pipeline.AdmitAsync(1, Encode(Reservation()), CancellationToken.None);
            Assert.That(decision.Approved, Is.True, decision.Detail);
            Assert.That(decision.SessionClaim, Is.EqualTo(SessionClaimOutcome.AllocatedMeanwhile));
            Assert.That(gate.Asked.Count, Is.EqualTo(2));
            Assert.That(pipeline.Ledger.Holds(1), Is.True);
        }

        [Test]
        public async Task AGateThatThrowsOnItsSecondSayIsRefusedByGame()
        {
            var (pipeline, evidence, gate, _) = Make();
            evidence.Claim = c => Task.FromResult(SessionClaimResult.AllocatedMeanwhile(MatchId));
            gate.Answer = f => f.SessionClaim == SessionClaimOutcome.None ? AdmissionGateResult.Admit : throw new InvalidOperationException("boom");
            AdmissionDecision decision = await pipeline.AdmitAsync(1, Encode(Reservation()), CancellationToken.None);
            Assert.That(decision.ReasonWire, Is.EqualTo("refused_by_game"));
            Assert.That(decision.Detail, Does.Contain("InvalidOperationException"));
            Assert.That(pipeline.Ledger.Count, Is.EqualTo(0));
        }

        private static IEnumerable<TestCaseData> FailedClaims()
        {
            yield return new TestCaseData((Func<CancellationToken, Task<SessionClaimResult>>)(c => Task.FromResult(SessionClaimResult.Failed("self-allocation Unreachable"))), "self-allocation Unreachable")
                .SetName("a failed claim is refused_by_game");
            yield return new TestCaseData((Func<CancellationToken, Task<SessionClaimResult>>)(c => throw new InvalidOperationException("boom")), "InvalidOperationException")
                .SetName("a throwing claim is refused_by_game");
            yield return new TestCaseData((Func<CancellationToken, Task<SessionClaimResult>>)(c => Task.FromResult(default(SessionClaimResult))), "no detail")
                .SetName("a claim that answers no outcome is refused_by_game");
        }

        [TestCaseSource(nameof(FailedClaims))]
        public async Task AClaimThatDoesNotSucceedIsRefusedByGameAndGivesTheSeatBack(Func<CancellationToken, Task<SessionClaimResult>> claim, string detail)
        {
            var (pipeline, evidence, _, _) = Make();
            evidence.Claim = claim;
            AdmissionDecision decision = await pipeline.AdmitAsync(1, Encode(Reservation()), CancellationToken.None);
            Assert.That(decision.ReasonWire, Is.EqualTo("refused_by_game"));
            Assert.That(decision.Detail, Does.Contain(detail));
            Assert.That(decision.SessionClaim, Is.EqualTo(SessionClaimOutcome.Failed));
            Assert.That(pipeline.Ledger.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task OptedInHostedEvidenceThatCannotClaimFailsClosed()
        {
            var options = new ApprovalOptions { ProtocolVersion = Protocol, Mode = HostingMode.Hosted, ClaimIdleSessions = true };
            var pipeline = new AdmissionPipeline(options, new ReadOnlyEvidence(), null, new ManualScheduler());
            AdmissionDecision decision = await pipeline.AdmitAsync(1, Encode(Reservation()), CancellationToken.None);
            Assert.That(decision.ReasonWire, Is.EqualTo("refused_by_game"));
            Assert.That(decision.Detail, Does.Contain("cannot claim"));
            Assert.That(pipeline.Ledger.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task AClaimThatOutlivesTheRestOfTheDeadlineIsApprovalTimeout()
        {
            var (pipeline, evidence, _, clock) = Make();
            evidence.Claim = async c =>
            {
                await Task.Delay(Timeout.Infinite, c);
                return SessionClaimResult.Claimed(SelfId);
            };
            Task<AdmissionDecision> pending = pipeline.AdmitAsync(4, Encode(Reservation()), CancellationToken.None);
            await WaitFor(() => evidence.Claims == 1, "the claim to start");
            Assert.That(clock.PendingDelays, Is.EqualTo(1), "only what is left of the deadline is armed");

            clock.Advance(TimeSpan.FromSeconds(9.9));
            await Task.Delay(50);
            Assert.That(pending.IsCompleted, Is.False);
            clock.Advance(TimeSpan.FromSeconds(0.1));
            AdmissionDecision decision = await pending;
            Assert.That(decision.ReasonWire, Is.EqualTo("approval_timeout"));
            Assert.That(decision.Detail, Does.Contain("session claim"));
            Assert.That(decision.ElapsedMs, Is.EqualTo(10000));
            Assert.That(pipeline.Ledger.Count, Is.EqualTo(0));
            await WaitFor(() => evidence.SawCancellation, "the claim's token was cancelled");
        }

        [Test]
        public async Task AConnectionThatClosesDuringTheClaimIsRefusedAndHoldsNoSeat()
        {
            var (pipeline, evidence, _, _) = Make();
            evidence.Claim = async c =>
            {
                await Task.Delay(Timeout.Infinite, c);
                return SessionClaimResult.Claimed(SelfId);
            };
            using (var closed = new CancellationTokenSource())
            {
                Task<AdmissionDecision> pending = pipeline.AdmitAsync(4, Encode(Reservation()), closed.Token);
                await WaitFor(() => evidence.Claims == 1, "the claim to start");
                closed.Cancel();
                AdmissionDecision decision = await pending;
                Assert.That(decision.ReasonWire, Is.EqualTo("approval_timeout"));
                Assert.That(decision.Detail, Does.Contain("cancelled"));
                Assert.That(pipeline.Ledger.Count, Is.EqualTo(0));
            }
        }

        private static async Task WaitFor(Func<bool> condition, string what)
        {
            for (int i = 0; i < 200 && !condition(); i++)
            {
                await Task.Delay(10);
            }

            Assert.That(condition(), Is.True, what);
        }
    }
}
