using System;
using System.Collections.Generic;

namespace PingCore.Core
{
    /// <summary>Conversions between <see cref="DiscoveryReason"/> and its wire string.</summary>
    /// <remarks>
    /// The table is a literal, not built by reflecting over <c>[EnumMember]</c>: Core does no
    /// runtime reflection, so IL2CPP stripping cannot empty it. The EditMode test
    /// <c>DiscoveryReasonTableTests</c> fails when this table and the enum's
    /// <c>[EnumMember]</c> attributes disagree in either direction, and the DTO dump carries
    /// these values to a contract checker, which compares the dump with the pinned specs.
    /// </remarks>
    public static class DiscoveryReasons
    {
        /// <summary>Every known reason, in enum declaration order. <see cref="DiscoveryReason.Unknown"/> has no wire value.</summary>
        private static readonly KeyValuePair<string, DiscoveryReason>[] Table =
        {
            new KeyValuePair<string, DiscoveryReason>("self_hosted_cap", DiscoveryReason.SelfHostedCap),
            new KeyValuePair<string, DiscoveryReason>("ip_cap", DiscoveryReason.IpCap),
            new KeyValuePair<string, DiscoveryReason>("agent_push_unavailable", DiscoveryReason.AgentPushUnavailable),
            new KeyValuePair<string, DiscoveryReason>("party_too_large", DiscoveryReason.PartyTooLarge),
            new KeyValuePair<string, DiscoveryReason>("relaxation_incomplete", DiscoveryReason.RelaxationIncomplete),
            new KeyValuePair<string, DiscoveryReason>("min_session_too_large", DiscoveryReason.MinSessionTooLarge),
            new KeyValuePair<string, DiscoveryReason>("min_session_below_party", DiscoveryReason.MinSessionBelowParty),
            new KeyValuePair<string, DiscoveryReason>("relax_after_too_long", DiscoveryReason.RelaxAfterTooLong),
            new KeyValuePair<string, DiscoveryReason>("rule_without_attribute", DiscoveryReason.RuleWithoutAttribute),
            new KeyValuePair<string, DiscoveryReason>("too_many_attributes", DiscoveryReason.TooManyAttributes),
            new KeyValuePair<string, DiscoveryReason>("too_many_latency_entries", DiscoveryReason.TooManyLatencyEntries),
            new KeyValuePair<string, DiscoveryReason>("max_latency_without_map", DiscoveryReason.MaxLatencyWithoutMap),
            new KeyValuePair<string, DiscoveryReason>("no_location_within_ceiling", DiscoveryReason.NoLocationWithinCeiling),
            new KeyValuePair<string, DiscoveryReason>("context_too_large", DiscoveryReason.ContextTooLarge),
            new KeyValuePair<string, DiscoveryReason>("party_too_large_for_player", DiscoveryReason.PartyTooLargeForPlayer),
            new KeyValuePair<string, DiscoveryReason>("session_too_small_for_player", DiscoveryReason.SessionTooSmallForPlayer),
            new KeyValuePair<string, DiscoveryReason>("party_fills_session_for_player", DiscoveryReason.PartyFillsSessionForPlayer),
            new KeyValuePair<string, DiscoveryReason>("invalid_ticket_id", DiscoveryReason.InvalidTicketId),
            new KeyValuePair<string, DiscoveryReason>("too_many_tickets", DiscoveryReason.TooManyTickets),
            new KeyValuePair<string, DiscoveryReason>("ticket_id_taken", DiscoveryReason.TicketIdTaken),
            new KeyValuePair<string, DiscoveryReason>("too_many_open_seats", DiscoveryReason.TooManyOpenSeats),
            new KeyValuePair<string, DiscoveryReason>("ttl_too_long", DiscoveryReason.TtlTooLong),
            new KeyValuePair<string, DiscoveryReason>("seats_exceed_capacity", DiscoveryReason.SeatsExceedCapacity),
            new KeyValuePair<string, DiscoveryReason>("server_not_in_session", DiscoveryReason.ServerNotInSession),
            new KeyValuePair<string, DiscoveryReason>("seats_unavailable", DiscoveryReason.SeatsUnavailable),
            new KeyValuePair<string, DiscoveryReason>("counter_full", DiscoveryReason.CounterFull),
            new KeyValuePair<string, DiscoveryReason>("no_capacity", DiscoveryReason.NoCapacity),
            new KeyValuePair<string, DiscoveryReason>("scope", DiscoveryReason.Scope),
            new KeyValuePair<string, DiscoveryReason>("anonymous_tokens_disabled", DiscoveryReason.AnonymousTokensDisabled),
            new KeyValuePair<string, DiscoveryReason>("anonymous_tokens_unavailable", DiscoveryReason.AnonymousTokensUnavailable),
            new KeyValuePair<string, DiscoveryReason>("no_seats", DiscoveryReason.NoSeats),
            new KeyValuePair<string, DiscoveryReason>("too_many_reservations", DiscoveryReason.TooManyReservations),
            new KeyValuePair<string, DiscoveryReason>("reservation_id_taken", DiscoveryReason.ReservationIdTaken),
            new KeyValuePair<string, DiscoveryReason>("wrong_server", DiscoveryReason.WrongServer),
            new KeyValuePair<string, DiscoveryReason>("not_in_reservation", DiscoveryReason.NotInReservation),
            new KeyValuePair<string, DiscoveryReason>("malformed_token", DiscoveryReason.MalformedToken),
            new KeyValuePair<string, DiscoveryReason>("unsupported_alg", DiscoveryReason.UnsupportedAlg),
            new KeyValuePair<string, DiscoveryReason>("no_signing_keys", DiscoveryReason.NoSigningKeys),
            new KeyValuePair<string, DiscoveryReason>("unknown_kid", DiscoveryReason.UnknownKid),
            new KeyValuePair<string, DiscoveryReason>("bad_signature", DiscoveryReason.BadSignature),
            new KeyValuePair<string, DiscoveryReason>("missing_claim", DiscoveryReason.MissingClaim),
            new KeyValuePair<string, DiscoveryReason>("invalid_sub", DiscoveryReason.InvalidSub),
            new KeyValuePair<string, DiscoveryReason>("invalid_claims", DiscoveryReason.InvalidClaims),
            new KeyValuePair<string, DiscoveryReason>("audience_mismatch", DiscoveryReason.AudienceMismatch),
            new KeyValuePair<string, DiscoveryReason>("token_expired", DiscoveryReason.TokenExpired),
            new KeyValuePair<string, DiscoveryReason>("token_not_yet_valid", DiscoveryReason.TokenNotYetValid),
            new KeyValuePair<string, DiscoveryReason>("lifetime_too_long", DiscoveryReason.LifetimeTooLong),
            new KeyValuePair<string, DiscoveryReason>("retiring", DiscoveryReason.Retiring),
            new KeyValuePair<string, DiscoveryReason>("shutdown", DiscoveryReason.Shutdown),
            new KeyValuePair<string, DiscoveryReason>("unhealthy", DiscoveryReason.Unhealthy),
            new KeyValuePair<string, DiscoveryReason>("booting", DiscoveryReason.Booting),
            new KeyValuePair<string, DiscoveryReason>("signed_tokens_disabled", DiscoveryReason.SignedTokensDisabled),
        };

