using System;
using System.Collections.Generic;
using NUnit.Framework;
using PingCore.Core.Handshake;
using static PingCore.Sdk.Tests.Editor.Handshake.HandshakeTestData;

namespace PingCore.Sdk.Tests.Editor.Handshake
{
    /// <summary>
    /// The connect decision table (<see cref="JoinAdmission"/>, published at
    /// https://pingcore.io/docs/fleets/admitting-players), cell by cell. Each case names the
    /// mode, the kind and the facts; every rejection is driven by facts that really trigger it, and
    /// each accept has a sibling case that changes one fact and is rejected, so no row passes vacuously.
    /// </summary>
    public sealed class JoinAdmissionTableTests
    {
        private const string Accept = null;

        private static TestCaseData Row(string name, JoinTicket ticket, AdmissionFacts facts, string expected) =>
            new TestCaseData(ticket, facts, expected).SetName(name);

        // ---- the checks before every cell: protocol, then stopping -------------------------------------

        private static IEnumerable<TestCaseData> Prechecks()
        {
            foreach (HostingMode mode in new[] { HostingMode.Hosted, HostingMode.SelfHosted, HostingMode.Listen })
            {
                foreach ((string kind, JoinTicket other) in new[]
                {
                    ("reservation", JoinTicket.ForReservation("res-1", PlayerId, Protocol + 1)),
                    ("match", JoinTicket.ForMatch(TicketId, Allocation, PlayerId, Protocol + 1)),
                    ("backfill", JoinTicket.ForBackfill(TicketId, Backfill, PlayerId, Protocol + 1)),
                    ("lan", JoinTicket.ForLan("lan-1", Protocol + 1)),
                })
                {
                    yield return Row(mode + " " + kind + " of another protocol version is protocol_mismatch, even while stopping",
                        other, Facts(mode).With(f => f.Stopping = true), "protocol_mismatch");
                }
            }

            yield return Row("Hosted match into its own session while stopping is stopping", Match(), HostedSession().With(f => f.Stopping = true), "stopping");
            yield return Row("Hosted reservation with a live hold while stopping is stopping", Reservation(), HostedHold(2).With(f => f.Stopping = true), "stopping");
            yield return Row("SelfHosted valid reservation while stopping is stopping", Reservation(),
                Verified(HostingMode.SelfHosted, ReservationEvidence.VerifyValid).With(f => f.Stopping = true), "stopping");
            yield return Row("Listen LAN-only lan ticket while stopping is stopping", Lan(),
                Facts(HostingMode.Listen, lanOnly: true).With(f => f.Stopping = true), "stopping");
        }

        // ---- PingCore-hosted, reservation --------------------------------------------------------------

