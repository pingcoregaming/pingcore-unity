using NUnit.Framework;
using PingCore.Core.Handshake;
using static PingCore.Sdk.Tests.Editor.Handshake.HandshakeTestData;

namespace PingCore.Sdk.Tests.Editor.Handshake
{
    /// <summary>The admission ledger: party size per (allocation, ticket), seats per reservation, one connection per player, release on disconnect.</summary>
    public sealed class AdmissionLedgerTests
    {
        [Test]
        public void APartyFillsItsTicketUpToPartySizeThenTheTableSaysRosterFull()
        {
            var ledger = new AdmissionLedger();
            string[] members = { "anon:a", "anon:b", "anon:c" };
            for (int i = 0; i < members.Length; i++)
            {
                JoinTicket ticket = Match(playerId: members[i]);
                AdmissionFacts facts = HostedSession(party: 3);
                ledger.Snapshot(ticket, facts);
                Assert.That(facts.AdmittedForTicket, Is.EqualTo(i));
                Assert.That(JoinAdmission.Decide(ticket, facts).Approved, Is.True, members[i]);
                Assert.That(ledger.Hold((ulong)(10 + i), ticket), Is.True);
            }

            JoinTicket fourth = Match(playerId: "anon:d");
            AdmissionFacts full = HostedSession(party: 3);
            ledger.Snapshot(fourth, full);
            Assert.That(JoinAdmission.Decide(fourth, full).ReasonWire, Is.EqualTo("roster_full"));
            Assert.That(ledger.AdmittedForTicket(Allocation, TicketId), Is.EqualTo(3));
        }

        [Test]
        public void ARosterlessAllocationCountsEveryMatchConnectionWhateverItsTicketIdUpToMaxPlayers()
        {
            var ledger = new AdmissionLedger();
            string[] tickets = { "ticket-a", "ticket-b", "ticket-a" };
            for (int i = 0; i < tickets.Length; i++)
            {
                JoinTicket ticket = Match(playerId: "anon:" + i, ticketId: tickets[i]);
                AdmissionFacts facts = RosterlessSession(3);
                ledger.Snapshot(ticket, facts);
                Assert.That(facts.AdmittedForAllocation, Is.EqualTo(i));
                Assert.That(JoinAdmission.Decide(ticket, facts).Approved, Is.True, "join " + i);
                Assert.That(ledger.Hold((ulong)(20 + i), ticket), Is.True);
            }

            JoinTicket fourth = Match(playerId: "anon:3", ticketId: "ticket-c");
            AdmissionFacts full = RosterlessSession(3);
            ledger.Snapshot(fourth, full);
            Assert.That(full.AdmittedForAllocation, Is.EqualTo(3));
            Assert.That(JoinAdmission.Decide(fourth, full).ReasonWire, Is.EqualTo("roster_full"));

            // Reservation and backfill seats, and another allocation, are not in this allocation's count.
            ledger.Hold(30, Reservation(playerId: "anon:r"));
            ledger.Hold(31, BackfillTicket(playerId: "anon:b", allocationId: Allocation));
            ledger.Hold(32, Match(playerId: "anon:o", allocationId: "self-other"));
            Assert.That(ledger.AdmittedForAllocation(Allocation), Is.EqualTo(3));
            Assert.That(ledger.AdmittedForAllocation("self-other"), Is.EqualTo(1));

            Assert.That(ledger.Release(21), Is.True);
            AdmissionFacts after = RosterlessSession(3);
            ledger.Snapshot(fourth, after);
            Assert.That(after.AdmittedForAllocation, Is.EqualTo(2));
            Assert.That(JoinAdmission.Decide(fourth, after).Approved, Is.True, "a disconnect gives the seat back");

            AdmissionFacts reservationFacts = HostedHold(2);
            ledger.Snapshot(Reservation(playerId: "anon:r2"), reservationFacts);
            Assert.That(reservationFacts.AdmittedForAllocation, Is.EqualTo(0), "only a match ticket reads the allocation count");

            ledger.Clear();
            Assert.That(ledger.AdmittedForAllocation(Allocation), Is.EqualTo(0));
        }

