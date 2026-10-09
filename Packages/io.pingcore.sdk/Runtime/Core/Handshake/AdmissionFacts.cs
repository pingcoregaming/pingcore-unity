using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace PingCore.Core.Handshake
{
    /// <summary>What the evidence found about a <c>reservation</c> join ticket.</summary>
    public enum ReservationEvidence
    {
        /// <summary>Nothing was gathered (the evidence does not read reservations). Fails closed: <c>reservation_unverifiable</c>.</summary>
        NotGathered = 0,

        /// <summary>Hosted: the local SDK endpoint served a live hold. <see cref="AdmissionFacts.ReservationPlayerIds"/> and <see cref="AdmissionFacts.ReservationSeats"/> are set.</summary>
        HoldFound = 1,

        /// <summary>Hosted: still <c>reservation not found</c> after the trailing-push wait (unknown, released or expired).</summary>
        HoldNotFound = 2,

        /// <summary>Hosted: the hold was served with an <c>expiresAt</c> already past.</summary>
        HoldExpired = 3,

        /// <summary>Hosted: the local SDK endpoint did not answer.</summary>
        HoldUnreachable = 4,

        /// <summary>Hosted: any other answer (an unexpected status or body, or a supervisor without reservation routes).</summary>
        HoldError = 5,

        /// <summary>Self-hosted or listen: verify said valid. A detailed answer also matched this game server and player and sets <see cref="AdmissionFacts.ReservationSeats"/> and <see cref="AdmissionFacts.ReservationPlayerIds"/>; a verdict-only answer leaves seats null.</summary>
        VerifyValid = 6,

        /// <summary>Self-hosted or listen: verify said invalid.</summary>
        VerifyInvalid = 7,

        /// <summary>Self-hosted or listen: the hold is on another game server.</summary>
        VerifyWrongServer = 8,

        /// <summary>Self-hosted or listen: the hold names players and this one is not among them.</summary>
        VerifyNotInReservation = 9,

        /// <summary>Self-hosted or listen: verify could not answer (429, 503, transport, or no server id yet). Never valid.</summary>
        VerifyUnavailable = 10,
    }

    /// <summary>
    /// Everything <see cref="JoinAdmission.Decide"/> needs, gathered before it runs: the game's own
    /// settings, what the evidence (<see cref="IAdmissionEvidence"/>) found, and a snapshot of the
    /// <see cref="AdmissionLedger"/>. A plain record so tests can build any combination; the
    /// decision reads only these fields. Unset fields fail closed.
    /// </summary>
    public sealed class AdmissionFacts
    {
        /// <summary>The game's own network protocol version.</summary>
        public int ProtocolVersion { get; set; }

        /// <summary>How this game server is hosted.</summary>
        public HostingMode Mode { get; set; }

        /// <summary>A listen host started LAN only (no heartbeat, no verify). Only meaningful with <see cref="HostingMode.Listen"/>.</summary>
        public bool LanOnly { get; set; }

        /// <summary>The game server is stopping or shutting down.</summary>
        public bool Stopping { get; set; }

        /// <summary>Where the evidence came from: <c>fleet</c>, <c>heartbeat</c>, <c>lan</c>, or the evidence's own name.</summary>
        public string EvidenceSource { get; set; }

        /// <summary>What the evidence found for a <c>reservation</c> ticket.</summary>
        public ReservationEvidence Reservation { get; set; }

        /// <summary>The hold's named players (a hosted hold, or a detailed verify answer), or null for open seats or a verdict-only answer.</summary>
        public IReadOnlyList<string> ReservationPlayerIds { get; set; }

        /// <summary>The hold's seats (a hosted hold, or a detailed verify answer); null when unknown (a verdict-only verify answer, whose seats Discovery counts itself).</summary>
        public int? ReservationSeats { get; set; }

        /// <summary>Hosted hold, or a verify answer that carried it: the hold's context, or null. For the game's gate.</summary>
        public JObject ReservationContext { get; set; }

        /// <summary>
        /// Hosted: the open session's allocation id (<c>CurrentAllocation</c>), or null when there is none. For a
        /// <c>reservation</c> join, null means the game server is idle, so with <see cref="ClaimIdleSessions"/> an accepted join
        /// must claim a session first (<see cref="JoinAdmission.NeedsSessionClaim"/>); the pipeline then sets it to the claimed or
        /// the platform's allocation.
        /// </summary>
        public string CurrentAllocationId { get; set; }

        /// <summary>
        /// Hosted <c>reservation</c> on an idle game server: what claiming the session came to. <see cref="SessionClaimOutcome.None"/>
        /// while the gate is first asked; the gate is asked a second time only with <see cref="SessionClaimOutcome.AllocatedMeanwhile"/>.
        /// </summary>
        public SessionClaimOutcome SessionClaim { get; set; }

        /// <summary>The roster to authorise a <c>match</c> or <c>backfill</c> ticket against: the allocation context's for a match, the backfill's for a backfill. Null when there is none.</summary>
        public IReadOnlyList<RosterEntry> Roster { get; set; }

        /// <summary>
        /// Hosted match: the current allocation carries no roster (its context has no <c>roster</c>, or a
        /// null or empty one, and is not the matchmaker's, and it parsed): a backend allocation or a supervisor
        /// self-allocation. A <c>match</c> ticket for it is admitted on <c>allocationId</c> alone, up to
        /// <see cref="MaxPlayers"/> (a self-allocation only with <see cref="AllowSelfAllocatedJoins"/>). False
        /// (the default) requires the ticket in <see cref="Roster"/>.
        /// </summary>
        public bool RosterlessAllocation { get; set; }

        /// <summary>
        /// Hosted match: the current allocation is a supervisor self-allocation (<c>self-&lt;ms&gt;</c>), whose
        /// id is guessable. A rosterless self-allocation admits only with <see cref="AllowSelfAllocatedJoins"/>;
        /// otherwise every <c>match</c> ticket for it is <c>not_in_roster</c>.
        /// </summary>
        public bool SelfAllocation { get; set; }

        /// <summary>The game's <see cref="ApprovalOptions.AllowSelfAllocatedJoins"/>: a rosterless self-allocation admits on its id alone.</summary>
        public bool AllowSelfAllocatedJoins { get; set; }

        /// <summary>The game's <see cref="ApprovalOptions.ClaimIdleSessions"/>: an accepted hosted <c>reservation</c> on an idle game server claims a session before its approval.</summary>
        public bool ClaimIdleSessions { get; set; }

        /// <summary>Hosted match on a <see cref="RosterlessAllocation"/>: how many connections the allocation admits; 0 or less means no limit. The <c>players</c> counter's capacity when the evidence can read one.</summary>
        public int MaxPlayers { get; set; }

        /// <summary>Backfill: the evidence found a delivered backfill for the ticket's <c>allocationId</c> (after waiting for it).</summary>
        public bool BackfillDelivered { get; set; }

        /// <summary>Backfill: the delivered backfill's own allocation id; must equal the ticket's.</summary>
        public string BackfillAllocationId { get; set; }

        /// <summary>Backfill: the session the delivered backfill joins; must equal <see cref="CurrentAllocationId"/>.</summary>
        public string BackfillSessionId { get; set; }

        /// <summary>Ledger snapshot: connections already admitted on this ticket's (<c>allocationId</c>, <c>ticketId</c>).</summary>
        public int AdmittedForTicket { get; set; }

        /// <summary>Ledger snapshot: <c>match</c> connections already admitted on this ticket's <c>allocationId</c>, whatever their <c>ticketId</c> (the (<c>allocationId</c>, <c>*</c>) count).</summary>
        public int AdmittedForAllocation { get; set; }

        /// <summary>Ledger snapshot: connections already admitted on this ticket's <c>reservationId</c>.</summary>
        public int AdmittedForReservation { get; set; }

        /// <summary>Ledger snapshot: this <c>playerId</c> already has a live admitted connection.</summary>
        public bool PlayerAlreadyAdmitted { get; set; }

        /// <summary>A shallow copy, so a test or the pipeline can vary one field.</summary>
        public AdmissionFacts Clone() => (AdmissionFacts)MemberwiseClone();
    }
}
