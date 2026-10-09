using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PingCore.Core.Discovery;

namespace PingCore.Core.Handshake
{
    /// <summary>
    /// One ticket in a match or backfill roster, as Discovery's matchmaker delivers it in the
    /// allocation context: <c>{ticketId, partySize, playerId, attributes, context}</c>. A game server
    /// authorises a <c>match</c> or <c>backfill</c> join ticket against it. <see cref="TicketId"/> is a
    /// bearer secret: <see cref="ToString"/> shows only its <see cref="TicketRef"/>.
    /// </summary>
    public sealed class RosterEntry
    {
        /// <summary>Creates an entry.</summary>
        /// <param name="ticketId">The matchmaking ticket id.</param>
        /// <param name="partySize">How many players the ticket brings, at least 1.</param>
        /// <param name="playerId">The submitting player's id; null for a ticket a backend submitted.</param>
        /// <param name="attributes">The ticket's numeric attributes, or null.</param>
        /// <param name="context">The ticket's own context object, or null.</param>
        public RosterEntry(string ticketId, int partySize, string playerId, IReadOnlyDictionary<string, double> attributes = null, JObject context = null)
        {
            if (string.IsNullOrEmpty(ticketId))
            {
                throw new ArgumentException("ticketId is required", nameof(ticketId));
            }

            if (partySize < 1)
            {
                throw new ArgumentException("partySize must be at least 1", nameof(partySize));
            }

            TicketId = ticketId;
            PartySize = partySize;
            PlayerId = playerId;
            Attributes = attributes;
            Context = context;
            TicketRef = SecureIds.Ref(ticketId);
        }

        /// <summary><c>ticketId</c>. A bearer secret: never log it.</summary>
        public string TicketId { get; }

        /// <summary><c>partySize</c>: how many players this ticket may bring in.</summary>
        public int PartySize { get; }

        /// <summary><c>playerId</c>: the submitting player's id, or null for a backend ticket. Informational: admission matches on <see cref="TicketId"/> alone.</summary>
        public string PlayerId { get; }

        /// <summary><c>attributes</c>, or null.</summary>
        public IReadOnlyDictionary<string, double> Attributes { get; }

        /// <summary><c>context</c>: the ticket's own context, or null.</summary>
        public JObject Context { get; }

        /// <summary>The loggable form of <see cref="TicketId"/>.</summary>
        public string TicketRef { get; }

        /// <summary>
        /// True when a join ticket with this ticket id belongs to this entry. The match is on the ticket id
        /// alone: it is a 128-bit bearer secret the SDK mints, and the roster names only the submitter
        /// (<see cref="PlayerId"/>), so the other members of a party present their own player ids.
        /// </summary>
        public bool HasTicket(string ticketId) => string.Equals(TicketId, ticketId, StringComparison.Ordinal);

        /// <summary>The ticket ref, party size and player; never the ticket id.</summary>
        public override string ToString() => "ticketRef " + TicketRef + " party " + PartySize + " player " + (PlayerId ?? "(backend)");

        /// <summary>
        /// Parses a <c>roster</c> array leniently: an entry without a non-empty string <c>ticketId</c> or a
        /// positive integer <c>partySize</c> is skipped (it can admit no one), a non-string
        /// <c>playerId</c> reads as null only when it is JSON null and skips the entry otherwise.
        /// Returns an empty list for anything that is not an array. Never throws.
        /// </summary>
        public static IReadOnlyList<RosterEntry> ParseRoster(JToken roster)
        {
            var entries = new List<RosterEntry>();
            if (!(roster is JArray array))
            {
                return entries;
            }

            foreach (JToken item in array)
            {
                RosterEntry entry = ParseEntry(item);
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }

            return entries;
        }

        /// <summary>One roster entry, or null when it cannot admit anyone (see <see cref="ParseRoster"/>).</summary>
        public static RosterEntry ParseEntry(JToken item)
        {
            if (!(item is JObject entry))
            {
                return null;
            }

            JToken ticket = entry["ticketId"];
            JToken party = entry["partySize"];
            JToken player = entry["playerId"];
            if (ticket == null || ticket.Type != JTokenType.String || string.IsNullOrEmpty((string)ticket))
            {
                return null;
            }

            if (party == null || party.Type != JTokenType.Integer || !(((JValue)party).Value is long size) || size < 1 || size > int.MaxValue)
            {
                return null;
            }

            string playerId;
            if (player == null || player.Type == JTokenType.Null)
            {
                playerId = null;
            }
            else if (player.Type == JTokenType.String && !string.IsNullOrEmpty((string)player))
            {
                playerId = (string)player;
            }
            else
            {
                return null;
            }

            return new RosterEntry((string)ticket, (int)size, playerId, ParseAttributes(entry["attributes"]), entry["context"] as JObject);
        }

        private static IReadOnlyDictionary<string, double> ParseAttributes(JToken token)
        {
            if (!(token is JObject map))
            {
                return null;
            }

            var attributes = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (JProperty property in map.Properties())
            {
                if (property.Value.Type == JTokenType.Integer || property.Value.Type == JTokenType.Float)
                {
                    attributes[property.Name] = property.Value.Value<double>();
                }
            }

            return attributes;
        }
    }
}
