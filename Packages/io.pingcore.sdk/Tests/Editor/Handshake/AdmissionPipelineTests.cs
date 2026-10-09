using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Core.Handshake;
using static PingCore.Sdk.Tests.Editor.Handshake.HandshakeTestData;

namespace PingCore.Sdk.Tests.Editor.Handshake
{
    /// <summary>
    /// One connection end to end, engine-neutral: decode, protocol and stopping before any evidence, the
    /// evidence raced against the deadline, the table, the game's gate and the ledger hold.
    /// </summary>
    public sealed class AdmissionPipelineTests
    {
        private sealed class FakeEvidence : IAdmissionEvidence
        {
            public Func<JoinTicket, AdmissionFacts, CancellationToken, Task> Gather { get; set; } = (t, f, c) => Task.CompletedTask;

            public int Calls { get; private set; }

            public bool Stopping { get; set; }

            public bool SawCancellation { get; private set; }

            public string Source => "fake";

            public bool IsStopping => Stopping;

            public async Task GatherAsync(JoinTicket ticket, AdmissionFacts facts, ApprovalOptions options, CancellationToken cancellationToken)
            {
                Calls++;
                try
                {
                    await Gather(ticket, facts, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    SawCancellation = true;
                    throw;
                }
            }
        }

        private sealed class FakeGate : IAdmissionGate
        {
            public Func<JoinTicket, AdmissionFacts, AdmissionGateResult> Answer { get; set; } = (t, f) => AdmissionGateResult.Admit;

            public int Calls { get; private set; }

            public AdmissionGateResult CanAdmit(JoinTicket ticket, AdmissionFacts facts)
            {
                Calls++;
                return Answer(ticket, facts);
            }
        }

        private static ApprovalOptions Options(HostingMode mode = HostingMode.Listen, bool lanOnly = true) =>
            new ApprovalOptions { ProtocolVersion = Protocol, Mode = mode, LanOnly = lanOnly };

        private static byte[] Encode(JoinTicket ticket) => JoinTicketCodec.Encode(ticket);

        private static (AdmissionPipeline Pipeline, FakeEvidence Evidence, FakeGate Gate, ManualScheduler Clock) Make(ApprovalOptions options = null)
        {
            var evidence = new FakeEvidence();
            var gate = new FakeGate();
            var clock = new ManualScheduler();
            return (new AdmissionPipeline(options ?? Options(), evidence, gate, clock), evidence, gate, clock);
        }

        private static IEnumerable<TestCaseData> Undecodable()
        {
            yield return new TestCaseData(new byte[0], "payload_empty").SetName("no payload never reaches the evidence");
            yield return new TestCaseData(Encoding.UTF8.GetBytes("not json"), "payload_malformed").SetName("garbage never reaches the evidence");
            yield return new TestCaseData(Encoding.UTF8.GetBytes("{\"v\":1}"), "payload_invalid").SetName("a schema break never reaches the evidence");
            yield return new TestCaseData(Encoding.UTF8.GetBytes("{\"v\":2}"), "unsupported_version").SetName("a later version never reaches the evidence");
            yield return new TestCaseData(Encoding.UTF8.GetBytes(new string(' ', 1025)), "payload_too_large").SetName("1025 bytes never reach the evidence");
        }

