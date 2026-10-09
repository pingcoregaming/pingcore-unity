using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;
using PingCore.Core.Discovery;

namespace PingCore.Discovery.Client
{
    /// <summary>
    /// The checks the client makes before sending, for a player-token caller, so a request the
    /// service would refuse costs no request (and no rate budget). Each refusal is
    /// <see cref="DiscoveryOutcome.InvalidRequest"/> with status 0 and the reason the service would
    /// give (Unknown where the service's 400 has none). Pure.
    /// Source: Discovery's ticket contract (the cross-field reasons, in its evaluation order), its
    /// request schemas and player reservation bounds, and its player defaults (the Discovery spec,
    /// <c>contracts/discovery/</c>).
    /// </summary>
    internal static class RequestChecks
    {
        /// <summary><c>PLAYER_MAX_SEATS</c> default.</summary>
        public const int PlayerMaxSeats = 8;

        /// <summary><c>PLAYER_CONTEXT_MAX_BYTES</c> default.</summary>
        public const int PlayerContextMaxBytes = 1024;

        /// <summary><c>PLAYER_TICKET_MAX_PARTY_SIZE</c> default.</summary>
        public const int PlayerMaxPartySize = 8;

        /// <summary><c>PLAYER_TICKET_MIN_SESSION_SIZE</c> default.</summary>
        public const int PlayerMinSessionSize = 2;

        /// <summary><c>MATCHMAKER_TICKET_TTL_SECONDS</c> default; <c>relaxAfterSeconds</c> must be below it.</summary>
        public const int TicketTtlSeconds = 300;

        /// <summary><c>config.matchmaker.maxLatencyEntries</c> default.</summary>
        public const int MaxLatencyEntries = 32;

        /// <summary>Reservation hold length bounds (schema minimum, configured maximum default).</summary>
        public const int MinTtlSeconds = 5;

        /// <summary>Reservation hold length maximum default.</summary>
        public const int MaxTtlSeconds = 300;

        /// <summary>The longest player id a reservation may name.</summary>
        public const int PlayerIdMaxLength = 128;

