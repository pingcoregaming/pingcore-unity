namespace PingCore.Core.Handshake
{
    /// <summary>How a player was sent to this game server (<c>contracts/handshake/join-ticket.v1.schema.json</c> <c>kind</c>).</summary>
    public enum JoinTicketKind
    {
        /// <summary><c>reservation</c>: a Discovery reservation or quick join.</summary>
        Reservation = 0,

        /// <summary><c>match</c>: a matchmaking allocation into a new session.</summary>
        Match = 1,

        /// <summary><c>backfill</c>: a backfill allocation into a running session.</summary>
        Backfill = 2,

        /// <summary><c>lan</c>: direct connect to a listen host started LAN only.</summary>
        Lan = 3,
    }

    /// <summary>The wire literals of <see cref="JoinTicketKind"/>.</summary>
    public static class JoinTicketKinds
    {
        /// <summary>The <c>kind</c> literal; <c>unknown</c> for a value outside the enum.</summary>
        public static string ToWire(JoinTicketKind kind)
        {
            switch (kind)
            {
                case JoinTicketKind.Reservation:
                    return "reservation";
                case JoinTicketKind.Match:
                    return "match";
                case JoinTicketKind.Backfill:
                    return "backfill";
                case JoinTicketKind.Lan:
                    return "lan";
                default:
                    return "unknown";
            }
        }

        /// <summary>Parses a <c>kind</c> literal exactly (case-sensitive).</summary>
        public static bool TryParse(string wire, out JoinTicketKind kind)
        {
            switch (wire)
            {
                case "reservation":
                    kind = JoinTicketKind.Reservation;
                    return true;
                case "match":
                    kind = JoinTicketKind.Match;
                    return true;
                case "backfill":
                    kind = JoinTicketKind.Backfill;
                    return true;
                case "lan":
                    kind = JoinTicketKind.Lan;
                    return true;
                default:
                    kind = default;
                    return false;
            }
        }
    }
}
