namespace PingCore.Discovery.Client
{
    /// <summary>Where a matchmaking ticket stands.</summary>
    public enum TicketState
    {
        /// <summary>Waiting in its queue.</summary>
        Queued,

        /// <summary>Matched; <see cref="TicketHandle.Match"/> says where to connect.</summary>
        Matched,

        /// <summary>Gone from Discovery (expired, or never visible to this player). Submit a new ticket.</summary>
        Expired,

        /// <summary>Cancelled by this client.</summary>
        Cancelled,

        /// <summary>Polling gave up (five failures in a row, or a refusal); <see cref="TicketHandle.LastError"/> says why.</summary>
        Failed,
    }

    /// <summary>Where a matched ticket was placed.</summary>
    public sealed class MatchAssignment
    {
        /// <summary>Creates an assignment.</summary>
        public MatchAssignment(string allocationId, string serverId, string ip, int port, bool backfill, string location)
        {
            AllocationId = allocationId;
            ServerId = serverId;
            Ip = ip;
            Port = port;
            Backfill = backfill;
            Location = location;
        }

        /// <summary>The allocation the game server received (the join ticket carries it).</summary>
        public string AllocationId { get; }

        /// <summary>The game server's id.</summary>
        public string ServerId { get; }

        /// <summary>Connect address.</summary>
        public string Ip { get; }

        /// <summary>Game port.</summary>
        public int Port { get; }

        /// <summary>True when placed into a running session (connect with a <c>backfill</c> join ticket).</summary>
        public bool Backfill { get; }

        /// <summary>The placement location id, or null.</summary>
        public string Location { get; }

        /// <inheritdoc />
        public override string ToString() => $"Match(allocationId={AllocationId}, serverId={ServerId}, {Ip}:{Port}, backfill={Backfill})";
    }
}
