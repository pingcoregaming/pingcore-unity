namespace PingCore.Core.Handshake
{
    /// <summary>
    /// The verdict on one connection: approved, or the reason it was not, with what it was decided
    /// on. It never carries the ticket id (only <see cref="TicketRef"/>) or any token, so it can go
    /// straight into a log line or an event.
    /// </summary>
    public sealed class AdmissionDecision
    {
        private AdmissionDecision(bool approved, JoinRejectReason reason, string detail, JoinTicketKind? kind, HostingMode mode,
            string evidenceSource, string ticketRef, string playerId, string allocationId, string reservationId, int? partySize,
            ulong connectionId, long elapsedMs, SessionClaimOutcome sessionClaim = SessionClaimOutcome.None)
        {
            SessionClaim = sessionClaim;
            Approved = approved;
            Reason = reason;
            Detail = detail;
            Kind = kind;
            Mode = mode;
            EvidenceSource = evidenceSource;
            TicketRef = ticketRef;
            PlayerId = playerId;
            AllocationId = allocationId;
            ReservationId = reservationId;
            PartySize = partySize;
            ConnectionId = connectionId;
            ElapsedMs = elapsedMs;
        }

        /// <summary>True when the connection is admitted.</summary>
        public bool Approved { get; }

        /// <summary><see cref="JoinRejectReason.None"/> when <see cref="Approved"/>.</summary>
        public JoinRejectReason Reason { get; }

        /// <summary>The wire literal of <see cref="Reason"/> (sent to the client), or null when approved.</summary>
        public string ReasonWire => Approved ? null : JoinRejectReasons.ToWire(Reason);

        /// <summary>Diagnostics only; never quotes a payload value.</summary>
        public string Detail { get; }

        /// <summary>The ticket's kind, or null when the payload did not decode (or for a listen host's own client).</summary>
        public JoinTicketKind? Kind { get; }

        /// <summary>The hosting mode it was decided in.</summary>
        public HostingMode Mode { get; }

        /// <summary>Where the evidence came from (<c>fleet</c>, <c>heartbeat</c>, <c>lan</c>, <c>host</c> for a listen host's own client), or null.</summary>
        public string EvidenceSource { get; }

        /// <summary>The ticket's <see cref="JoinTicket.TicketRef"/>, or null. Never the ticket id.</summary>
        public string TicketRef { get; }

        /// <summary>The ticket's player id, or null when the payload did not decode.</summary>
        public string PlayerId { get; }

        /// <summary>The ticket's allocation id (match, backfill), or null.</summary>
        public string AllocationId { get; }

        /// <summary>The ticket's reservation id, or null.</summary>
        public string ReservationId { get; }

        /// <summary>For an approved match or backfill ticket, the roster entry's party size; otherwise null.</summary>
        public int? PartySize { get; }

        /// <summary>The netcode connection id it was decided for (NGO's client id); 0 from <see cref="JoinAdmission.Decide"/> alone.</summary>
        public ulong ConnectionId { get; }

        /// <summary>How long the decision took, in milliseconds of scheduler time; 0 from <see cref="JoinAdmission.Decide"/> alone.</summary>
        public long ElapsedMs { get; }

        /// <summary>
        /// A hosted <c>reservation</c> join on an idle game server: what claiming its session came to
        /// (<see cref="SessionClaimOutcome.Claimed"/> on every approval of such a join); <see cref="SessionClaimOutcome.None"/> otherwise.
        /// </summary>
        public SessionClaimOutcome SessionClaim { get; }

        /// <summary>An approval.</summary>
        public static AdmissionDecision Accept(JoinTicket ticket, AdmissionFacts facts, int? partySize = null)
        {
            return new AdmissionDecision(true, JoinRejectReason.None, null, ticket?.Kind, facts?.Mode ?? default, facts?.EvidenceSource,
                ticket?.TicketRef, ticket?.PlayerId, ticket?.AllocationId, ticket?.ReservationId, partySize, 0, 0);
        }

        /// <summary>A rejection of a decoded ticket.</summary>
        public static AdmissionDecision Reject(JoinRejectReason reason, string detail, JoinTicket ticket, AdmissionFacts facts)
        {
            return new AdmissionDecision(false, reason, detail, ticket?.Kind, facts?.Mode ?? default, facts?.EvidenceSource,
                ticket?.TicketRef, ticket?.PlayerId, ticket?.AllocationId, ticket?.ReservationId, null, 0, 0);
        }

        /// <summary>A rejection before any ticket or facts exist (the payload did not decode).</summary>
        public static AdmissionDecision Reject(JoinRejectReason reason, string detail, HostingMode mode, string evidenceSource)
        {
            return new AdmissionDecision(false, reason, detail, null, mode, evidenceSource, null, null, null, null, null, 0, 0);
        }

        /// <summary>A listen host's own client, approved at once with no ticket.</summary>
        public static AdmissionDecision HostClient(HostingMode mode, ulong connectionId)
        {
            return new AdmissionDecision(true, JoinRejectReason.None, "the listen host's own client", null, mode, "host", null, null, null, null, null, connectionId, 0);
        }

        /// <summary>This decision for <paramref name="connectionId"/>, taking <paramref name="elapsedMs"/>.</summary>
        public AdmissionDecision For(ulong connectionId, long elapsedMs)
        {
            return new AdmissionDecision(Approved, Reason, Detail, Kind, Mode, EvidenceSource, TicketRef, PlayerId, AllocationId, ReservationId,
                PartySize, connectionId, elapsedMs, SessionClaim);
        }

        /// <summary>This decision with what its session claim came to.</summary>
        public AdmissionDecision WithSessionClaim(SessionClaimOutcome sessionClaim)
        {
            return new AdmissionDecision(Approved, Reason, Detail, Kind, Mode, EvidenceSource, TicketRef, PlayerId, AllocationId, ReservationId,
                PartySize, ConnectionId, ElapsedMs, sessionClaim);
        }

        /// <summary>This decision turned into a rejection (for example when the gate refuses, or the connection left), keeping what it was decided on.</summary>
        public AdmissionDecision AsRejection(JoinRejectReason reason, string detail)
        {
            return new AdmissionDecision(false, reason, detail, Kind, Mode, EvidenceSource, TicketRef, PlayerId, AllocationId, ReservationId,
                null, ConnectionId, ElapsedMs, SessionClaim);
        }

        /// <summary>Approved or the reason, with kind, mode, evidence and the ticket ref; never the ticket id.</summary>
        public override string ToString()
        {
            string kind = Kind.HasValue ? JoinTicketKinds.ToWire(Kind.Value) : "-";
            string verdict = Approved ? "approved" : "rejected " + ReasonWire;
            return verdict + " kind " + kind + " mode " + Mode + " evidence " + (EvidenceSource ?? "-")
                + (TicketRef != null ? " ticketRef " + TicketRef : string.Empty)
                + (SessionClaim != SessionClaimOutcome.None ? " sessionClaim " + SessionClaim : string.Empty) + " " + ElapsedMs + " ms";
        }
    }
}
