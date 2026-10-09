using System.Collections.Generic;

namespace PingCore.Core.Handshake
{
    /// <summary>
    /// Why a game server refused a connection. <see cref="JoinRejectReasons.ToWire"/> gives the
    /// literal the game server sends to the client as the refusal reason (on NGO, the disconnect
    /// reason) and writes to its approval event; a client reads it back with
    /// <see cref="JoinRejectReasons.TryParse"/>. The literals are the reject reasons listed at
    /// https://pingcore.io/docs/fleets/admitting-players.
    /// </summary>
    public enum JoinRejectReason
    {
        /// <summary>Not a rejection: the connection was admitted.</summary>
        None = 0,

        /// <summary><c>payload_empty</c>: the connection carried no payload.</summary>
        PayloadEmpty = 1,

        /// <summary><c>payload_too_large</c>: the payload is over 1024 bytes.</summary>
        PayloadTooLarge = 2,

        /// <summary><c>payload_malformed</c>: not strict UTF-8, not one JSON object, or a key appears twice.</summary>
        PayloadMalformed = 3,

        /// <summary><c>payload_invalid</c>: a JSON object that breaks the v1 schema (a missing, extra, mistyped or over-long field, or a kind without its ids).</summary>
        PayloadInvalid = 4,

        /// <summary><c>unsupported_version</c>: <c>v</c> is a number other than 1.</summary>
        UnsupportedVersion = 5,

        /// <summary><c>kind_not_accepted</c>: a valid ticket of a kind this hosting mode never accepts.</summary>
        KindNotAccepted = 6,

        /// <summary><c>protocol_mismatch</c>: the ticket's <c>protocolVersion</c> is not the game's.</summary>
        ProtocolMismatch = 7,

        /// <summary><c>not_in_session</c>: the game's gate; there is no open session to join.</summary>
        NotInSession = 8,

        /// <summary><c>allocation_mismatch</c>: the ticket names another allocation than the open session's.</summary>
        AllocationMismatch = 9,

        /// <summary><c>server_full</c>: the game's gate; every seat is taken.</summary>
        ServerFull = 10,

        /// <summary><c>reservation_invalid</c>: the hold is unknown, released, expired, on another game server, or does not name this player.</summary>
        ReservationInvalid = 11,

        /// <summary><c>reservation_unverifiable</c>: the evidence could not be read (local SDK endpoint unreachable, Discovery rate-limited or degraded). Worth retrying.</summary>
        ReservationUnverifiable = 12,

        /// <summary><c>not_in_roster</c>: the <c>ticketId</c> is not in the allocation's or backfill's roster, matched on the ticket id alone (a matchmaker allocation with an empty roster included, and a supervisor self-allocation unless <see cref="ApprovalOptions.AllowSelfAllocatedJoins"/>).</summary>
        NotInRoster = 13,

        /// <summary><c>roster_full</c>: the ticket has already brought <c>partySize</c> players in, a hold that names no players has every seat taken, or an allocation without a roster already holds its player cap.</summary>
        RosterFull = 14,

        /// <summary><c>backfill_unknown</c>: no delivered backfill with this <c>allocationId</c> joins the open session, even after the wait.</summary>
        BackfillUnknown = 15,

        /// <summary><c>duplicate_player</c>: this <c>playerId</c> already has a live connection.</summary>
        DuplicatePlayer = 16,

        /// <summary><c>approval_timeout</c>: the decision did not finish within the deadline (10 s by default).</summary>
        ApprovalTimeout = 17,

        /// <summary><c>stopping</c>: the game server is shutting down and takes no one new.</summary>
        Stopping = 18,

        /// <summary><c>refused_by_game</c>: the game's gate refused for a reason of its own.</summary>
        RefusedByGame = 19,
    }

    /// <summary>The literal table of <see cref="JoinRejectReason"/>. No reflection: the table is written out, and a test pins it to the enum.</summary>
    public static class JoinRejectReasons
    {
        private static readonly (JoinRejectReason Reason, string Wire)[] Table =
        {
            (JoinRejectReason.None, "none"),
            (JoinRejectReason.PayloadEmpty, "payload_empty"),
            (JoinRejectReason.PayloadTooLarge, "payload_too_large"),
            (JoinRejectReason.PayloadMalformed, "payload_malformed"),
            (JoinRejectReason.PayloadInvalid, "payload_invalid"),
            (JoinRejectReason.UnsupportedVersion, "unsupported_version"),
            (JoinRejectReason.KindNotAccepted, "kind_not_accepted"),
            (JoinRejectReason.ProtocolMismatch, "protocol_mismatch"),
            (JoinRejectReason.NotInSession, "not_in_session"),
            (JoinRejectReason.AllocationMismatch, "allocation_mismatch"),
            (JoinRejectReason.ServerFull, "server_full"),
            (JoinRejectReason.ReservationInvalid, "reservation_invalid"),
            (JoinRejectReason.ReservationUnverifiable, "reservation_unverifiable"),
            (JoinRejectReason.NotInRoster, "not_in_roster"),
            (JoinRejectReason.RosterFull, "roster_full"),
            (JoinRejectReason.BackfillUnknown, "backfill_unknown"),
            (JoinRejectReason.DuplicatePlayer, "duplicate_player"),
            (JoinRejectReason.ApprovalTimeout, "approval_timeout"),
            (JoinRejectReason.Stopping, "stopping"),
            (JoinRejectReason.RefusedByGame, "refused_by_game"),
        };

        /// <summary>The wire literal; <c>unknown</c> for a value outside the table.</summary>
        public static string ToWire(JoinRejectReason reason)
        {
            foreach ((JoinRejectReason r, string wire) in Table)
            {
                if (r == reason)
                {
                    return wire;
                }
            }

            return "unknown";
        }

        /// <summary>Parses a literal a game server sent (for example NGO's <c>DisconnectReason</c>); false for anything else, <c>none</c> included.</summary>
        public static bool TryParse(string wire, out JoinRejectReason reason)
        {
            foreach ((JoinRejectReason r, string literal) in Table)
            {
                if (r != JoinRejectReason.None && literal == wire)
                {
                    reason = r;
                    return true;
                }
            }

            reason = JoinRejectReason.None;
            return false;
        }

        /// <summary>Every rejection literal (not <c>none</c>), in table order.</summary>
        public static IReadOnlyList<string> AllWireValues()
        {
            var values = new List<string>(Table.Length - 1);
            foreach ((JoinRejectReason r, string wire) in Table)
            {
                if (r != JoinRejectReason.None)
                {
                    values.Add(wire);
                }
            }

            return values;
        }

        /// <summary>Every value in the table, <see cref="JoinRejectReason.None"/> first.</summary>
        public static IReadOnlyList<JoinRejectReason> AllReasons()
        {
            var values = new List<JoinRejectReason>(Table.Length);
            foreach ((JoinRejectReason r, string _) in Table)
            {
                values.Add(r);
            }

            return values;
        }
    }
}