        private static IEnumerable<TestCaseData> HostedReservation()
        {
            yield return Row("Hosted reservation: a hold naming the player is accepted", Reservation(), HostedHold(2, PlayerId, "anon:p2"), Accept);
            yield return Row("Hosted reservation: a hold naming other players is reservation_invalid", Reservation(), HostedHold(2, "anon:p2", "anon:p3"), "reservation_invalid");
            yield return Row("Hosted reservation: an open hold below its seats is accepted", Reservation(), HostedHold(2).With(f => f.AdmittedForReservation = 1), Accept);
            yield return Row("Hosted reservation: an open hold with every seat held is roster_full", Reservation(), HostedHold(2).With(f => f.AdmittedForReservation = 2), "roster_full");
            yield return Row("Hosted reservation: an open hold with no seat count fails closed as roster_full", Reservation(), HostedHold(null), "roster_full");
            yield return Row("Hosted reservation: a named hold ignores the open-seat count", Reservation(), HostedHold(1, PlayerId).With(f => f.AdmittedForReservation = 1), Accept);
            yield return Row("Hosted reservation: NotFound is reservation_invalid", Reservation(), Facts(HostingMode.Hosted).With(f => f.Reservation = ReservationEvidence.HoldNotFound), "reservation_invalid");
            yield return Row("Hosted reservation: Expired is reservation_invalid", Reservation(), Facts(HostingMode.Hosted).With(f => f.Reservation = ReservationEvidence.HoldExpired), "reservation_invalid");
            yield return Row("Hosted reservation: Unreachable is reservation_unverifiable", Reservation(), Facts(HostingMode.Hosted).With(f => f.Reservation = ReservationEvidence.HoldUnreachable), "reservation_unverifiable");
            yield return Row("Hosted reservation: Error is reservation_unverifiable", Reservation(), Facts(HostingMode.Hosted).With(f => f.Reservation = ReservationEvidence.HoldError), "reservation_unverifiable");
            yield return Row("Hosted reservation: no evidence gathered fails closed as reservation_unverifiable", Reservation(), Facts(HostingMode.Hosted), "reservation_unverifiable");
            yield return Row("Hosted reservation: a verify verdict is the wrong evidence and fails closed", Reservation(),
                Verified(HostingMode.Hosted, ReservationEvidence.VerifyValid), "reservation_unverifiable");
            yield return Row("Hosted reservation: a second connection of one player on a named hold is duplicate_player", Reservation(),
                HostedHold(2, PlayerId).With(f => f.PlayerAlreadyAdmitted = true), "duplicate_player");
            yield return Row("Hosted reservation: a second connection of one player on an open hold with seats left is duplicate_player", Reservation(),
                HostedHold(4).With(f => { f.PlayerAlreadyAdmitted = true; f.AdmittedForReservation = 1; }), "duplicate_player");
            yield return Row("Hosted reservation: the party example's player on a hold naming both members is accepted",
                JoinTicketCodec.Decode(Example("reservation.party")).Ticket,
                HostedHold(2, "anon:2f1c6b6e-7d0a-4a52-9f3e-1c4c0b8e9a10", "anon:7c4e2a90-5b1d-4f6e-9a3c-8d2b1e0f4a67"), Accept);
        }

        // ---- PingCore-hosted, match --------------------------------------------------------------------

        private static IEnumerable<TestCaseData> HostedMatch()
        {
            yield return Row("Hosted match: the submitter into its own session is accepted", Match(), HostedSession(), Accept);
            yield return Row("Hosted match: no current allocation is allocation_mismatch", Match(), HostedSession().With(f => f.CurrentAllocationId = null), "allocation_mismatch");
            yield return Row("Hosted match: another allocation is allocation_mismatch", Match(allocationId: "alloc-2"), HostedSession(), "allocation_mismatch");
            yield return Row("Hosted match: a ticket missing from the roster is not_in_roster", Match(ticketId: "forged-ticket"), HostedSession(), "not_in_roster");
            yield return Row("Hosted match: no roster at all is not_in_roster", Match(), HostedSession().With(f => f.Roster = null), "not_in_roster");
            yield return Row("Hosted match: a party member with their own player id joins on the ticket id alone", Match(playerId: "anon:friend"), HostedSession(party: 3), Accept);
            yield return Row("Hosted match: a backend ticket (roster player null) admits any player", Match(playerId: "anon:any"), HostedSession(rosterPlayer: null), Accept);
            yield return Row("Hosted match: a ticket below its party size is accepted", Match(playerId: "anon:third"), HostedSession(party: 3).With(f => f.AdmittedForTicket = 2), Accept);
            yield return Row("Hosted match: a ticket that brought its whole party is roster_full", Match(playerId: "anon:fourth"), HostedSession(party: 3).With(f => f.AdmittedForTicket = 3), "roster_full");
            yield return Row("Hosted match: the submitter is first come, not reserved, so it too is roster_full when the party is in", Match(), HostedSession(party: 2).With(f => f.AdmittedForTicket = 2), "roster_full");
            yield return Row("Hosted match: a party member repeating a player id already in is duplicate_player", Match(), HostedSession(party: 3).With(f => { f.AdmittedForTicket = 1; f.PlayerAlreadyAdmitted = true; }), "duplicate_player");
            yield return Row("Hosted match: duplicate_player is checked before the party size", Match(), HostedSession(party: 1).With(f => { f.AdmittedForTicket = 1; f.PlayerAlreadyAdmitted = true; }), "duplicate_player");
        }

        // ---- PingCore-hosted, match into a rosterless allocation (a backend or self-allocation) ----------

