using BeaconRush.Admission;
using BeaconRush.Hosting;
using BeaconRush.Networking;
using BeaconRush.Session;
using NUnit.Framework;
using PingCore.Core.Handshake;

namespace BeaconRush.Tests.Editor
{
    /// <summary>Beacon Rush's own gate, row by row: a platform allocation that beat a solo join's claim, the session (with the solo join on an idle hosted game server), then the results phase, then a reservation into a roster lobby, then seats.</summary>
    public sealed class BeaconRushAdmissionGateTests
    {
        private static readonly JoinTicket Reservation = JoinTicket.ForReservation("res-1", "anon:p1", BeaconRushProtocol.Version, "Ada");
        private static readonly JoinTicket Match = JoinTicket.ForMatch("ticket-secret-1", "alloc-1", "anon:p2", BeaconRushProtocol.Version);
        private static readonly JoinTicket Lan = JoinTicket.ForLan("lan-1", BeaconRushProtocol.Version);

        [TestCase(GameHostingMode.Hosted, false, SessionPhase.Lobby, 0, "not_in_session", Description = "an idle hosted game server takes no reservation join")]
        [TestCase(GameHostingMode.Hosted, true, SessionPhase.Lobby, 0, null, Description = "an allocated lobby takes a late reservation join")]
        [TestCase(GameHostingMode.Hosted, true, SessionPhase.Match, 7, null, Description = "the eighth seat is free")]
        [TestCase(GameHostingMode.Hosted, true, SessionPhase.Match, 8, "server_full", Description = "eight seats taken")]
        [TestCase(GameHostingMode.Hosted, true, SessionPhase.Match, 12, "server_full", Description = "more than eight held (never expected) is still full")]
        [TestCase(GameHostingMode.Hosted, true, SessionPhase.Results, 0, "refused_by_game", Description = "results: the session is about to end")]
        [TestCase(GameHostingMode.Hosted, false, SessionPhase.Results, 8, "not_in_session", Description = "no session wins over every later row")]
        [TestCase(GameHostingMode.Hosted, true, SessionPhase.Results, 8, "refused_by_game", Description = "results wins over full")]
        [TestCase(GameHostingMode.SelfHosted, true, SessionPhase.Lobby, 3, null, Description = "a self-hosted local session")]
        [TestCase(GameHostingMode.SelfHosted, false, SessionPhase.Lobby, 0, "not_in_session", Description = "between two local sessions")]
        [TestCase(GameHostingMode.Listen, true, SessionPhase.Match, 8, "server_full", Description = "a listen host counts its own seat in the caller's seat count")]
        [TestCase(GameHostingMode.Local, true, SessionPhase.Match, 1, null, Description = "unlisted")]
        public void TheGateFollowsItsTable(GameHostingMode mode, bool open, SessionPhase phase, int seats, string reason)
        {
            AdmissionGateResult result = BeaconRushAdmissionGate.Decide(Reservation, new GateState(mode, open, phase, seats));
            Assert.That(result.Admitted, Is.EqualTo(reason == null));
            Assert.That(result.Admitted ? null : JoinRejectReasons.ToWire(result.Reason), Is.EqualTo(reason));
        }

        [Test]
        public void EveryKindMeetsTheSameRows()
        {
            var full = new GateState(GameHostingMode.Hosted, true, SessionPhase.Match, BeaconRushProtocol.MaxPlayers);
            var idle = new GateState(GameHostingMode.Hosted, false, SessionPhase.Lobby, 0);
            foreach (JoinTicket ticket in new[] { Reservation, Match, Lan })
            {
                Assert.That(BeaconRushAdmissionGate.Decide(ticket, full).Reason, Is.EqualTo(JoinRejectReason.ServerFull), ticket.ToString());
                Assert.That(BeaconRushAdmissionGate.Decide(ticket, idle).Reason, Is.EqualTo(JoinRejectReason.NotInSession), ticket.ToString());
            }
        }

