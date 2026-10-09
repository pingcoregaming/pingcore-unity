using System.Runtime.Serialization;

namespace PingCore.Core
{
    /// <summary>
    /// Every machine-readable <c>reason</c> value in the pinned Discovery spec
    /// (<c>contracts/discovery/openapi.json</c>, API 1.5.1), in the order the spec first
    /// lists them, plus <see cref="Unknown"/> for a value this SDK version does not know.
    /// A contract checker compares the DTO dump with the pinned specs and fails when the spec gains a
    /// value missing here.
    /// Wire bodies keep <c>reason</c> as a string; parse it with <see cref="DiscoveryReasons.Parse"/>.
    /// </summary>
    public enum DiscoveryReason
    {
        /// <summary>No reason, or a value this SDK version does not know.</summary>
        Unknown = 0,

        /// <summary>Wire value <c>self_hosted_cap</c>.</summary>
        [EnumMember(Value = "self_hosted_cap")]
        SelfHostedCap = 1,

        /// <summary>Wire value <c>ip_cap</c>.</summary>
        [EnumMember(Value = "ip_cap")]
        IpCap = 2,

        /// <summary>Wire value <c>agent_push_unavailable</c>.</summary>
        [EnumMember(Value = "agent_push_unavailable")]
        AgentPushUnavailable = 3,

        /// <summary>Wire value <c>party_too_large</c>.</summary>
        [EnumMember(Value = "party_too_large")]
        PartyTooLarge = 4,

        /// <summary>Wire value <c>relaxation_incomplete</c>.</summary>
        [EnumMember(Value = "relaxation_incomplete")]
        RelaxationIncomplete = 5,

        /// <summary>Wire value <c>min_session_too_large</c>.</summary>
        [EnumMember(Value = "min_session_too_large")]
        MinSessionTooLarge = 6,

        /// <summary>Wire value <c>min_session_below_party</c>.</summary>
        [EnumMember(Value = "min_session_below_party")]
        MinSessionBelowParty = 7,

        /// <summary>Wire value <c>relax_after_too_long</c>.</summary>
        [EnumMember(Value = "relax_after_too_long")]
        RelaxAfterTooLong = 8,

        /// <summary>Wire value <c>rule_without_attribute</c>.</summary>
        [EnumMember(Value = "rule_without_attribute")]
        RuleWithoutAttribute = 9,

        /// <summary>Wire value <c>too_many_attributes</c>.</summary>
        [EnumMember(Value = "too_many_attributes")]
        TooManyAttributes = 10,

        /// <summary>Wire value <c>too_many_latency_entries</c>.</summary>
        [EnumMember(Value = "too_many_latency_entries")]
        TooManyLatencyEntries = 11,

        /// <summary>Wire value <c>max_latency_without_map</c>.</summary>
        [EnumMember(Value = "max_latency_without_map")]
        MaxLatencyWithoutMap = 12,

        /// <summary>Wire value <c>no_location_within_ceiling</c>.</summary>
        [EnumMember(Value = "no_location_within_ceiling")]
        NoLocationWithinCeiling = 13,

        /// <summary>Wire value <c>context_too_large</c>.</summary>
        [EnumMember(Value = "context_too_large")]
        ContextTooLarge = 14,

        /// <summary>Wire value <c>party_too_large_for_player</c>.</summary>
        [EnumMember(Value = "party_too_large_for_player")]
        PartyTooLargeForPlayer = 15,

        /// <summary>Wire value <c>session_too_small_for_player</c>.</summary>
        [EnumMember(Value = "session_too_small_for_player")]
        SessionTooSmallForPlayer = 16,

        /// <summary>Wire value <c>party_fills_session_for_player</c>.</summary>
        [EnumMember(Value = "party_fills_session_for_player")]
        PartyFillsSessionForPlayer = 17,

        /// <summary>Wire value <c>invalid_ticket_id</c>.</summary>
        [EnumMember(Value = "invalid_ticket_id")]
        InvalidTicketId = 18,

        /// <summary>Wire value <c>too_many_tickets</c>.</summary>
        [EnumMember(Value = "too_many_tickets")]
        TooManyTickets = 19,

        /// <summary>Wire value <c>ticket_id_taken</c>.</summary>
        [EnumMember(Value = "ticket_id_taken")]
        TicketIdTaken = 20,

        /// <summary>Wire value <c>too_many_open_seats</c>.</summary>
        [EnumMember(Value = "too_many_open_seats")]
        TooManyOpenSeats = 21,

        /// <summary>Wire value <c>ttl_too_long</c>.</summary>
        [EnumMember(Value = "ttl_too_long")]
        TtlTooLong = 22,