        /// <summary>Checks a ticket against the player floors, in the service's evaluation order; null when it may be sent.</summary>
        public static DiscoveryCallResult CheckTicket(TicketOptions o)
        {
            if (o == null)
            {
                return DiscoveryCallResult.Refused(DiscoveryReason.Unknown, "ticket options are required");
            }

            // The schema (joi) first: these 400s carry no reason.
            if (o.Queue != null && (o.Queue.Trim().Length < 1 || o.Queue.Trim().Length > 50))
            {
                return Refused(DiscoveryReason.Unknown, "\"queue\" must be 1 to 50 characters.");
            }

            if (o.SessionSize < 1 || o.SessionSize > 1000)
            {
                return Refused(DiscoveryReason.Unknown, "\"sessionSize\" must be 1 to 1000.");
            }

            if (o.PartySize < 1 || o.PartySize > 1000)
            {
                return Refused(DiscoveryReason.Unknown, "\"partySize\" must be 1 to 1000.");
            }

            if (o.MinSessionSize.HasValue && (o.MinSessionSize.Value < 1 || o.MinSessionSize.Value > 1000))
            {
                return Refused(DiscoveryReason.Unknown, "\"minSessionSize\" must be 1 to 1000.");
            }

            if (o.RelaxAfterSeconds.HasValue && (o.RelaxAfterSeconds.Value < 0 || o.RelaxAfterSeconds.Value > 86400))
            {
                return Refused(DiscoveryReason.Unknown, "\"relaxAfterSeconds\" must be 0 to 86400.");
            }

            DiscoveryCallResult latency = CheckLatencyShape(o.Latency, o.MaxLatencyMs);
            if (latency != null)
            {
                return latency;
            }

            if (o.Filters?.Meta != null)
            {
                foreach (KeyValuePair<string, string> entry in o.Filters.Meta)
                {
                    if (!MetaKeyOk(entry.Key) || entry.Value == null || entry.Value.Length > 200)
                    {
                        return Refused(DiscoveryReason.Unknown, "ticket meta filters are string values of at most 200 characters under keys of [A-Za-z0-9_.-]{1,50}.");
                    }
                }
            }

            // The cross-field contract, in Discovery's evaluation order.
            bool hasMin = o.MinSessionSize.HasValue;
            bool hasRelax = o.RelaxAfterSeconds.HasValue;
            if (o.PartySize > o.SessionSize)
            {
                return Refused(DiscoveryReason.PartyTooLarge, "partySize cannot exceed sessionSize.");
            }

            if (hasMin != hasRelax)
            {
                return Refused(DiscoveryReason.RelaxationIncomplete, "minSessionSize and relaxAfterSeconds must be sent together.");
            }

            if (hasMin && o.MinSessionSize.Value > o.SessionSize)
            {
                return Refused(DiscoveryReason.MinSessionTooLarge, "minSessionSize cannot exceed sessionSize.");
            }

            if (hasMin && o.MinSessionSize.Value < o.PartySize)
            {
                return Refused(DiscoveryReason.MinSessionBelowParty, "minSessionSize cannot be smaller than partySize: a session must hold the whole party.");
            }

            if (hasRelax && o.RelaxAfterSeconds.Value >= TicketTtlSeconds)
            {
                return Refused(DiscoveryReason.RelaxAfterTooLong, "relaxAfterSeconds must be below the ticket TTL of 300 seconds.");
            }

            int latencyCount = o.Latency?.Count ?? 0;
            if (latencyCount > MaxLatencyEntries)
            {
                return Refused(DiscoveryReason.TooManyLatencyEntries, "latency has " + latencyCount + " entries; the limit is 32.");
            }

            if (o.MaxLatencyMs.HasValue && latencyCount == 0)
            {
                return Refused(DiscoveryReason.MaxLatencyWithoutMap, "maxLatencyMs needs a latency map to apply to.");
            }

            if (o.MaxLatencyMs.HasValue)
            {
                bool anyWithin = false;
                foreach (KeyValuePair<string, int> entry in o.Latency)
                {
                    anyWithin |= entry.Value <= o.MaxLatencyMs.Value;
                }

                if (!anyWithin)
                {
                    return Refused(DiscoveryReason.NoLocationWithinCeiling, "No measured location is within maxLatencyMs.");
                }
            }

            int contextBytes = ContextBytes(o.Context);
            if (contextBytes > PlayerContextMaxBytes)
            {
                return Refused(DiscoveryReason.ContextTooLarge, "\"context\" is " + contextBytes + " bytes; the limit is 1024.");
            }

            if (o.PartySize > PlayerMaxPartySize)
            {
                return Refused(DiscoveryReason.PartyTooLargeForPlayer, "Player tokens may submit a party of at most 8 players.");
            }

            if (o.SessionSize < PlayerMinSessionSize || (hasMin && o.MinSessionSize.Value < PlayerMinSessionSize))
            {
                return Refused(DiscoveryReason.SessionTooSmallForPlayer, "Player tokens need a session of at least 2 players (sessionSize and minSessionSize).");
            }

            if (o.PartySize >= o.SessionSize || (hasMin && o.PartySize >= o.MinSessionSize.Value))
            {
                return Refused(DiscoveryReason.PartyFillsSessionForPlayer, "A player token's party cannot fill a session on its own: partySize must be smaller than both sessionSize and minSessionSize.");
            }

            return null;
        }

        /// <summary>Checks the seat fields shared by reserve and quick join; null when they may be sent.</summary>
        public static DiscoveryCallResult CheckSeats(int seats, IReadOnlyList<string> playerIds, JObject context)
        {
            if (seats < 1)
            {
                return Refused(DiscoveryReason.Unknown, "\"seats\" must be at least 1.");
            }

            if (seats > PlayerMaxSeats)
            {
                return Refused(DiscoveryReason.Unknown, "Player tokens may reserve at most 8 seats per reservation.");
            }

            if (playerIds != null)
            {
                if (playerIds.Count != seats)
                {
                    return Refused(DiscoveryReason.Unknown, "\"playerIds\" has " + playerIds.Count + " entries but \"seats\" is " + seats + "; they must match.");
                }

                foreach (string id in playerIds)
                {
                    if (string.IsNullOrEmpty(id) || id.Length > PlayerIdMaxLength)
                    {
                        return Refused(DiscoveryReason.Unknown, "each player id is 1 to 128 characters.");
                    }
                }
            }

            int contextBytes = ContextBytes(context);
            if (contextBytes > PlayerContextMaxBytes)
            {
                return Refused(DiscoveryReason.Unknown, "Player token context must be 1024 bytes or smaller.");
            }

            return null;
        }

