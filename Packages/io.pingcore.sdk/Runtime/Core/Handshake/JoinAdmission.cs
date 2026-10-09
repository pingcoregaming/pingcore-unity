using System;
using System.Collections.Generic;

namespace PingCore.Core.Handshake
{
    /// <summary>
    /// The connect decision table, as a pure function (published, engine-neutral, at
    /// https://pingcore.io/docs/fleets/admitting-players). Checked in
    /// order for every kind: the payload decodes (<see cref="JoinTicketCodec.Decode"/>, before this runs);
    /// <c>protocolVersion</c> equals the game's (<c>protocol_mismatch</c>); the game server is not stopping
    /// (<c>stopping</c>); then the cell for the hosting mode and kind. Inside each accepting cell the order
    /// is: the evidence, then one live connection per <c>playerId</c> (<c>duplicate_player</c>), then seats.
    /// <list type="table">
    /// <item><term>Hosted, reservation</term><description>The pushed hold: NotFound or Expired is
    /// <c>reservation_invalid</c>; Unreachable or Error is <c>reservation_unverifiable</c>; a hold naming
    /// players admits only those (<c>reservation_invalid</c> otherwise); a hold naming none admits while the
    /// ledger for the reservation is below <c>seats</c> (<c>roster_full</c> otherwise). The cell does not ask for an open
    /// session: an idle game server (no current allocation) is accepted on the hold too, and whether idle is allowed is the
    /// game's gate's call. With <see cref="ApprovalOptions.ClaimIdleSessions"/>, an accepted join on an idle game server then
    /// needs a session claim before it is approved (<see cref="NeedsSessionClaim"/>, carried out by <see cref="AdmissionPipeline"/>).</description></item>
    /// <item><term>Hosted, match</term><description><c>allocationId</c> equals the current allocation, else
    /// <c>allocation_mismatch</c>; <c>ticketId</c> in the roster (matched on the ticket id alone), else
    /// <c>not_in_roster</c>; then up to <c>partySize</c> distinct players per (<c>allocationId</c>,
    /// <c>ticketId</c>), first come, the submitter included, <c>roster_full</c> after that. A rosterless
    /// allocation (<see cref="AdmissionFacts.RosterlessAllocation"/>: a backend allocation, or a supervisor
    /// self-allocation when <see cref="AdmissionFacts.AllowSelfAllocatedJoins"/> is set) skips the roster
    /// and party checks and admits on <c>allocationId</c> alone, any <c>ticketId</c>, up to
    /// <see cref="AdmissionFacts.MaxPlayers"/> per allocation (<c>roster_full</c> after that; 0 is no
    /// limit). The allocation id is then the only secret, so a backend MUST mint an unguessable
    /// <c>allocationId</c> (or omit it, so Discovery mints a UUID) before it relies on rosterless admission.
    /// A self-allocation's id is the supervisor's clock (<c>self-&lt;ms&gt;</c>), so by default it is never
    /// rosterless: every <c>match</c> ticket for it is <c>not_in_roster</c>.</description></item>
    /// <item><term>Hosted, backfill</term><description>A delivered backfill with this <c>allocationId</c> whose
    /// <c>sessionId</c> is the open session's (the evidence waits for it), else <c>backfill_unknown</c>; then
    /// the roster and party checks of match.</description></item>
    /// <item><term>Self-hosted, reservation; listen online, reservation</term><description>Verify: Invalid,
    /// WrongServer, NotInReservation are <c>reservation_invalid</c>; Unavailable is
    /// <c>reservation_unverifiable</c>; Valid admits. A detailed Valid naming no players admits while the ledger
    /// for the reservation is below its <c>seats</c> (<c>roster_full</c> otherwise); a verdict-only Valid (no
    /// seats) admits once per player and leaves the seat count to Discovery.</description></item>
    /// <item><term>Listen LAN only, lan</term><description>Admits.</description></item>
    /// <item><term>Every other cell</term><description><c>kind_not_accepted</c>.</description></item>
    /// </list>
    /// Evidence of the wrong sort for the mode (a verify verdict on a hosted game server, a hold on a
    /// self-hosted one, or none) fails closed as <c>reservation_unverifiable</c>. The game's
    /// <see cref="IAdmissionGate"/> and the deadline come after this, in <see cref="AdmissionPipeline"/>.
    /// </summary>
    public static class JoinAdmission
    {
        /// <summary>Decides one decoded ticket against the gathered facts. Pure; never throws for a fact combination.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="ticket"/> or <paramref name="facts"/> is null.</exception>
        public static AdmissionDecision Decide(JoinTicket ticket, AdmissionFacts facts)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            if (facts == null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            AdmissionDecision early = Precheck(ticket, facts.ProtocolVersion, facts.Stopping, facts);
            if (early != null)
            {
                return early;
            }

            switch (facts.Mode)
            {
                case HostingMode.Hosted:
                    switch (ticket.Kind)
                    {
                        case JoinTicketKind.Reservation:
                            return HostedReservation(ticket, facts);
                        case JoinTicketKind.Match:
                            return HostedMatch(ticket, facts);
                        case JoinTicketKind.Backfill:
                            return HostedBackfill(ticket, facts);
                        default:
                            return NotAccepted(ticket, facts, "a PingCore-hosted game server takes no lan tickets");
                    }

                case HostingMode.SelfHosted:
                    return ticket.Kind == JoinTicketKind.Reservation
                        ? VerifiedReservation(ticket, facts)
                        : NotAccepted(ticket, facts, "a self-hosted game server takes reservation tickets only");

                case HostingMode.Listen:
                    if (facts.LanOnly)
                    {
                        return ticket.Kind == JoinTicketKind.Lan
                            ? Admit(ticket, facts, null)
                            : NotAccepted(ticket, facts, "a LAN-only listen host takes lan tickets only");
                    }

                    return ticket.Kind == JoinTicketKind.Reservation
                        ? VerifiedReservation(ticket, facts)
                        : NotAccepted(ticket, facts, "an online listen host takes reservation tickets only");

                default:
                    return NotAccepted(ticket, facts, "unknown hosting mode");
            }
        }