        [TestCaseSource(nameof(Undecodable))]
        public async Task APayloadThatDoesNotDecodeIsRejectedBeforeAnyEvidence(byte[] payload, string expected)
        {
            var (pipeline, evidence, gate, _) = Make();
            AdmissionDecision decision = await pipeline.AdmitAsync(5, payload, CancellationToken.None);
            Assert.That(decision.ReasonWire, Is.EqualTo(expected), decision.Detail);
            Assert.That(decision.Kind, Is.Null);
            Assert.That(decision.ConnectionId, Is.EqualTo(5));
            Assert.That(evidence.Calls, Is.EqualTo(0));
            Assert.That(gate.Calls, Is.EqualTo(0));
            Assert.That(pipeline.Ledger.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task AProtocolMismatchOrAStoppingGameServerNeverReachesTheEvidence()
        {
            var (pipeline, evidence, _, _) = Make();
            AdmissionDecision mismatch = await pipeline.AdmitAsync(1, Encode(JoinTicket.ForLan("lan-1", Protocol + 1)), CancellationToken.None);
            Assert.That(mismatch.ReasonWire, Is.EqualTo("protocol_mismatch"));

            evidence.Stopping = true;
            AdmissionDecision byEvidence = await pipeline.AdmitAsync(2, Encode(Lan()), CancellationToken.None);
            Assert.That(byEvidence.ReasonWire, Is.EqualTo("stopping"));
            evidence.Stopping = false;

            pipeline.NotifyStopping();
            AdmissionDecision byNotice = await pipeline.AdmitAsync(3, Encode(Lan()), CancellationToken.None);
            Assert.That(byNotice.ReasonWire, Is.EqualTo("stopping"));
            Assert.That(evidence.Calls, Is.EqualTo(0));
        }

        [Test]
        public async Task AStopThatArrivesWhileTheEvidenceIsGatheredStillRefuses()
        {
            var (pipeline, evidence, _, _) = Make();
            evidence.Gather = (t, f, c) =>
            {
                pipeline.NotifyStopping();
                return Task.CompletedTask;
            };
            AdmissionDecision decision = await pipeline.AdmitAsync(1, Encode(Lan()), CancellationToken.None);
            Assert.That(decision.ReasonWire, Is.EqualTo("stopping"));
            Assert.That(pipeline.Ledger.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task AnAcceptedConnectionHoldsItsSeatUnderItsConnectionId()
        {
            var (pipeline, evidence, gate, _) = Make();
            AdmissionDecision decision = await pipeline.AdmitAsync(42, Encode(Lan("lan-a")), CancellationToken.None);
            Assert.That(decision.Approved, Is.True, decision.Detail);
            Assert.That(decision.ConnectionId, Is.EqualTo(42));
            Assert.That(decision.EvidenceSource, Is.EqualTo("fake"));
            Assert.That(evidence.Calls, Is.EqualTo(1));
            Assert.That(gate.Calls, Is.EqualTo(1));
            Assert.That(pipeline.Ledger.Holds(42), Is.True);
            Assert.That(pipeline.Ledger.IsPlayerAdmitted("lan-a"), Is.True);

            AdmissionDecision duplicate = await pipeline.AdmitAsync(43, Encode(Lan("lan-a")), CancellationToken.None);
            Assert.That(duplicate.ReasonWire, Is.EqualTo("duplicate_player"));
            Assert.That(pipeline.Release(42), Is.True);
            Assert.That((await pipeline.AdmitAsync(44, Encode(Lan("lan-a")), CancellationToken.None)).Approved, Is.True);
        }

        private static IEnumerable<TestCaseData> GateAnswers()
        {
            yield return new TestCaseData(JoinRejectReason.NotInSession, "not_in_session").SetName("the gate's not_in_session reaches the client");
            yield return new TestCaseData(JoinRejectReason.ServerFull, "server_full").SetName("the gate's server_full reaches the client");
            yield return new TestCaseData(JoinRejectReason.RefusedByGame, "refused_by_game").SetName("the gate's refused_by_game reaches the client");
            yield return new TestCaseData(JoinRejectReason.RosterFull, "refused_by_game").SetName("a gate reason outside its three becomes refused_by_game");
        }

        [TestCaseSource(nameof(GateAnswers))]
        public async Task TheGateDecidesAfterTheTableAndARefusalHoldsNoSeat(JoinRejectReason answer, string expected)
        {
            var (pipeline, _, gate, _) = Make();
            gate.Answer = (t, f) => AdmissionGateResult.Reject(answer, "no");
            AdmissionDecision decision = await pipeline.AdmitAsync(1, Encode(Lan()), CancellationToken.None);
            Assert.That(decision.ReasonWire, Is.EqualTo(expected));
            Assert.That(decision.Kind, Is.EqualTo(JoinTicketKind.Lan));
            Assert.That(pipeline.Ledger.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task TheGateIsNotAskedWhenTheTableRefuses()
        {
            var (pipeline, _, gate, _) = Make();
            AdmissionDecision decision = await pipeline.AdmitAsync(1, Encode(Reservation()), CancellationToken.None);
            Assert.That(decision.ReasonWire, Is.EqualTo("kind_not_accepted"));
            Assert.That(gate.Calls, Is.EqualTo(0));
        }

        [Test]
        public async Task AThrowingGateOrEvidenceIsRefusedByGameAndNeverApproves()
        {
            var (pipeline, evidence, gate, _) = Make();
            gate.Answer = (t, f) => throw new InvalidOperationException("boom");
            Assert.That((await pipeline.AdmitAsync(1, Encode(Lan()), CancellationToken.None)).ReasonWire, Is.EqualTo("refused_by_game"));

            gate.Answer = (t, f) => AdmissionGateResult.Admit;
            evidence.Gather = (t, f, c) => throw new InvalidOperationException("boom");
            AdmissionDecision decision = await pipeline.AdmitAsync(2, Encode(Lan()), CancellationToken.None);
            Assert.That(decision.ReasonWire, Is.EqualTo("refused_by_game"));
            Assert.That(decision.Detail, Does.Contain("InvalidOperationException"));
            Assert.That(pipeline.Ledger.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task EvidenceThatOutlivesTheDeadlineIsApprovalTimeoutAndIsCancelled()
        {
            var (pipeline, evidence, gate, clock) = Make();
            evidence.Gather = (t, f, c) => Task.Delay(Timeout.Infinite, c);
            Task<AdmissionDecision> pending = pipeline.AdmitAsync(9, Encode(Lan()), CancellationToken.None);
            Assert.That(clock.PendingDelays, Is.EqualTo(1), "the deadline is armed on the scheduler");

            clock.Advance(TimeSpan.FromSeconds(9.9));
            await Task.Delay(50);
            Assert.That(pending.IsCompleted, Is.False, "decided before the deadline");

            clock.Advance(TimeSpan.FromSeconds(0.1));
            AdmissionDecision decision = await pending;
            Assert.That(decision.ReasonWire, Is.EqualTo("approval_timeout"));
            Assert.That(decision.ElapsedMs, Is.EqualTo(10000));
            Assert.That(gate.Calls, Is.EqualTo(0));
            Assert.That(pipeline.Ledger.Count, Is.EqualTo(0));
            await WaitFor(() => evidence.SawCancellation, "the evidence's token was cancelled");
        }

        [Test]
        public async Task EvidenceThatFinishesBeforeTheDeadlineDisarmsIt()
        {
            var (pipeline, evidence, _, clock) = Make();
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            evidence.Gather = (t, f, c) => release.Task;
            Task<AdmissionDecision> pending = pipeline.AdmitAsync(9, Encode(Lan()), CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(3));
            release.SetResult(true);
            AdmissionDecision decision = await pending;
            Assert.That(decision.Approved, Is.True, decision.Detail);
            Assert.That(decision.ElapsedMs, Is.EqualTo(3000));
            Assert.That(clock.PendingDelays, Is.EqualTo(0), "the deadline delay was cancelled");
        }

        [Test]
        public async Task ACancelledConnectionIsRefusedAndHoldsNoSeat()
        {
            var (pipeline, evidence, _, _) = Make();
            evidence.Gather = (t, f, c) => Task.Delay(Timeout.Infinite, c);
            using (var closed = new CancellationTokenSource())
            {
                Task<AdmissionDecision> pending = pipeline.AdmitAsync(3, Encode(Lan()), closed.Token);
                closed.Cancel();
                AdmissionDecision decision = await pending;
                Assert.That(decision.Approved, Is.False);
                Assert.That(decision.Detail, Does.Contain("cancelled"));
                Assert.That(pipeline.Ledger.Count, Is.EqualTo(0));
            }
        }

        [Test]
        public async Task TwoPendingJoinsForTheLastSeatOfAPartyCannotBothGetIn()
        {
            var (pipeline, evidence, _, _) = Make(Options(HostingMode.Hosted, false));
            var gathered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            evidence.Gather = async (t, f, c) =>
            {
                await gathered.Task;
                f.CurrentAllocationId = Allocation;
                f.Roster = Roster(new RosterEntry(TicketId, 2, PlayerId));
            };
            pipeline.Ledger.Hold(100, Match(playerId: "anon:first"));
            Task<AdmissionDecision> a = pipeline.AdmitAsync(1, Encode(Match(playerId: "anon:a")), CancellationToken.None);
            Task<AdmissionDecision> b = pipeline.AdmitAsync(2, Encode(Match(playerId: "anon:b")), CancellationToken.None);
            gathered.SetResult(true);
            AdmissionDecision[] both = await Task.WhenAll(a, b);
            Assert.That(both.Count(d => d.Approved), Is.EqualTo(1), string.Join(", ", both.Select(d => d.ToString())));
            Assert.That(both.Single(d => !d.Approved).ReasonWire, Is.EqualTo("roster_full"));
            Assert.That(pipeline.Ledger.AdmittedForTicket(Allocation, TicketId), Is.EqualTo(2));
        }

        [Test]
        public async Task EveryRejectLiteralIsReachedByARealCase()
        {
            var reached = new Dictionary<string, string>();
            void Note(AdmissionDecision d, string why)
            {
                Assert.That(d.Approved, Is.False, why);
                reached[d.ReasonWire] = why;
            }

            var (pipeline, evidence, gate, clock) = Make();
            foreach (TestCaseData row in Undecodable())
            {
                Note(await pipeline.AdmitAsync(1, (byte[])row.Arguments[0], CancellationToken.None), "decode");
            }

            foreach (TestCaseData row in GateAnswers())
            {
                JoinRejectReason answer = (JoinRejectReason)row.Arguments[0];
                gate.Answer = (t, f) => AdmissionGateResult.Reject(answer);
                Note(await pipeline.AdmitAsync(1, Encode(Lan()), CancellationToken.None), "gate");
            }

            evidence.Gather = (t, f, c) => Task.Delay(Timeout.Infinite, c);
            Task<AdmissionDecision> late = pipeline.AdmitAsync(1, Encode(Lan()), CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(10));
            Note(await late, "deadline");

            var table = new (JoinTicket Ticket, AdmissionFacts Facts)[]
            {
                (JoinTicket.ForLan("lan-1", Protocol + 1), Facts(HostingMode.Listen, true)),
                (Lan(), Facts(HostingMode.Listen, true).With(f => f.Stopping = true)),
                (Lan(), Facts(HostingMode.Hosted)),
                (Match(allocationId: "x"), HostedSession()),
                (Match(ticketId: "x"), HostedSession()),
                (Match(), HostedSession(party: 1).With(f => f.AdmittedForTicket = 1)),
                (Match(), HostedSession().With(f => f.PlayerAlreadyAdmitted = true)),
                (BackfillTicket(), HostedBackfill().With(f => f.BackfillDelivered = false)),
                (Reservation(), Facts(HostingMode.Hosted).With(f => f.Reservation = ReservationEvidence.HoldNotFound)),
                (Reservation(), Facts(HostingMode.Hosted).With(f => f.Reservation = ReservationEvidence.HoldUnreachable)),
            };
            foreach ((JoinTicket ticket, AdmissionFacts facts) in table)
            {
                Note(JoinAdmission.Decide(ticket, facts), "table");
            }

            Assert.That(reached.Keys, Is.EquivalentTo(JoinRejectReasonTableTests.Documented));
        }

        [Test]
        public void ThePipelineRefusesAMissingPartOrANonPositiveDeadline()
        {
            var clock = new ManualScheduler();
            Assert.Throws<ArgumentNullException>(() => new AdmissionPipeline(null, new LanAdmissionEvidence(), null, clock));
            Assert.Throws<ArgumentNullException>(() => new AdmissionPipeline(Options(), null, null, clock));
            Assert.Throws<ArgumentNullException>(() => new AdmissionPipeline(Options(), new LanAdmissionEvidence(), null, null));
            Assert.Throws<ArgumentException>(() => new AdmissionPipeline(new ApprovalOptions { Deadline = TimeSpan.Zero }, new LanAdmissionEvidence(), null, clock));
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
