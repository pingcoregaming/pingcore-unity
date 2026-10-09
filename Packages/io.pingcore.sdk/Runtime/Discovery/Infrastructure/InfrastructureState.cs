namespace PingCore.Discovery.Client
{
    /// <summary>
    /// What a game client can tell about its PingCore backend from Discovery alone, with the public app id
    /// (<see cref="InfrastructureCheck"/>). The four failure states and their messages are the hosting spec's; a client
    /// never sees the workspace, so it can say which part is missing but not why (in the Editor, the plugin adds that:
    /// <see cref="InfrastructureReport.EditorDetail"/>).
    /// </summary>
    public enum InfrastructureState
    {
        /// <summary>The app is known and lists at least one game server.</summary>
        Ok = 0,

        /// <summary>The client settings asset holds no app id: the game was never connected to a fleet.</summary>
        NoAppId = 1,

        /// <summary>
        /// Discovery does not know the app id, or the app is disabled: the server list answered 404 with no
        /// <c>reason</c> (the spec's <c>UnknownApp</c> answer, which is the same for both by design), or the id is not
        /// a <c>dscp_</c> public id at all.
        /// </summary>
        AppUnknown = 2,

        /// <summary>The app is known, but its server list holds no game server (<c>totalServers</c> 0).</summary>
        NoGameServers = 3,

        /// <summary>No answer from Discovery: a transport error (connection, DNS, timeout).</summary>
        Unreachable = 4,

        /// <summary>
        /// Any other answer: a 503, a 429, an unexpected status, a body that does not parse, a 404 that carries a
        /// <c>reason</c> this SDK does not know, or a cancelled check. Never read as "no game servers".
        /// </summary>
        DiscoveryError = 5,
    }
}