        /// <summary>Wire value <c>seats_exceed_capacity</c>.</summary>
        [EnumMember(Value = "seats_exceed_capacity")]
        SeatsExceedCapacity = 23,

        /// <summary>Wire value <c>server_not_in_session</c>.</summary>
        [EnumMember(Value = "server_not_in_session")]
        ServerNotInSession = 24,

        /// <summary>Wire value <c>seats_unavailable</c>.</summary>
        [EnumMember(Value = "seats_unavailable")]
        SeatsUnavailable = 25,

        /// <summary>Wire value <c>counter_full</c>.</summary>
        [EnumMember(Value = "counter_full")]
        CounterFull = 26,

        /// <summary>Wire value <c>no_capacity</c>.</summary>
        [EnumMember(Value = "no_capacity")]
        NoCapacity = 27,

        /// <summary>Wire value <c>scope</c>.</summary>
        [EnumMember(Value = "scope")]
        Scope = 28,

        /// <summary>Wire value <c>anonymous_tokens_disabled</c>.</summary>
        [EnumMember(Value = "anonymous_tokens_disabled")]
        AnonymousTokensDisabled = 29,

        /// <summary>Wire value <c>anonymous_tokens_unavailable</c>.</summary>
        [EnumMember(Value = "anonymous_tokens_unavailable")]
        AnonymousTokensUnavailable = 30,

        /// <summary>Wire value <c>no_seats</c>.</summary>
        [EnumMember(Value = "no_seats")]
        NoSeats = 31,

        /// <summary>Wire value <c>too_many_reservations</c>.</summary>
        [EnumMember(Value = "too_many_reservations")]
        TooManyReservations = 32,

        /// <summary>Wire value <c>reservation_id_taken</c>.</summary>
        [EnumMember(Value = "reservation_id_taken")]
        ReservationIdTaken = 33,

        /// <summary>Wire value <c>wrong_server</c>.</summary>
        [EnumMember(Value = "wrong_server")]
        WrongServer = 34,

        /// <summary>Wire value <c>not_in_reservation</c>.</summary>
        [EnumMember(Value = "not_in_reservation")]
        NotInReservation = 35,

        /// <summary>Wire value <c>malformed_token</c>.</summary>
        [EnumMember(Value = "malformed_token")]
        MalformedToken = 36,

        /// <summary>Wire value <c>unsupported_alg</c>.</summary>
        [EnumMember(Value = "unsupported_alg")]
        UnsupportedAlg = 37,

        /// <summary>Wire value <c>no_signing_keys</c>.</summary>
        [EnumMember(Value = "no_signing_keys")]
        NoSigningKeys = 38,

        /// <summary>Wire value <c>unknown_kid</c>.</summary>
        [EnumMember(Value = "unknown_kid")]
        UnknownKid = 39,

        /// <summary>Wire value <c>bad_signature</c>.</summary>
        [EnumMember(Value = "bad_signature")]
        BadSignature = 40,

        /// <summary>Wire value <c>missing_claim</c>.</summary>
        [EnumMember(Value = "missing_claim")]
        MissingClaim = 41,

        /// <summary>Wire value <c>invalid_sub</c>.</summary>
        [EnumMember(Value = "invalid_sub")]
        InvalidSub = 42,

        /// <summary>Wire value <c>invalid_claims</c>.</summary>
        [EnumMember(Value = "invalid_claims")]
        InvalidClaims = 43,

        /// <summary>Wire value <c>audience_mismatch</c>.</summary>
        [EnumMember(Value = "audience_mismatch")]
        AudienceMismatch = 44,

        /// <summary>Wire value <c>token_expired</c>.</summary>
        [EnumMember(Value = "token_expired")]
        TokenExpired = 45,

        /// <summary>Wire value <c>token_not_yet_valid</c>.</summary>
        [EnumMember(Value = "token_not_yet_valid")]
        TokenNotYetValid = 46,

        /// <summary>Wire value <c>lifetime_too_long</c>.</summary>
        [EnumMember(Value = "lifetime_too_long")]
        LifetimeTooLong = 47,

        /// <summary>Wire value <c>retiring</c>.</summary>
        [EnumMember(Value = "retiring")]
        Retiring = 48,

        /// <summary>Wire value <c>shutdown</c>.</summary>
        [EnumMember(Value = "shutdown")]
        Shutdown = 49,

        /// <summary>Wire value <c>unhealthy</c>.</summary>
        [EnumMember(Value = "unhealthy")]
        Unhealthy = 50,

        /// <summary>Wire value <c>booting</c>.</summary>
        [EnumMember(Value = "booting")]
        Booting = 51,

        /// <summary>Wire value <c>signed_tokens_disabled</c>.</summary>
        [EnumMember(Value = "signed_tokens_disabled")]
        SignedTokensDisabled = 52,
    }
}
