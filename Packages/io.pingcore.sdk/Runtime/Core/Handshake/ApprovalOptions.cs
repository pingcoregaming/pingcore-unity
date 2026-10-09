using System;

namespace PingCore.Core.Handshake
{
    /// <summary>How a game server admits connections. Read once, when the approval is created.</summary>
    public sealed class ApprovalOptions
    {
        /// <summary>The game's own network protocol version; a ticket with another one is <c>protocol_mismatch</c>.</summary>
        public int ProtocolVersion { get; set; }

        /// <summary>How this game server is hosted. Chosen at startup, never from a ticket.</summary>
        public HostingMode Mode { get; set; }

        /// <summary>A listen host started LAN only: takes <c>lan</c> tickets only and talks to no Discovery. Only with <see cref="HostingMode.Listen"/>.</summary>
        public bool LanOnly { get; set; }

        /// <summary>
        /// Hosted: how many players a rosterless allocation (a backend allocation, or a self-allocation with
        /// <see cref="AllowSelfAllocatedJoins"/>, admitted on its <c>allocationId</c> alone) takes when the <c>players</c> counter has no capacity to read. Default 0,
        /// no limit.
        /// </summary>
        public int MaxPlayers { get; set; }

        /// <summary>
        /// Hosted: admit <c>match</c> joins into a supervisor self-allocation (<c>POST /allocate</c>, id
        /// <c>self-&lt;ms&gt;</c>, empty context) on its <c>allocationId</c> alone. Default false: a
        /// self-allocation is never rosterless, so every <c>match</c> ticket for it is <c>not_in_roster</c>.
        /// Its id is the supervisor's clock in milliseconds, which anyone can guess, so turn this on only for
        /// local testing, or in a game that self-allocates and accepts that any player who can reach the game
        /// server can join the session. A backend allocation without a roster is admitted on its id either way.
        /// </summary>
        public bool AllowSelfAllocatedJoins { get; set; }

        /// <summary>
        /// Hosted: claim an idle game server for the session a <c>reservation</c> join starts (the solo join). Default false.
        /// When set, a <c>reservation</c> join that the table and the gate accepted while no allocation is current is approved
        /// only after the evidence claimed a session (<see cref="ISessionClaimEvidence"/>; on PingCore a self-allocation,
        /// <c>POST /allocate</c>), so the matchmaker stops allocating the game server before the player is let in.
        /// <para>
        /// The game then owns that session: it must end it (<c>EndSessionAsync</c> with the self-allocation's id) when its
        /// players are done, or the game server stays <c>in_session</c> with <c>sessions</c> 1 and is never allocated again.
        /// A claim can also land for a joiner who is then refused (out of time, the connection closed): the game must end a
        /// claimed session that nobody joined too, for example with a short lobby timeout.
        /// </para>
        /// <para>
        /// Off (the default), nothing is claimed: an idle reservation goes to the gate as it is. A gate that admits it seats
        /// a player on a game server the matchmaker can still allocate over; answer <c>not_in_session</c> while no session is
        /// open to keep idle game servers for the matchmaker alone.
        /// </para>
        /// </summary>
        public bool ClaimIdleSessions { get; set; }

        /// <summary>The whole decision's deadline (<c>approval_timeout</c>). Default 10 s; the netcode's pending-connection timeout must stay above it.</summary>
        public TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>How long a <c>backfill</c> join waits for its backfill to be delivered (the joiner can outrun the annotation). Default 5 s.</summary>
        public TimeSpan BackfillWait { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>How often the backfill wait asks the local SDK endpoint again. Default 500 ms.</summary>
        public TimeSpan BackfillPoll { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>Whether an approved connection gets a player object (NGO's <c>CreatePlayerObject</c>). Default true.</summary>
        public bool CreatePlayerObject { get; set; } = true;

        /// <summary>The scheduler for the deadline and the waits; null means the engine adapter's default (the Unity main-thread scheduler).</summary>
        public PingCore.Core.IScheduler Scheduler { get; set; }

        /// <summary>A copy, so the approval keeps the values it was created with.</summary>
        public ApprovalOptions Clone() => (ApprovalOptions)MemberwiseClone();
    }
}