        private static IEnumerable<TestCaseData> HostedRosterlessMatch()
        {
            yield return Row("Hosted rosterless match: a ticket for the current allocation is accepted", Match(), RosterlessSession(4), Accept);
            yield return Row("Hosted rosterless match: the ticket id is ignored, so one in no roster is accepted", Match(ticketId: "forged-ticket"), RosterlessSession(4), Accept);
            yield return Row("Hosted rosterless match: an empty roster list is still rosterless", Match(), RosterlessSession(4).With(f => f.Roster = Roster()), Accept);
            yield return Row("Hosted rosterless match: another allocation is allocation_mismatch", Match(allocationId: "self-999"), RosterlessSession(4), "allocation_mismatch");
            yield return Row("Hosted rosterless match: no current allocation is allocation_mismatch", Match(), RosterlessSession(4).With(f => f.CurrentAllocationId = null), "allocation_mismatch");
            yield return Row("Hosted rosterless match: a player already in is duplicate_player", Match(), RosterlessSession(4).With(f => { f.AdmittedForAllocation = 1; f.PlayerAlreadyAdmitted = true; }), "duplicate_player");
            yield return Row("Hosted rosterless match: duplicate_player is checked before the cap", Match(), RosterlessSession(2).With(f => { f.AdmittedForAllocation = 2; f.PlayerAlreadyAdmitted = true; }), "duplicate_player");
            yield return Row("Hosted rosterless match: below max players is accepted", Match(), RosterlessSession(4).With(f => f.AdmittedForAllocation = 3), Accept);
            yield return Row("Hosted rosterless match: at max players is roster_full", Match(), RosterlessSession(4).With(f => f.AdmittedForAllocation = 4), "roster_full");
            yield return Row("Hosted rosterless match: a max players of 0 is no limit", Match(), RosterlessSession(0).With(f => f.AdmittedForAllocation = 500), Accept);
            yield return Row("Hosted rosterless match: the per-ticket count plays no part", Match(), RosterlessSession(4).With(f => f.AdmittedForTicket = 9), Accept);
            yield return Row("Hosted rosterless match: a rosterless flag beside a real roster keeps the roster rules", Match(ticketId: "forged-ticket"),
                HostedSession().With(f => { f.RosterlessAllocation = true; f.MaxPlayers = 4; }), "not_in_roster");
            yield return Row("Hosted match with a roster ignores the allocation-wide count and the cap", Match(),
                HostedSession().With(f => { f.AdmittedForAllocation = 50; f.MaxPlayers = 1; }), Accept);
            yield return Row("Hosted backfill into a rosterless session still needs its backfill roster", BackfillTicket(),
                HostedBackfill().With(f => { f.RosterlessAllocation = true; f.Roster = null; }), "not_in_roster");
            yield return Row("Hosted backfill: a rosterless allocation never stands in for an undelivered backfill", BackfillTicket(),
                RosterlessSession(4).With(f => f.BackfillDelivered = false), "backfill_unknown");
            yield return Row("Self-hosted match into a rosterless allocation is kind_not_accepted", Match(), RosterlessSession(4).With(f => f.Mode = HostingMode.SelfHosted), "kind_not_accepted");

            // A supervisor self-allocation's id is the clock in milliseconds: guessable, so never rosterless by default.
            // Mutation: drop the SelfAllocation check in HostedMatch and the first row is accepted.
            yield return Row("Hosted self-allocation: a match join is not_in_roster by default", Match(), RosterlessSession(4).With(f => f.SelfAllocation = true), "not_in_roster");
            yield return Row("Hosted self-allocation: a match join with AllowSelfAllocatedJoins is accepted", Match(),
                RosterlessSession(4).With(f => { f.SelfAllocation = true; f.AllowSelfAllocatedJoins = true; }), Accept);
            yield return Row("Hosted self-allocation: with AllowSelfAllocatedJoins the ticket id is still ignored", Match(ticketId: "forged-ticket"),
                RosterlessSession(4).With(f => { f.SelfAllocation = true; f.AllowSelfAllocatedJoins = true; }), Accept);
            yield return Row("Hosted self-allocation: with AllowSelfAllocatedJoins the player cap still holds", Match(),
                RosterlessSession(4).With(f => { f.SelfAllocation = true; f.AllowSelfAllocatedJoins = true; f.AdmittedForAllocation = 4; }), "roster_full");
            yield return Row("Hosted self-allocation: by default it rejects before the duplicate check", Match(),
                RosterlessSession(4).With(f => { f.SelfAllocation = true; f.PlayerAlreadyAdmitted = true; }), "not_in_roster");
            yield return Row("Hosted self-allocation: AllowSelfAllocatedJoins alone does not admit into another allocation", Match(allocationId: "self-999"),
                RosterlessSession(4).With(f => { f.SelfAllocation = true; f.AllowSelfAllocatedJoins = true; }), "allocation_mismatch");
            yield return Row("Hosted backend rosterless allocation: admitted on its id without the flag", Match(),
                RosterlessSession(4).With(f => { f.SelfAllocation = false; f.AllowSelfAllocatedJoins = false; }), Accept);
            yield return Row("Hosted backend rosterless allocation: the flag changes nothing", Match(ticketId: "forged-ticket"),
                RosterlessSession(4).With(f => f.AllowSelfAllocatedJoins = true), Accept);
            yield return Row("Hosted self-allocation with a roster keeps the roster rules even with the flag", Match(ticketId: "forged-ticket"),
                HostedSession().With(f => { f.SelfAllocation = true; f.AllowSelfAllocatedJoins = true; }), "not_in_roster");
        }

