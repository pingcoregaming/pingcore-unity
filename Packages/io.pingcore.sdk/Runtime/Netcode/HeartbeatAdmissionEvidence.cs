using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core.Handshake;
using PingCore.Discovery.Host;

namespace PingCore.Netcode.NGO
{
    /// <summary>
    /// The admission evidence of a self-hosted dedicated game server or an online listen host: a
    /// <c>reservation</c> ticket is checked with Discovery's verify through the game server's own
    /// <see cref="HeartbeatReporter"/> (which always sends its own <c>serverId</c> and the player's id).
    /// A detailed answer carries the hold's seats and players, which the ledger then counts; a
    /// verdict-only answer (an open app's shipped heartbeat token) is admitted once per player while
    /// Discovery counts the seats. Never used on a PingCore-hosted game server, which never verifies.
    /// </summary>
    public sealed class HeartbeatAdmissionEvidence : IAdmissionEvidence
    {
        private readonly HeartbeatReporter reporter;

        /// <summary>Creates the evidence over a started reporter.</summary>
        public HeartbeatAdmissionEvidence(HeartbeatReporter reporter)
        {
            this.reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        }

        /// <inheritdoc />
        public string Source => "heartbeat";

        /// <inheritdoc />
        public bool IsStopping => reporter.Status != null && reporter.Status.Stopped;

        /// <inheritdoc />
        public async Task GatherAsync(JoinTicket ticket, AdmissionFacts facts, ApprovalOptions options, CancellationToken cancellationToken)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            if (facts == null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            if (ticket.Kind == JoinTicketKind.Reservation)
            {
                VerifyResult verdict = await reporter.VerifyReservationAsync(ticket.ReservationId, ticket.PlayerId, cancellationToken);
                Apply(verdict, facts);
            }

            facts.Stopping = facts.Stopping || IsStopping;
        }

        /// <summary>Maps a verify verdict onto the decision's evidence. Pure.</summary>
        public static ReservationEvidence MapVerdict(VerifyVerdict verdict)
        {
            switch (verdict)
            {
                case VerifyVerdict.Valid:
                    return ReservationEvidence.VerifyValid;
                case VerifyVerdict.Invalid:
                    return ReservationEvidence.VerifyInvalid;
                case VerifyVerdict.WrongServer:
                    return ReservationEvidence.VerifyWrongServer;
                case VerifyVerdict.NotInReservation:
                    return ReservationEvidence.VerifyNotInReservation;
                default:
                    return ReservationEvidence.VerifyUnavailable;
            }
        }

        /// <summary>
        /// Writes a verify result into <paramref name="facts"/>: the evidence, and for a detailed answer the
        /// hold's seats, players and context. A verdict-only answer leaves seats and players null. Pure.
        /// </summary>
        public static void Apply(VerifyResult verdict, AdmissionFacts facts)
        {
            if (facts == null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            if (verdict == null)
            {
                facts.Reservation = ReservationEvidence.VerifyUnavailable;
                return;
            }

            facts.Reservation = MapVerdict(verdict.Verdict);
            facts.ReservationSeats = null;
            facts.ReservationPlayerIds = null;
            facts.ReservationContext = null;
            if (verdict.Detailed && verdict.Response != null)
            {
                facts.ReservationSeats = verdict.Response.Seats;
                facts.ReservationPlayerIds = verdict.Response.PlayerIds;
                facts.ReservationContext = verdict.Response.Context;
            }
        }
    }
}