        private static readonly Dictionary<string, DiscoveryReason> ByWireValue = BuildForward();
        private static readonly Dictionary<DiscoveryReason, string> WireValueByReason = BuildReverse();

        /// <summary>
        /// Maps a wire <c>reason</c> to its enum value. Null, empty and unknown values map to
        /// <see cref="DiscoveryReason.Unknown"/>, so a reason added by a newer Discovery never
        /// throws in a shipped game. Matching is exact (ordinal, case-sensitive).
        /// </summary>
        public static DiscoveryReason Parse(string wireValue)
        {
            if (string.IsNullOrEmpty(wireValue))
            {
                return DiscoveryReason.Unknown;
            }

            return ByWireValue.TryGetValue(wireValue, out DiscoveryReason reason) ? reason : DiscoveryReason.Unknown;
        }

        /// <summary>The wire string for <paramref name="reason"/>, or null for <see cref="DiscoveryReason.Unknown"/>.</summary>
        public static string ToWireValue(DiscoveryReason reason)
        {
            return WireValueByReason.TryGetValue(reason, out string value) ? value : null;
        }

        /// <summary>Every known wire value, in enum declaration order.</summary>
        public static IReadOnlyList<string> AllWireValues()
        {
            var values = new List<string>(Table.Length);
            foreach (KeyValuePair<string, DiscoveryReason> entry in Table)
            {
                values.Add(entry.Key);
            }

            return values;
        }

        private static Dictionary<string, DiscoveryReason> BuildForward()
        {
            var forward = new Dictionary<string, DiscoveryReason>(Table.Length, StringComparer.Ordinal);
            foreach (KeyValuePair<string, DiscoveryReason> entry in Table)
            {
                forward.Add(entry.Key, entry.Value);
            }

            return forward;
        }

        private static Dictionary<DiscoveryReason, string> BuildReverse()
        {
            var reverse = new Dictionary<DiscoveryReason, string>(Table.Length);
            foreach (KeyValuePair<string, DiscoveryReason> entry in Table)
            {
                reverse.Add(entry.Value, entry.Key);
            }

            return reverse;
        }
    }
}