        // ---- PingCore-hosted, backfill -----------------------------------------------------------------

        private static IEnumerable<TestCaseData> HostedBackfillRows()
        {
            yield return Row("Hosted backfill: a delivered backfill into the open session is accepted", BackfillTicket(), HostedBackfill(), Accept);
            yield return Row("Hosted backfill: an undelivered backfill is backfill_unknown", BackfillTicket(), HostedBackfill().With(f => f.BackfillDelivered = false), "backfill_unknown");
            yield return Row("Hosted backfill: evidence for another backfill id is backfill_unknown", BackfillTicket(allocationId: "bf-2"), HostedBackfill(), "backfill_unknown");
            yield return Row("Hosted backfill: a backfill into another session is backfill_unknown", BackfillTicket(), HostedBackfill().With(f => f.BackfillSessionId = "alloc-old"), "backfill_unknown");
            yield return Row("Hosted backfill: no open session is backfill_unknown", BackfillTicket(), HostedBackfill().With(f => f.CurrentAllocationId = null), "backfill_unknown");
            yield return Row("Hosted backfill: a ticket missing from the backfill roster is not_in_roster", BackfillTicket(ticketId: "forged"), HostedBackfill(), "not_in_roster");
            yield return Row("Hosted backfill: a party member joins on the ticket id alone", BackfillTicket(playerId: "anon:friend"), HostedBackfill(party: 2), Accept);
            yield return Row("Hosted backfill: a backfill ticket that brought its party is roster_full", BackfillTicket(playerId: "anon:x"), HostedBackfill(party: 2).With(f => f.AdmittedForTicket = 2), "roster_full");
            yield return Row("Hosted backfill: a player already in is duplicate_player", BackfillTicket(), HostedBackfill(party: 2).With(f => f.PlayerAlreadyAdmitted = true), "duplicate_player");
            yield return Row("Hosted backfill: a match ticket for the backfill id is allocation_mismatch", Match(allocationId: Backfill), HostedBackfill(), "allocation_mismatch");
        }

        // ---- the cells that never accept ---------------------------------------------------------------