        /// <summary>
        /// The checks that need no evidence: protocol version, then stopping. Null when both pass. The
        /// pipeline runs this before gathering evidence, so a mismatched client costs no lookup;
        /// <see cref="Decide"/> runs it again.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="ticket"/> is null.</exception>
        public static AdmissionDecision Precheck(JoinTicket ticket, int protocolVersion, bool stopping, AdmissionFacts factsForReport = null)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            if (ticket.ProtocolVersion != protocolVersion)
            {
                return Reject(JoinRejectReason.ProtocolMismatch,
                    "the client speaks protocol " + ticket.ProtocolVersion + "; this game server speaks " + protocolVersion, ticket, factsForReport);
            }

            if (stopping)
            {
                return Reject(JoinRejectReason.Stopping, "this game server is stopping", ticket, factsForReport);
            }

            return null;
        }

        /// <summary>
        /// The hosted reservation rule's second half, pure and opt-in: when the game set
        /// <see cref="ApprovalOptions.ClaimIdleSessions"/> (<see cref="AdmissionFacts.ClaimIdleSessions"/>), a <c>reservation</c>
        /// join on a PingCore-hosted game server whose evidence found no current allocation (an idle game server) is approved
        /// only after the game server claimed a session for it (a self-allocation through the local SDK endpoint), so the
        /// matchmaker stops allocating it before the player is let in. True for exactly that cell, opted in, before any claim
        /// was made; the pipeline asks it after the table and the gate accepted. Without the opt-in it is always false, and an
        /// idle reservation is decided by the table and the gate alone. The joiner is admitted on the hold; the self-allocation's guessable id never admits a
        /// <c>match</c> join (see <see cref="AdmissionFacts.SelfAllocation"/>).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="ticket"/> or <paramref name="facts"/> is null.</exception>
        public static bool NeedsSessionClaim(JoinTicket ticket, AdmissionFacts facts)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            if (facts == null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            return facts.ClaimIdleSessions && facts.Mode == HostingMode.Hosted && ticket.Kind == JoinTicketKind.Reservation
                && facts.CurrentAllocationId == null && facts.SessionClaim == SessionClaimOutcome.None;
        }

