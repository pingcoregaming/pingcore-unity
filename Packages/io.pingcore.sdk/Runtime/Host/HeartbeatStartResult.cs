using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Discovery.Host.Wire;

namespace PingCore.Discovery.Host
{
    /// <summary>How <see cref="HeartbeatReporter.StartAsync"/> ended.</summary>
    public enum HeartbeatStartOutcome
    {
        /// <summary>Discovery accepted the first heartbeat; the loop runs and <see cref="HeartbeatReporter.ServerId"/> is set.</summary>
        Started = 0,

        /// <summary>
        /// Discovery refused this NEW game server (409): <see cref="HeartbeatStartResult.Reason"/> is
        /// <c>self_hosted_cap</c> (the app's self-hosted allowance; <c>limit</c> 0 means self-hosting
        /// is off for the app) or <c>ip_cap</c> (the open-registration per-source-IP cap). Nothing more
        /// is sent. Game servers already listed keep heartbeating.
        /// </summary>
        Refused = 1,

        /// <summary>
        /// <c>AGONES_SDK_HTTP_PORT</c> names a port: this is a PingCore-hosted game server, which reaches
        /// Discovery through the supervisor and never heartbeats. Nothing was sent.
        /// </summary>
        LocalSdkEndpointPresent = 2,

        /// <summary>
        /// Anything else: invalid options, already started or disposed (nothing sent, status 0), or a
        /// first heartbeat that failed (<see cref="HeartbeatStartResult.Call"/> says how). Nothing is
        /// retried; call <see cref="HeartbeatReporter.StartAsync"/> again to retry.
        /// </summary>
        Failed = 3,
    }

    /// <summary>The result of <see cref="HeartbeatReporter.StartAsync"/>.</summary>
    public sealed class HeartbeatStartResult
    {
        /// <summary>Creates a result.</summary>
        public HeartbeatStartResult(HeartbeatStartOutcome outcome, DiscoveryCallResult call, HeartbeatResponse response, int? limit)
        {
            Outcome = outcome;
            Call = call;
            Response = response;
            Limit = limit;
        }

        /// <summary>How the start ended.</summary>
        public HeartbeatStartOutcome Outcome { get; }

        /// <summary>The first heartbeat's call result, or a local result when nothing was sent.</summary>
        public DiscoveryCallResult Call { get; }

        /// <summary>The first heartbeat's answer on <see cref="HeartbeatStartOutcome.Started"/>; null otherwise.</summary>
        public HeartbeatResponse Response { get; }

        /// <summary>The refusal's <c>reason</c> (<see cref="DiscoveryReason.SelfHostedCap"/> or <see cref="DiscoveryReason.IpCap"/>); Unknown otherwise.</summary>
        public DiscoveryReason Reason => Call == null ? DiscoveryReason.Unknown : Call.Reason;

        /// <summary>The refusal's <c>limit</c>; null otherwise.</summary>
        public int? Limit { get; }

        /// <summary>True for <see cref="HeartbeatStartOutcome.Started"/>.</summary>
        public bool IsStarted => Outcome == HeartbeatStartOutcome.Started;

        /// <inheritdoc />
        public override string ToString()
        {
            if (Outcome == HeartbeatStartOutcome.Refused)
            {
                return "Refused " + (Call == null ? null : Call.ReasonWire) + " limit " + Limit;
            }

            return Call == null ? Outcome.ToString() : Outcome + " (" + Call + ")";
        }
    }
}
