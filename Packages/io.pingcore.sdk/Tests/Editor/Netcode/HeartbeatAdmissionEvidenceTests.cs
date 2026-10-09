using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Core.Handshake;
using PingCore.Discovery.Host;
using PingCore.Discovery.Host.Wire;

namespace PingCore.Netcode.NGO.Tests.Editor
{
    /// <summary>
    /// How a verify result becomes admission evidence on a self-hosted game server or an online listen
    /// host, and what the decision table then does with it (the detailed-answer seat rule and the
    /// verdict-only once-per-player rule).
    /// </summary>
    public sealed class HeartbeatAdmissionEvidenceTests
    {
        private static readonly DiscoveryCallResult Ok = DiscoveryCallResult.Refused(DiscoveryReason.Unknown, "test");

        private static JoinTicket Ticket(string player = "anon:p1") => JoinTicket.ForReservation("res-1", player, 3);

        private static AdmissionFacts Facts(VerifyResult verdict, HostingMode mode = HostingMode.SelfHosted, int admitted = 0, bool duplicate = false)
        {
            var facts = new AdmissionFacts { ProtocolVersion = 3, Mode = mode, EvidenceSource = "heartbeat", AdmittedForReservation = admitted, PlayerAlreadyAdmitted = duplicate };
            HeartbeatAdmissionEvidence.Apply(verdict, facts);
            return facts;
        }

        private static VerifyResult Detailed(VerifyVerdict verdict, int? seats, List<string> players) => new VerifyResult(verdict, true,
            new VerifyReservationResponse { Valid = verdict == VerifyVerdict.Valid, Seats = seats, PlayerIds = players, Context = JObject.Parse("{\"mode\":\"duel\"}") }, Ok);

        [TestCase(VerifyVerdict.Valid, ReservationEvidence.VerifyValid)]
        [TestCase(VerifyVerdict.Invalid, ReservationEvidence.VerifyInvalid)]
        [TestCase(VerifyVerdict.WrongServer, ReservationEvidence.VerifyWrongServer)]
        [TestCase(VerifyVerdict.NotInReservation, ReservationEvidence.VerifyNotInReservation)]
        [TestCase(VerifyVerdict.Unavailable, ReservationEvidence.VerifyUnavailable)]
        public void EveryVerdictMapsToItsEvidence(VerifyVerdict verdict, ReservationEvidence expected)
        {
            Assert.That(HeartbeatAdmissionEvidence.MapVerdict(verdict), Is.EqualTo(expected));
        }

        [Test]
        public void AVerdictOnlyValidCarriesNoSeatsAndIsAdmittedOncePerPlayer()
        {
            var verdictOnly = new VerifyResult(VerifyVerdict.Valid, false, new VerifyReservationResponse { Valid = true }, Ok);
            AdmissionFacts facts = Facts(verdictOnly, admitted: 50);
            Assert.That(facts.ReservationSeats, Is.Null);
            Assert.That(facts.ReservationPlayerIds, Is.Null);
            Assert.That(JoinAdmission.Decide(Ticket(), facts).Approved, Is.True, "Discovery counted the seat itself");
            Assert.That(JoinAdmission.Decide(Ticket(), Facts(verdictOnly, duplicate: true)).ReasonWire, Is.EqualTo("duplicate_player"));
        }

        [Test]
        public void ADetailedValidOpenHoldIsCountedAgainstItsSeats()
        {
            VerifyResult open = Detailed(VerifyVerdict.Valid, 2, null);
            AdmissionFacts below = Facts(open, admitted: 1);
            Assert.That(below.ReservationSeats, Is.EqualTo(2));
            Assert.That((string)below.ReservationContext["mode"], Is.EqualTo("duel"));
            Assert.That(JoinAdmission.Decide(Ticket(), below).Approved, Is.True);
            Assert.That(JoinAdmission.Decide(Ticket(), Facts(open, admitted: 2)).ReasonWire, Is.EqualTo("roster_full"));
            Assert.That(JoinAdmission.Decide(Ticket(), Facts(open, HostingMode.Listen, admitted: 2)).ReasonWire, Is.EqualTo("roster_full"), "the listen host counts the same way");
        }

        [Test]
        public void ADetailedValidNamedHoldAdmitsOnlyItsPlayers()
        {
            VerifyResult named = Detailed(VerifyVerdict.Valid, 2, new List<string> { "anon:p1", "anon:p2" });
            Assert.That(JoinAdmission.Decide(Ticket("anon:p2"), Facts(named)).Approved, Is.True);
            Assert.That(JoinAdmission.Decide(Ticket("anon:p9"), Facts(named)).ReasonWire, Is.EqualTo("reservation_invalid"));
        }

        [Test]
        public void ARefusedOrMissingVerdictNeverAdmits()
        {
            Assert.That(JoinAdmission.Decide(Ticket(), Facts(Detailed(VerifyVerdict.WrongServer, 2, null))).ReasonWire, Is.EqualTo("reservation_invalid"));
            Assert.That(JoinAdmission.Decide(Ticket(), Facts(new VerifyResult(VerifyVerdict.Unavailable, false, null, Ok))).ReasonWire, Is.EqualTo("reservation_unverifiable"));
            AdmissionFacts none = Facts(null);
            Assert.That(none.Reservation, Is.EqualTo(ReservationEvidence.VerifyUnavailable));
            Assert.That(JoinAdmission.Decide(Ticket(), none).ReasonWire, Is.EqualTo("reservation_unverifiable"));
        }
    }
}