        private static IEnumerable<TestCaseData> NotAccepted()
        {
            yield return Row("Hosted lan is kind_not_accepted", Lan(), Facts(HostingMode.Hosted), "kind_not_accepted");
            yield return Row("Hosted lan is kind_not_accepted even with LanOnly set", Lan(), Facts(HostingMode.Hosted, lanOnly: true), "kind_not_accepted");
            yield return Row("SelfHosted match is kind_not_accepted", Match(), HostedSession().With(f => f.Mode = HostingMode.SelfHosted), "kind_not_accepted");
            yield return Row("SelfHosted backfill is kind_not_accepted", BackfillTicket(), HostedBackfill().With(f => f.Mode = HostingMode.SelfHosted), "kind_not_accepted");
            yield return Row("SelfHosted lan is kind_not_accepted", Lan(), Facts(HostingMode.SelfHosted), "kind_not_accepted");
            yield return Row("SelfHosted lan is kind_not_accepted even with LanOnly set", Lan(), Facts(HostingMode.SelfHosted, lanOnly: true), "kind_not_accepted");
            yield return Row("Listen online match is kind_not_accepted", Match(), HostedSession().With(f => f.Mode = HostingMode.Listen), "kind_not_accepted");
            yield return Row("Listen online backfill is kind_not_accepted", BackfillTicket(), HostedBackfill().With(f => f.Mode = HostingMode.Listen), "kind_not_accepted");
            yield return Row("Listen online lan is kind_not_accepted", Lan(), Facts(HostingMode.Listen), "kind_not_accepted");
            yield return Row("Listen LAN-only reservation is kind_not_accepted even with a valid verify", Reservation(),
                Verified(HostingMode.Listen, ReservationEvidence.VerifyValid).With(f => f.LanOnly = true), "kind_not_accepted");
            yield return Row("Listen LAN-only match is kind_not_accepted", Match(), HostedSession().With(f => { f.Mode = HostingMode.Listen; f.LanOnly = true; }), "kind_not_accepted");
            yield return Row("Listen LAN-only backfill is kind_not_accepted", BackfillTicket(), HostedBackfill().With(f => { f.Mode = HostingMode.Listen; f.LanOnly = true; }), "kind_not_accepted");
            yield return Row("An unknown hosting mode is kind_not_accepted", Lan(), Facts((HostingMode)99, lanOnly: true), "kind_not_accepted");
        }

        // ---- self-hosted and listen online, reservation (verify) ---------------------------------------

        private static IEnumerable<TestCaseData> VerifiedReservations()
        {
            foreach (HostingMode mode in new[] { HostingMode.SelfHosted, HostingMode.Listen })
            {
                string m = mode == HostingMode.SelfHosted ? "SelfHosted" : "Listen online";
                yield return Row(m + " reservation: a verdict-only Valid is accepted", Reservation(), Verified(mode, ReservationEvidence.VerifyValid), Accept);
                yield return Row(m + " reservation: a verdict-only Valid for a player already in is duplicate_player", Reservation(),
                    Verified(mode, ReservationEvidence.VerifyValid).With(f => f.PlayerAlreadyAdmitted = true), "duplicate_player");
                yield return Row(m + " reservation: a detailed Valid open hold below its seats is accepted", Reservation(),
                    Verified(mode, ReservationEvidence.VerifyValid, 2).With(f => f.AdmittedForReservation = 1), Accept);
                yield return Row(m + " reservation: a detailed Valid open hold with every seat held is roster_full", Reservation(),
                    Verified(mode, ReservationEvidence.VerifyValid, 2).With(f => f.AdmittedForReservation = 2), "roster_full");
                yield return Row(m + " reservation: a detailed Valid open hold for a player already in is duplicate_player", Reservation(),
                    Verified(mode, ReservationEvidence.VerifyValid, 4).With(f => f.PlayerAlreadyAdmitted = true), "duplicate_player");
                yield return Row(m + " reservation: a detailed Valid hold naming the player is accepted", Reservation(),
                    Verified(mode, ReservationEvidence.VerifyValid, 2, PlayerId, "anon:p2"), Accept);
                yield return Row(m + " reservation: a detailed Valid hold naming only others is reservation_invalid", Reservation(),
                    Verified(mode, ReservationEvidence.VerifyValid, 2, "anon:p2"), "reservation_invalid");
                yield return Row(m + " reservation: Invalid is reservation_invalid", Reservation(), Verified(mode, ReservationEvidence.VerifyInvalid), "reservation_invalid");
                yield return Row(m + " reservation: WrongServer is reservation_invalid", Reservation(), Verified(mode, ReservationEvidence.VerifyWrongServer), "reservation_invalid");
                yield return Row(m + " reservation: NotInReservation is reservation_invalid", Reservation(), Verified(mode, ReservationEvidence.VerifyNotInReservation), "reservation_invalid");
                yield return Row(m + " reservation: Unavailable is reservation_unverifiable", Reservation(), Verified(mode, ReservationEvidence.VerifyUnavailable), "reservation_unverifiable");
                yield return Row(m + " reservation: no verify gathered fails closed", Reservation(), Facts(mode), "reservation_unverifiable");
                yield return Row(m + " reservation: a hosted hold is the wrong evidence and fails closed", Reservation(),
                    HostedHold(2, PlayerId).With(f => f.Mode = mode), "reservation_unverifiable");
            }
        }

