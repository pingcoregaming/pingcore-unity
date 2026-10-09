namespace PingCore.Fleet
{
    /// <summary>How one call to the local SDK endpoint ended.</summary>
    public enum FleetCallOutcome
    {
        /// <summary>A 2xx answer.</summary>
        Ok = 0,

        /// <summary>Not a hosted game server; nothing was sent.</summary>
        Inert = 1,

        /// <summary>
        /// The endpoint answered a 4xx or 5xx status other than its 501 fallback.
        /// <see cref="FleetCallResult.Status"/> and <see cref="FleetCallResult.Message"/> say why.
        /// </summary>
        Rejected = 2,

        /// <summary>
        /// The endpoint closed: the connection was refused after the endpoint had answered at least
        /// once, or any transport failure while the process is stopping or within five
        /// seconds after the watch stream closed. Expected during a container stop: the supervisor
        /// closes the endpoint (refusing new connections, while the watch stream stays open) before
        /// it signals the game. Logged at info, never as an error.
        /// </summary>
        EndpointClosed = 3,

        /// <summary>
        /// Outside a stop: a timeout, a transport failure from an endpoint that never answered, or a
        /// connection reset (the answer may be lost even though the endpoint handled the call).
        /// </summary>
        Unreachable = 4,

        /// <summary>The endpoint answered its JSON 501 fallback (or a non-JSON 404): this supervisor has no such route.</summary>
        Unsupported = 5,

        /// <summary>The caller's cancellation token fired, or the shim was disposed.</summary>
        Cancelled = 6,
    }
}
