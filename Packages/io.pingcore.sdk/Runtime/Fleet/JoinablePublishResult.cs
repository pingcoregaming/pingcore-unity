using PingCore.Fleet.Wire;

namespace PingCore.Fleet
{
    /// <summary>
    /// The result of <see cref="IFleetSdk.PublishJoinableAsync"/>.
    /// <para>
    /// <see cref="LocallyAccepted"/> means only that the supervisor validated the record, stored
    /// its shadow copy and forwarded it to Discovery as a <c>joinableSession</c> frame. It does
    /// NOT mean Discovery accepted it: Discovery answers on the supervisor's socket, not to the
    /// game, and supervisor 1.3.4 gives the game no signal when Discovery refuses the record.
    /// Today the only place a refusal shows is the matchmaking inspector's
    /// <c>lastBackfillRefusal</c>, which a backend-scope token reads; no SDK call can.
    /// </para>
    /// </summary>
    public sealed class JoinablePublishResult : FleetCallResult
    {
        /// <summary>Creates a publish result.</summary>
        public JoinablePublishResult(FleetCallOutcome outcome, int status, string message, JoinableSessionRecord record)
            : base(outcome, status, message)
        {
            Record = record;
        }

        /// <summary>The stored shadow record the supervisor echoed (with <c>sessionId</c> set to the allocation id), or null.</summary>
        public JoinableSessionRecord Record { get; }

        /// <summary>True when the supervisor answered 2xx: stored and forwarded, not confirmed by Discovery.</summary>
        public bool LocallyAccepted => IsOk;
    }
}