        [Test]
        public void TheSameTicketIdInAnotherAllocationIsCountedApart()
        {
            var ledger = new AdmissionLedger();
            ledger.Hold(1, Match(allocationId: "alloc-1"));
            Assert.That(ledger.AdmittedForTicket("alloc-1", TicketId), Is.EqualTo(1));
            Assert.That(ledger.AdmittedForTicket("alloc-2", TicketId), Is.EqualTo(0));
        }

        [Test]
        public void AnOpenReservationCountsSeatsAndAReleaseGivesOneBack()
        {
            var ledger = new AdmissionLedger();
            ledger.Hold(1, Reservation(playerId: "anon:a"));
            ledger.Hold(2, Reservation(playerId: "anon:b"));
            JoinTicket third = Reservation(playerId: "anon:c");
            AdmissionFacts facts = HostedHold(2);
            ledger.Snapshot(third, facts);
            Assert.That(facts.AdmittedForReservation, Is.EqualTo(2));
            Assert.That(JoinAdmission.Decide(third, facts).ReasonWire, Is.EqualTo("roster_full"));

            Assert.That(ledger.Release(1), Is.True);
            AdmissionFacts after = HostedHold(2);
            ledger.Snapshot(third, after);
            Assert.That(after.AdmittedForReservation, Is.EqualTo(1));
            Assert.That(JoinAdmission.Decide(third, after).Approved, Is.True);
        }

        [Test]
        public void ASecondConnectionOfOnePlayerIsDuplicateUntilTheFirstDisconnects()
        {
            var ledger = new AdmissionLedger();
            ledger.Hold(1, Lan("lan-a"));
            AdmissionFacts facts = Facts(HostingMode.Listen, lanOnly: true);
            ledger.Snapshot(Lan("lan-a"), facts);
            Assert.That(facts.PlayerAlreadyAdmitted, Is.True);
            Assert.That(JoinAdmission.Decide(Lan("lan-a"), facts).ReasonWire, Is.EqualTo("duplicate_player"));

            ledger.Release(1);
            AdmissionFacts again = Facts(HostingMode.Listen, lanOnly: true);
            ledger.Snapshot(Lan("lan-a"), again);
            Assert.That(again.PlayerAlreadyAdmitted, Is.False);
            Assert.That(JoinAdmission.Decide(Lan("lan-a"), again).Approved, Is.True, "a reconnect after a disconnect is fine");
        }

        [Test]
        public void AConnectionIsHeldOnceAndReleasedOnce()
        {
            var ledger = new AdmissionLedger();
            Assert.That(ledger.Hold(7, Match()), Is.True);
            Assert.That(ledger.Hold(7, Match(playerId: "anon:other")), Is.False, "the same connection cannot take a second seat");
            Assert.That(ledger.Count, Is.EqualTo(1));
            Assert.That(ledger.IsPlayerAdmitted("anon:other"), Is.False);
            Assert.That(ledger.Release(7), Is.True);
            Assert.That(ledger.Release(7), Is.False);
            Assert.That(ledger.Release(99), Is.False);
            Assert.That(ledger.Count, Is.EqualTo(0));
            Assert.That(ledger.IsPlayerAdmitted(PlayerId), Is.False);
            Assert.That(ledger.AdmittedForTicket(Allocation, TicketId), Is.EqualTo(0));
        }

        [Test]
        public void AReservationHoldCountsOnlyAgainstItsReservationAndAMatchOnlyAgainstItsTicket()
        {
            var ledger = new AdmissionLedger();
            ledger.Hold(1, Reservation(reservationId: "res-x"));
            ledger.Hold(2, Match(playerId: "anon:m"));
            Assert.That(ledger.AdmittedForReservation("res-x"), Is.EqualTo(1));
            Assert.That(ledger.AdmittedForTicket(Allocation, TicketId), Is.EqualTo(1));
            Assert.That(ledger.AdmittedForReservation("res-y"), Is.EqualTo(0));
            ledger.Clear();
            Assert.That(ledger.Count, Is.EqualTo(0));
            Assert.That(ledger.AdmittedForReservation("res-x"), Is.EqualTo(0));
        }
    }
}