        private static AdmissionDecision HostedReservation(JoinTicket ticket, AdmissionFacts facts)
        {
            switch (facts.Reservation)
            {
                case ReservationEvidence.HoldFound:
                    break;
                case ReservationEvidence.HoldNotFound:
                    return Reject(JoinRejectReason.ReservationInvalid, "no live hold with this id on this game server", ticket, facts);
                case ReservationEvidence.HoldExpired:
                    return Reject(JoinRejectReason.ReservationInvalid, "the hold has expired", ticket, facts);
                case ReservationEvidence.HoldUnreachable:
                    return Reject(JoinRejectReason.ReservationUnverifiable, "the local SDK endpoint did not answer", ticket, facts);
                case ReservationEvidence.HoldError:
                    return Reject(JoinRejectReason.ReservationUnverifiable, "the local SDK endpoint gave an unexpected answer", ticket, facts);
                default:
                    return Reject(JoinRejectReason.ReservationUnverifiable, "no hosted hold evidence was gathered", ticket, facts);
            }

            // A hosted hold always has seats; a missing count fails closed (no seat to give).
            return HoldSeats(ticket, facts, facts.ReservationSeats ?? 0);
        }

        private static AdmissionDecision VerifiedReservation(JoinTicket ticket, AdmissionFacts facts)
        {
            switch (facts.Reservation)
            {
                case ReservationEvidence.VerifyValid:
                    break;
                case ReservationEvidence.VerifyInvalid:
                    return Reject(JoinRejectReason.ReservationInvalid, "verify said the hold is not valid", ticket, facts);
                case ReservationEvidence.VerifyWrongServer:
                    return Reject(JoinRejectReason.ReservationInvalid, "verify said the hold is on another game server", ticket, facts);
                case ReservationEvidence.VerifyNotInReservation:
                    return Reject(JoinRejectReason.ReservationInvalid, "verify said the hold does not name this player", ticket, facts);
                case ReservationEvidence.VerifyUnavailable:
                    return Reject(JoinRejectReason.ReservationUnverifiable, "verify could not answer", ticket, facts);
                default:
                    return Reject(JoinRejectReason.ReservationUnverifiable, "no verify evidence was gathered", ticket, facts);
            }

            if (facts.ReservationSeats == null && facts.ReservationPlayerIds == null)
            {
                // A verdict-only answer (an open app's shipped heartbeat token): Discovery counts the seats.
                return facts.PlayerAlreadyAdmitted ? Duplicate(ticket, facts) : Admit(ticket, facts, null);
            }

            return HoldSeats(ticket, facts, facts.ReservationSeats ?? 0);
        }

        /// <summary>The ledger rules of a hold the game server can see: named players only, else open seats up to <paramref name="seats"/>.</summary>
        private static AdmissionDecision HoldSeats(JoinTicket ticket, AdmissionFacts facts, int seats)
        {
            if (facts.ReservationPlayerIds != null && !Contains(facts.ReservationPlayerIds, ticket.PlayerId))
            {
                return Reject(JoinRejectReason.ReservationInvalid, "the hold names other players", ticket, facts);
            }

            if (facts.PlayerAlreadyAdmitted)
            {
                return Duplicate(ticket, facts);
            }

            if (facts.ReservationPlayerIds == null && facts.AdmittedForReservation >= seats)
            {
                return Reject(JoinRejectReason.RosterFull, "every seat of the hold is taken (" + seats + ")", ticket, facts);
            }

            return Admit(ticket, facts, null);
        }

        private static AdmissionDecision HostedMatch(JoinTicket ticket, AdmissionFacts facts)
        {
            if (facts.CurrentAllocationId == null || !string.Equals(ticket.AllocationId, facts.CurrentAllocationId, StringComparison.Ordinal))
            {
                return Reject(JoinRejectReason.AllocationMismatch, facts.CurrentAllocationId == null
                    ? "this game server has no current allocation"
                    : "the ticket names another allocation", ticket, facts);
            }

            if (facts.RosterlessAllocation && (facts.Roster == null || facts.Roster.Count == 0))
            {
                if (facts.SelfAllocation && !facts.AllowSelfAllocatedJoins)
                {
                    return Reject(JoinRejectReason.NotInRoster,
                        "a supervisor self-allocation has a guessable id, so it admits no match join unless ApprovalOptions.AllowSelfAllocatedJoins is set", ticket, facts);
                }

                return RosterlessSeats(ticket, facts);
            }

            return RosterAndParty(ticket, facts);
        }

