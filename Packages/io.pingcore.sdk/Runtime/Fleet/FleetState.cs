namespace PingCore.Fleet
{
    /// <summary>
    /// The local SDK shim's view of this game server's lifecycle. It is driven by the watch
    /// stream (the source of truth for <see cref="InSession"/>) and by the shim's own calls,
    /// which only predict. It is never the Discovery state, which the game cannot see.
    /// </summary>
    public enum FleetState
    {
        /// <summary><c>AGONES_SDK_HTTP_PORT</c> was unset or invalid at create: not a hosted game server. Terminal; every call returns without I/O.</summary>
        Inert = 0,

        /// <summary>Hosted, and <see cref="IFleetSdk.StartAsync"/> has not yet read the GameServer view.</summary>
        Starting = 1,

        /// <summary>The view was read; the game has not called Ready (Agones <c>Scheduled</c>).</summary>
        NotReady = 2,

        /// <summary>Ready and not in a session (Agones <c>Ready</c>).</summary>
        Ready = 3,

        /// <summary>An allocation is on the GameServer view (Agones <c>Allocated</c>).</summary>
        InSession = 4,

        /// <summary>Shutdown was requested (by the game, or the view says <c>Shutdown</c>). The supervisor recycles the process. Left only for <see cref="Stopping"/>.</summary>
        ShuttingDown = 5,

        /// <summary>The process is stopping (<see cref="IFleetSdk.NotifyProcessStopping"/>). Terminal.</summary>
        Stopping = 6,

        /// <summary>The local SDK endpoint did not answer at start, or the watch could not reconnect. Left on the next watch frame.</summary>
        Unreachable = 7,
    }
}