        [Test]
        public void TheInstanceAsksForTheStateOnEveryTicket()
        {
            var state = new GateState(GameHostingMode.Hosted, false, SessionPhase.Lobby, 0);
            var gate = new BeaconRushAdmissionGate(() => state);
            Assert.That(gate.CanAdmit(Reservation, InSession("alloc-1")).Admitted, Is.False);
            state = new GateState(GameHostingMode.Hosted, true, SessionPhase.Lobby, 0);
            Assert.That(gate.CanAdmit(Reservation, InSession("alloc-1")).Admitted, Is.True, "a later state is read, not a cached one");
        }

        [Test]
        public void AnAdmittedTicketsDisplayNameIsKeptOnceAndARefusedOneIsNot()
        {
            var state = new GateState(GameHostingMode.Hosted, true, SessionPhase.Lobby, 0);
            var gate = new BeaconRushAdmissionGate(() => state);
            gate.CanAdmit(Reservation, new AdmissionFacts());
            Assert.That(gate.TakeDisplayName("anon:p1"), Is.EqualTo("Ada"));
            Assert.That(gate.TakeDisplayName("anon:p1"), Is.Null, "taken once");

            state = new GateState(GameHostingMode.Hosted, true, SessionPhase.Lobby, BeaconRushProtocol.MaxPlayers);
            gate.CanAdmit(Reservation, new AdmissionFacts());
            Assert.That(gate.TakeDisplayName("anon:p1"), Is.Null, "a refused ticket leaves nothing behind");
            Assert.That(gate.TakeDisplayName(null), Is.Null);
        }

        private static AdmissionFacts Idle(HostingMode mode = HostingMode.Hosted) => new AdmissionFacts
        {
            Mode = mode, Reservation = ReservationEvidence.HoldFound, ReservationSeats = 1, ClaimIdleSessions = true,
        };

        private static AdmissionFacts IdleNotOptedIn() => new AdmissionFacts { Mode = HostingMode.Hosted, Reservation = ReservationEvidence.HoldFound, ReservationSeats = 1 };

        private static AdmissionFacts InSession(string allocationId) => new AdmissionFacts { Mode = HostingMode.Hosted, CurrentAllocationId = allocationId };

        private static AdmissionFacts AllocatedMeanwhile() => new AdmissionFacts
        {
            Mode = HostingMode.Hosted, CurrentAllocationId = "alloc-match", SessionClaim = SessionClaimOutcome.AllocatedMeanwhile,
        };

        private static System.Collections.Generic.IEnumerable<TestCaseData> SoloRows()
        {
            var idle = new GateState(GameHostingMode.Hosted, false, SessionPhase.Lobby, 0);
            var open = new GateState(GameHostingMode.Hosted, true, SessionPhase.Lobby, 1);
            yield return new TestCaseData(Reservation, idle, Idle(), null).SetName("solo join: a reservation on an idle hosted game server is admitted (the pipeline claims its session)");
            yield return new TestCaseData(Reservation, new GateState(GameHostingMode.Hosted, false, SessionPhase.Lobby, 8), Idle(), "server_full").SetName("solo join: seats still count");
            yield return new TestCaseData(Reservation, idle, InSession("alloc-1"), "not_in_session").SetName("an allocation is current but its session is not open here: refused");
            yield return new TestCaseData(Match, idle, Idle(), "not_in_session").SetName("a match ticket on an idle game server is never a solo join");
            yield return new TestCaseData(Reservation, idle, Idle(HostingMode.SelfHosted), "not_in_session").SetName("only a hosted game server claims a session");
            yield return new TestCaseData(Reservation, idle, IdleNotOptedIn(), "not_in_session").SetName("without the claim opt-in an idle reservation is refused as before");
            yield return new TestCaseData(Reservation, idle, AllocatedMeanwhile(), "refused_by_game").SetName("the platform allocated meanwhile: refused, the client quick-joins again");
            yield return new TestCaseData(Reservation, open, AllocatedMeanwhile(), "refused_by_game").SetName("the platform allocated meanwhile, its session already open: still refused");
            yield return new TestCaseData(Reservation, open, InSession("self-1791200000000"), null).SetName("a second quick-play player joins the open self-allocated session");
            yield return new TestCaseData(Reservation, open, InSession("alloc-1"), null).SetName("a late reservation into an open allocated session (unchanged)");
        }