        /// <summary>
        /// A match into an allocation that carries no roster (a backend allocation, or an allowed supervisor
        /// self-allocation): the allocation id the backend handed out is the bearer secret, so the roster
        /// and party checks are skipped, the ticket id is ignored, and the allocation admits up to
        /// <see cref="AdmissionFacts.MaxPlayers"/> connections (no limit at 0 or less).
        /// </summary>
        private static AdmissionDecision RosterlessSeats(JoinTicket ticket, AdmissionFacts facts)
        {
            if (facts.PlayerAlreadyAdmitted)
            {
                return Duplicate(ticket, facts);
            }

            if (facts.MaxPlayers > 0 && facts.AdmittedForAllocation >= facts.MaxPlayers)
            {
                return Reject(JoinRejectReason.RosterFull, "the rosterless allocation already holds its " + facts.MaxPlayers + " players", ticket, facts);
            }

            return Admit(ticket, facts, null);
        }

        private static AdmissionDecision HostedBackfill(JoinTicket ticket, AdmissionFacts facts)
        {
            if (!facts.BackfillDelivered || !string.Equals(facts.BackfillAllocationId, ticket.AllocationId, StringComparison.Ordinal))
            {
                return Reject(JoinRejectReason.BackfillUnknown, "no delivered backfill has this allocation id", ticket, facts);
            }

            if (facts.CurrentAllocationId == null || !string.Equals(facts.BackfillSessionId, facts.CurrentAllocationId, StringComparison.Ordinal))
            {
                return Reject(JoinRejectReason.BackfillUnknown, "the backfill does not join the open session", ticket, facts);
            }

            return RosterAndParty(ticket, facts);
        }

        private static AdmissionDecision RosterAndParty(JoinTicket ticket, AdmissionFacts facts)
        {
            RosterEntry entry = null;
            if (facts.Roster != null)
            {
                foreach (RosterEntry candidate in facts.Roster)
                {
                    if (candidate != null && candidate.HasTicket(ticket.TicketId))
                    {
                        entry = candidate;
                        break;
                    }
                }
            }

            if (entry == null)
            {
                return Reject(JoinRejectReason.NotInRoster, "the ticket is not in the roster", ticket, facts);
            }

            if (facts.PlayerAlreadyAdmitted)
            {
                return Duplicate(ticket, facts);
            }

            if (facts.AdmittedForTicket >= entry.PartySize)
            {
                return Reject(JoinRejectReason.RosterFull, "the ticket already brought its party of " + entry.PartySize, ticket, facts);
            }

            return Admit(ticket, facts, entry.PartySize);
        }

        private static bool Contains(IReadOnlyList<string> values, string value)
        {
            foreach (string candidate in values)
            {
                if (string.Equals(candidate, value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static AdmissionDecision Admit(JoinTicket ticket, AdmissionFacts facts, int? partySize)
        {
            if (facts.PlayerAlreadyAdmitted)
            {
                return Duplicate(ticket, facts);
            }

            return AdmissionDecision.Accept(ticket, facts, partySize);
        }

        private static AdmissionDecision Duplicate(JoinTicket ticket, AdmissionFacts facts) =>
            Reject(JoinRejectReason.DuplicatePlayer, "this player already has a live connection", ticket, facts);

        private static AdmissionDecision NotAccepted(JoinTicket ticket, AdmissionFacts facts, string detail) =>
            Reject(JoinRejectReason.KindNotAccepted, detail, ticket, facts);

        private static AdmissionDecision Reject(JoinRejectReason reason, string detail, JoinTicket ticket, AdmissionFacts facts) =>
            AdmissionDecision.Reject(reason, detail, ticket, facts);
    }
}
