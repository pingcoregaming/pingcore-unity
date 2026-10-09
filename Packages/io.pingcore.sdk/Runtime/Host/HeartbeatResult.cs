using System;
using PingCore.Core.Discovery;
using PingCore.Discovery.Host.Wire;

namespace PingCore.Discovery.Host
{
    /// <summary>One heartbeat send, raised through <see cref="HeartbeatReporter.Beat"/> after every send, the first included.</summary>
    public sealed class HeartbeatResult
    {
        /// <summary>Creates a result.</summary>
        public HeartbeatResult(DiscoveryResult<HeartbeatResponse> call, int missedBeats, TimeSpan? nextBeatIn, bool stopped)
        {
            Call = call;
            MissedBeats = missedBeats;
            NextBeatIn = nextBeatIn;
            Stopped = stopped;
        }

        /// <summary>The call result; <c>Call.Value</c> is the answer when it was accepted.</summary>
        public DiscoveryResult<HeartbeatResponse> Call { get; }

        /// <summary>The answer when accepted; null otherwise.</summary>
        public HeartbeatResponse Response => Call == null ? null : Call.Value;

        /// <summary>True when Discovery accepted this heartbeat.</summary>
        public bool Accepted => Call != null && Call.IsOk;

        /// <summary>Consecutive failed sends after this one (0 when accepted).</summary>
        public int MissedBeats { get; }

        /// <summary>The wait before the next send, or null when the loop stops here.</summary>
        public TimeSpan? NextBeatIn { get; }

        /// <summary>True when the loop stops after this send (a 409 refusal: <c>Call.Reason</c> and <c>Call.Error.Limit</c> say why).</summary>
        public bool Stopped { get; }

        /// <inheritdoc />
        public override string ToString() => (Call == null ? "none" : Call.ToString()) + " missed=" + MissedBeats + (Stopped ? " stopped" : string.Empty);
    }
}