        private static readonly JoinTicket Backfill = JoinTicket.ForBackfill("ticket-secret-2", "bf-1", "anon:p3", BeaconRushProtocol.Version);

        private static System.Collections.Generic.IEnumerable<TestCaseData> RosterLobbyRows()
        {
            var rosterLobby = new GateState(GameHostingMode.Hosted, true, SessionPhase.Lobby, 1, sessionHasRoster: true);
            var rosterMatch = new GateState(GameHostingMode.Hosted, true, SessionPhase.Match, 2, sessionHasRoster: true);
            var rosterlessLobby = new GateState(GameHostingMode.Hosted, true, SessionPhase.Lobby, 1, sessionHasRoster: false);
            yield return new TestCaseData(Reservation, rosterLobby, "refused_by_game").SetName("a reservation into the lobby of a match waiting for its roster is refused");
            yield return new TestCaseData(Match, rosterLobby, null).SetName("a matched ticket joins its roster lobby");
            yield return new TestCaseData(Backfill, rosterLobby, null).SetName("a backfill ticket is not a reservation and is admitted");
            yield return new TestCaseData(Reservation, rosterMatch, null).SetName("a reservation into a roster session's running match is admitted");
            yield return new TestCaseData(Reservation, rosterlessLobby, null).SetName("a reservation into a rosterless lobby (backend or self-allocated) is admitted");
            yield return new TestCaseData(Reservation, new GateState(GameHostingMode.SelfHosted, true, SessionPhase.Lobby, 1, sessionHasRoster: true), null)
                .SetName("only a hosted game server has roster lobbies");
        }

        [TestCaseSource(nameof(RosterLobbyRows))]
        public void AReservationIsNotSeatedInARosterLobby(JoinTicket ticket, GateState state, string reason)
        {
            // Mutation: drop the roster-lobby row in BeaconRushAdmissionGate.Decide and the first row admits.
            AdmissionGateResult result = BeaconRushAdmissionGate.Decide(ticket, state, InSession("alloc-match"));
            Assert.That(result.Admitted ? null : JoinRejectReasons.ToWire(result.Reason), Is.EqualTo(reason), result.Detail);
        }

        [TestCaseSource(nameof(SoloRows))]
        public void TheSoloJoinRowsFollowTheTable(JoinTicket ticket, GateState state, AdmissionFacts facts, string reason)
        {
            AdmissionGateResult result = BeaconRushAdmissionGate.Decide(ticket, state, facts);
            Assert.That(result.Admitted ? null : JoinRejectReasons.ToWire(result.Reason), Is.EqualTo(reason), result.Detail);
        }

        [Test]
        public void TheInstanceHandsTheFactsToTheTable()
        {
            var gate = new BeaconRushAdmissionGate(() => new GateState(GameHostingMode.Hosted, false, SessionPhase.Lobby, 0));
            Assert.That(gate.CanAdmit(Reservation, Idle()).Admitted, Is.True, "solo join");
            Assert.That(gate.CanAdmit(Reservation, AllocatedMeanwhile()).Reason, Is.EqualTo(JoinRejectReason.RefusedByGame));
            Assert.That(BeaconRushAdmissionGate.IsSoloJoin(Reservation, new GateState(GameHostingMode.Hosted, false, SessionPhase.Lobby, 0), null), Is.False, "no facts, no solo join");
        }
    }
}
