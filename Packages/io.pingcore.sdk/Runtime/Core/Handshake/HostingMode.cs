namespace PingCore.Core.Handshake
{
    /// <summary>
    /// How this game server is hosted. Chosen at startup, never from a ticket: a client cannot
    /// talk a game server into another mode.
    /// </summary>
    public enum HostingMode
    {
        /// <summary>PingCore-hosted: the local SDK endpoint answers. Evidence comes from the local SDK shim (holds, allocations, backfills); never verify.</summary>
        Hosted = 0,

        /// <summary>Self-hosted dedicated: no local SDK endpoint, a heartbeat token at runtime, reservations checked with verify.</summary>
        SelfHosted = 1,

        /// <summary>A player build that hosts: online (heartbeat and verify, as self-hosted) or LAN only (no Discovery at all, <see cref="ApprovalOptions.LanOnly"/>).</summary>
        Listen = 2,
    }
}