        /// <summary>Checks a reserve call; null when it may be sent.</summary>
        public static DiscoveryCallResult CheckReserve(string serverId, ReserveOptions o)
        {
            if (string.IsNullOrEmpty(serverId) || serverId.Length > 200)
            {
                return Refused(DiscoveryReason.Unknown, "a server id is required");
            }

            if (o.ReservationId != null && !SecureIds.IsValidId(o.ReservationId))
            {
                return Refused(DiscoveryReason.Unknown, "reservationId may contain only letters, digits and _ . : - (1 to 100 characters).");
            }

            if (o.TtlSeconds.HasValue && (o.TtlSeconds.Value < MinTtlSeconds || o.TtlSeconds.Value > MaxTtlSeconds))
            {
                return Refused(DiscoveryReason.Unknown, "\"ttlSeconds\" must be 5 to 300.");
            }

            return CheckSeats(o.Seats, o.PlayerIds, o.Context);
        }

        /// <summary>Checks a quick-join call; null when it may be sent.</summary>
        public static DiscoveryCallResult CheckQuickJoin(QuickJoinOptions o)
        {
            if (o.IdempotencyKey != null && (o.IdempotencyKey.Trim().Length < 1 || o.IdempotencyKey.Trim().Length > 100))
            {
                return Refused(DiscoveryReason.Unknown, "\"idempotencyKey\" must be 1 to 100 characters.");
            }

            DiscoveryCallResult latency = CheckLatencyShape(o.Latency, o.MaxLatencyMs);
            if (latency != null)
            {
                return latency;
            }

            if (o.Filters != null)
            {
                if (o.Filters.Version != null && o.Filters.Version.Length > 200)
                {
                    return Refused(DiscoveryReason.Unknown, "\"filters.version\" is at most 200 characters.");
                }

                if (o.Filters.Meta != null && o.Filters.Meta.Count > 20)
                {
                    return Refused(DiscoveryReason.Unknown, "\"filters.meta\" has at most 20 keys.");
                }
            }

            DiscoveryCallResult seats = CheckSeats(o.Seats, o.PlayerIds, o.Context);
            if (seats != null)
            {
                return seats;
            }

            int count = o.Latency?.Count ?? 0;
            if (count > MaxLatencyEntries)
            {
                return Refused(DiscoveryReason.TooManyLatencyEntries, "latency has " + count + " entries; the limit is 32.");
            }

            if (o.MaxLatencyMs.HasValue && count == 0)
            {
                return Refused(DiscoveryReason.MaxLatencyWithoutMap, "maxLatencyMs needs a latency map to apply to.");
            }

            return null;
        }

        /// <summary>The UTF-8 size of <paramref name="context"/> as compact JSON (what Discovery measures with JSON.stringify); 0 for null.</summary>
        public static int ContextBytes(JObject context)
        {
            return context == null ? 0 : Encoding.UTF8.GetByteCount(context.ToString(Formatting.None));
        }

        private static DiscoveryCallResult CheckLatencyShape(IReadOnlyDictionary<string, int> latency, int? maxLatencyMs)
        {
            if (latency != null)
            {
                foreach (KeyValuePair<string, int> entry in latency)
                {
                    if (!LatencyKeys.IsValidLocationId(entry.Key) || entry.Value < 0 || entry.Value > LatencyKeys.MaxLatencyMs)
                    {
                        return Refused(DiscoveryReason.Unknown, "latency entries are 0 to 10000 ms under location ids of [A-Za-z0-9_-]{1,30}.");
                    }
                }
            }

            if (maxLatencyMs.HasValue && (maxLatencyMs.Value < 1 || maxLatencyMs.Value > LatencyKeys.MaxLatencyMs))
            {
                return Refused(DiscoveryReason.Unknown, "\"maxLatencyMs\" must be 1 to 10000.");
            }

            return null;
        }

        private static bool MetaKeyOk(string key) => Wire.MetaKeys.IsValid(key);

        private static DiscoveryCallResult Refused(DiscoveryReason reason, string message) => DiscoveryCallResult.Refused(reason, message);
    }
}
