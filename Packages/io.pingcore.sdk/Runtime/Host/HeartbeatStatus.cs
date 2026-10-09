using System;

namespace PingCore.Discovery.Host
{
    /// <summary>
    /// A snapshot of what Discovery last said about this game server, and of the loop. The
    /// verification fields come from the last accepted heartbeat (<c>verificationMode</c>, <c>verified</c>,
    /// <c>lastProbeError</c>, <c>expiresIn</c>); they keep their values through failed sends.
    /// </summary>
    public sealed class HeartbeatStatus
    {
        /// <summary>The status before any heartbeat was accepted.</summary>
        public static readonly HeartbeatStatus NotStarted = new HeartbeatStatus(false, false, null, null, null, null, null, 0);

        /// <summary>Creates a snapshot.</summary>
        public HeartbeatStatus(bool running, bool stopped, string verificationMode, string verified, string lastProbeError, int? expiresIn, DateTimeOffset? lastAcceptedAt, int missedBeats)
        {
            Running = running;
            Stopped = stopped;
            VerificationMode = verificationMode;
            Verified = verified;
            LastProbeError = lastProbeError;
            ExpiresIn = expiresIn;
            LastAcceptedAt = lastAcceptedAt;
            MissedBeats = missedBeats;
        }

        /// <summary>True while the heartbeat loop runs.</summary>
        public bool Running { get; }

        /// <summary>True once the loop ended for good: <see cref="HeartbeatReporter.StopAsync"/>, disposal, or a 409 refusal.</summary>
        public bool Stopped { get; }

        /// <summary><c>none</c>, <c>tcp</c> or <c>udp-echo</c> (an open-registration app is always <c>udp-echo</c>); null before the first accepted beat.</summary>
        public string VerificationMode { get; }

        /// <summary><c>pending</c>, <c>verified</c> or <c>unverified</c>; null when the mode is <c>none</c> or before the first accepted beat.</summary>
        public string Verified { get; }

        /// <summary>Why Discovery's last probe failed, or null.</summary>
        public string LastProbeError { get; }

        /// <summary>Seconds the entry lives without another heartbeat, from the last accepted one; null before it.</summary>
        public int? ExpiresIn { get; }

        /// <summary>When the last heartbeat was accepted (scheduler clock), or null.</summary>
        public DateTimeOffset? LastAcceptedAt { get; }

        /// <summary>Consecutive failed sends since the last accepted heartbeat.</summary>
        public int MissedBeats { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            return "running=" + Running + " stopped=" + Stopped + " mode=" + VerificationMode + " verified=" + Verified + " missed=" + MissedBeats;
        }
    }
}
