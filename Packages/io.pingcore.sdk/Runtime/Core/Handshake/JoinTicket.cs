using System;
using PingCore.Core.Discovery;

namespace PingCore.Core.Handshake
{
    /// <summary>
    /// A join ticket, version 1: the connection payload a client sends to a game server
    /// (<c>contracts/handshake/join-ticket.v1.schema.json</c>). Not a matchmaking ticket.
    /// <para>
    /// Built by the factories below (which enforce the schema's field rules) or by
    /// <see cref="JoinTicketCodec.Decode"/>. <see cref="TicketId"/> is a bearer secret shared by the
    /// submitter, Discovery and the matched game server: never log it, log <see cref="TicketRef"/>.
    /// <see cref="ToString"/> leaves it out. Player tokens never travel in a join ticket.
    /// </para>
    /// </summary>
    public sealed class JoinTicket
    {
        /// <summary>The longest id the schema allows, in characters (code points).</summary>
        public const int MaxIdLength = 100;

        /// <summary>The longest <see cref="DisplayName"/> the schema allows, in characters (code points).</summary>
        public const int MaxDisplayNameLength = 32;

        internal JoinTicket(JoinTicketKind kind, int protocolVersion, string playerId, string reservationId, string ticketId,
            string allocationId, string displayName)
        {
            Kind = kind;
            ProtocolVersion = protocolVersion;
            PlayerId = playerId;
            ReservationId = reservationId;
            TicketId = ticketId;
            AllocationId = allocationId;
            DisplayName = displayName;
            TicketRef = ticketId == null ? null : SecureIds.Ref(ticketId);
        }

        /// <summary>The payload format version. Always 1: the decoder refuses anything else.</summary>
        public int Version => 1;

        /// <summary>How the player was sent here.</summary>
        public JoinTicketKind Kind { get; }

        /// <summary>The client's own network protocol version (the game's, not the platform's build version).</summary>
        public int ProtocolVersion { get; }

        /// <summary>The player id from the player-token response (for example <c>anon:&lt;uuid&gt;</c>), or a LAN id.</summary>
        public string PlayerId { get; }

        /// <summary>Set for <see cref="JoinTicketKind.Reservation"/>, else null.</summary>
        public string ReservationId { get; }

        /// <summary>Set for <see cref="JoinTicketKind.Match"/> and <see cref="JoinTicketKind.Backfill"/>, else null. A bearer secret: never log it.</summary>
        public string TicketId { get; }

        /// <summary>Set for <see cref="JoinTicketKind.Match"/> and <see cref="JoinTicketKind.Backfill"/>, else null.</summary>
        public string AllocationId { get; }

        /// <summary>Optional, null when absent. Untrusted text.</summary>
        public string DisplayName { get; }

        /// <summary>The first 12 hex characters of the SHA-256 of <see cref="TicketId"/>: the only form logs and events may carry. Null when there is no ticket id.</summary>
        public string TicketRef { get; }

        /// <summary>A <c>reservation</c> ticket: the player holds <paramref name="reservationId"/> (reserve or quick join).</summary>
        /// <exception cref="ArgumentException">A field breaks the v1 schema.</exception>
        public static JoinTicket ForReservation(string reservationId, string playerId, int protocolVersion, string displayName = null)
        {
            CheckProtocol(protocolVersion);
            CheckId(nameof(reservationId), reservationId);
            CheckId(nameof(playerId), playerId);
            CheckDisplayName(displayName);
            return new JoinTicket(JoinTicketKind.Reservation, protocolVersion, playerId, reservationId, null, null, displayName);
        }

        /// <summary>A <c>match</c> ticket: the matchmaking ticket <paramref name="ticketId"/> was matched into allocation <paramref name="allocationId"/>.</summary>
        /// <exception cref="ArgumentException">A field breaks the v1 schema.</exception>
        public static JoinTicket ForMatch(string ticketId, string allocationId, string playerId, int protocolVersion, string displayName = null)
        {
            return ForAllocation(JoinTicketKind.Match, ticketId, allocationId, playerId, protocolVersion, displayName);
        }

        /// <summary>A <c>backfill</c> ticket: the matchmaking ticket <paramref name="ticketId"/> was placed into a running session by backfill <paramref name="allocationId"/>.</summary>
        /// <exception cref="ArgumentException">A field breaks the v1 schema.</exception>
        public static JoinTicket ForBackfill(string ticketId, string allocationId, string playerId, int protocolVersion, string displayName = null)
        {
            return ForAllocation(JoinTicketKind.Backfill, ticketId, allocationId, playerId, protocolVersion, displayName);
        }

        /// <summary>A <c>lan</c> ticket, for a listen host started LAN only. <paramref name="playerId"/> is the client's own LAN id.</summary>
        /// <exception cref="ArgumentException">A field breaks the v1 schema.</exception>
        public static JoinTicket ForLan(string playerId, int protocolVersion, string displayName = null)
        {
            CheckProtocol(protocolVersion);
            CheckId(nameof(playerId), playerId);
            CheckDisplayName(displayName);
            return new JoinTicket(JoinTicketKind.Lan, protocolVersion, playerId, null, null, null, displayName);
        }

        /// <summary>Kind, protocol, player and the non-secret ids; the ticket id appears only as its <see cref="TicketRef"/>.</summary>
        public override string ToString()
        {
            string text = JoinTicketKinds.ToWire(Kind) + " v1 protocol " + ProtocolVersion + " player " + PlayerId;
            if (ReservationId != null)
            {
                text += " reservation " + ReservationId;
            }

            if (TicketRef != null)
            {
                text += " ticketRef " + TicketRef + " allocation " + AllocationId;
            }

            return text;
        }

        private static JoinTicket ForAllocation(JoinTicketKind kind, string ticketId, string allocationId, string playerId, int protocolVersion, string displayName)
        {
            CheckProtocol(protocolVersion);
            CheckId(nameof(ticketId), ticketId);
            CheckId(nameof(allocationId), allocationId);
            CheckId(nameof(playerId), playerId);
            CheckDisplayName(displayName);
            return new JoinTicket(kind, protocolVersion, playerId, null, ticketId, allocationId, displayName);
        }

        private static void CheckProtocol(int protocolVersion)
        {
            if (protocolVersion < 0)
            {
                throw new ArgumentException("protocolVersion must be 0 or more", nameof(protocolVersion));
            }
        }

        private static void CheckId(string name, string value)
        {
            int length = value == null ? 0 : JoinTicketCodec.CountCodePoints(value);
            if (length < 1 || length > MaxIdLength)
            {
                // Never quote the value: a ticket id is a bearer secret.
                throw new ArgumentException(name + " must be 1 to " + MaxIdLength + " characters", name);
            }
        }

        private static void CheckDisplayName(string displayName)
        {
            if (displayName != null && JoinTicketCodec.CountCodePoints(displayName) > MaxDisplayNameLength)
            {
                throw new ArgumentException("displayName must be at most " + MaxDisplayNameLength + " characters", nameof(displayName));
            }
        }
    }
}