        // ---- listen host started LAN only --------------------------------------------------------------

        private static IEnumerable<TestCaseData> LanOnly()
        {
            yield return Row("Listen LAN-only lan ticket is accepted", Lan(), Facts(HostingMode.Listen, lanOnly: true), Accept);
            yield return Row("Listen LAN-only lan ticket for a player already in is duplicate_player", Lan(),
                Facts(HostingMode.Listen, lanOnly: true).With(f => f.PlayerAlreadyAdmitted = true), "duplicate_player");
            yield return Row("The lan example is accepted by a LAN-only listen host", JoinTicketCodec.Decode(Example("lan")).Ticket, Facts(HostingMode.Listen, lanOnly: true), Accept);
        }

        [TestCaseSource(nameof(Prechecks))]
        [TestCaseSource(nameof(HostedReservation))]
        [TestCaseSource(nameof(HostedMatch))]
        [TestCaseSource(nameof(HostedRosterlessMatch))]
        [TestCaseSource(nameof(HostedBackfillRows))]
        [TestCaseSource(nameof(NotAccepted))]
        [TestCaseSource(nameof(VerifiedReservations))]
        [TestCaseSource(nameof(LanOnly))]
        public void TheTableDecides(JoinTicket ticket, AdmissionFacts facts, string expected)
        {
            AdmissionDecision decision = JoinAdmission.Decide(ticket, facts);
            Assert.That(decision.ReasonWire, Is.EqualTo(expected), decision.Detail);
            Assert.That(decision.Approved, Is.EqualTo(expected == null));
            Assert.That(decision.Reason == JoinRejectReason.None, Is.EqualTo(expected == null));
            Assert.That(decision.Kind, Is.EqualTo(ticket.Kind));
            Assert.That(decision.Mode, Is.EqualTo(facts.Mode));
            if (decision.Detail != null && ticket.TicketId != null)
            {
                Assert.That(decision.Detail, Does.Not.Contain(ticket.TicketId), "a detail must never quote the ticket id");
            }
        }

        [Test]
        public void AnAcceptedMatchCarriesThePartySizeAndTheTicketRefButNeverTheTicketId()
        {
            AdmissionDecision decision = JoinAdmission.Decide(Match(playerId: "anon:friend"), HostedSession(party: 3));
            Assert.That(decision.Approved, Is.True);
            Assert.That(decision.PartySize, Is.EqualTo(3));
            Assert.That(decision.TicketRef, Is.EqualTo(Match().TicketRef));
            Assert.That(decision.ToString(), Does.Not.Contain(TicketId));
            Assert.That(decision.AllocationId, Is.EqualTo(Allocation));
            Assert.That(decision.PlayerId, Is.EqualTo("anon:friend"));
        }

        [Test]
        public void TheProtocolIsCheckedBeforeStoppingAndStoppingBeforeTheCell()
        {
            AdmissionFacts stoppingWithNothing = Facts(HostingMode.Hosted).With(f => f.Stopping = true);
            Assert.That(JoinAdmission.Decide(Match(), stoppingWithNothing).ReasonWire, Is.EqualTo("stopping"), "the cell would say allocation_mismatch");
            Assert.That(JoinAdmission.Precheck(Match(), Protocol, false), Is.Null);
            Assert.That(JoinAdmission.Precheck(Match(), Protocol + 1, true).ReasonWire, Is.EqualTo("protocol_mismatch"));
            Assert.That(JoinAdmission.Precheck(Match(), Protocol, true).ReasonWire, Is.EqualTo("stopping"));
        }

        [Test]
        public void NullTicketsOrFactsAreAProgrammingError()
        {
            Assert.Throws<ArgumentNullException>(() => JoinAdmission.Decide(null, Facts(HostingMode.Hosted)));
            Assert.Throws<ArgumentNullException>(() => JoinAdmission.Decide(Match(), null));
        }
    }
}
