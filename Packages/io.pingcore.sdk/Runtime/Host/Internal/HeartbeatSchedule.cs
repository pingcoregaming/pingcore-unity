using System;
using PingCore.Core.Discovery;

namespace PingCore.Discovery.Host
{
    /// <summary>What the heartbeat loop does after one send.</summary>
    internal enum HeartbeatStepKind
    {
        /// <summary>Wait <see cref="HeartbeatStep.Delay"/>, then send again.</summary>
        Continue = 0,

        /// <summary>Discovery refused this game server (409): send nothing more.</summary>
        Stop = 1,

        /// <summary>The reporter is stopping: send nothing more.</summary>
        Cancelled = 2,
    }

    /// <summary>The loop's decision after one send.</summary>
    internal readonly struct HeartbeatStep
    {
        public HeartbeatStep(HeartbeatStepKind kind, TimeSpan delay, int failures)
        {
            Kind = kind;
            Delay = delay;
            Failures = failures;
        }

        public HeartbeatStepKind Kind { get; }

        /// <summary>The wait before the next send (Continue only).</summary>
        public TimeSpan Delay { get; }

        /// <summary>Consecutive failed sends after this one (0 after a success).</summary>
        public int Failures { get; }
    }

    /// <summary>
    /// The heartbeat tier's timing, pure. Discovery keeps an entry for <see cref="Ttl"/> after its
    /// last accepted heartbeat (<c>HEARTBEAT_TTL_SECONDS</c>, 90 by default), so the reporter beats
    /// every <see cref="Interval"/> with up to <see cref="Jitter"/> either way, three beats per TTL.
    /// After a 429 it waits the answer's <c>Retry-After</c>. After a 503, another 5xx or no answer it
    /// retries at 5, 10, then every 20 s: the cap stays well below the TTL, so a recovered Discovery
    /// gets a beat before an entry that was fresh at the start of the outage lapses. Any other failed
    /// answer (400, 401, 403) keeps the regular cadence, because a later <c>SetMeta</c> or a restored
    /// token can fix it; only a 409 stops the loop. A change from <c>SetPlayers</c> or <c>SetMeta</c>
    /// is sent early, at most once per <see cref="EarlySendSpacing"/>, and only while the last send
    /// succeeded, so an early send never cuts a backoff or a <c>Retry-After</c> short.
    /// </summary>
    internal static class HeartbeatSchedule
    {
        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan Jitter = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(90);
        public static readonly TimeSpan EarlySendSpacing = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan RetryCap = TimeSpan.FromSeconds(20);

        /// <summary>The shortest wait after a 429, so a <c>Retry-After: 0</c> never spins.</summary>
        public static readonly TimeSpan RateLimitFloor = TimeSpan.FromSeconds(1);

        /// <summary>Consecutive failed sends at which the reporter warns that beats are being missed.</summary>
        public const int MissedBeatsWarning = 2;

        /// <summary>The regular wait: <see cref="Interval"/> plus a jitter in [-3 s, +3 s) for <paramref name="unit"/> in [0, 1).</summary>
        public static TimeSpan Regular(double unit)
        {
            if (double.IsNaN(unit) || unit < 0)
            {
                unit = 0;
            }

            if (unit >= 1)
            {
                unit = 0.999999;
            }

            // Ticks, not TimeSpan.FromSeconds: that rounds to whole milliseconds on Mono and .NET
            // Framework, which would turn the top of the range into exactly 33 s.
            double offset = ((unit * 2) - 1) * Jitter.TotalSeconds;
            return TimeSpan.FromTicks((long)((Interval.TotalSeconds + offset) * TimeSpan.TicksPerSecond));
        }

        /// <summary>The retry wait after the <paramref name="failures"/>-th consecutive transient failure (1-based): 5, 10, then 20 s.</summary>
        public static TimeSpan Retry(int failures)
        {
            if (failures <= 1)
            {
                return TimeSpan.FromSeconds(5);
            }

            return failures == 2 ? TimeSpan.FromSeconds(10) : RetryCap;
        }

        /// <summary>True for the failures the retry schedule covers: 503, any other 5xx or unparseable answer, no answer.</summary>
        public static bool IsTransient(DiscoveryCallResult result)
        {
            return result.Outcome == DiscoveryOutcome.Degraded
                || result.Outcome == DiscoveryOutcome.Unreachable
                || (result.Outcome == DiscoveryOutcome.Unexpected && (result.Status == 0 || result.Status >= 500));
        }

        /// <summary>The decision after one send that ended with <paramref name="result"/>.</summary>
        /// <param name="result">How the send ended.</param>
        /// <param name="failuresBefore">Consecutive failed sends before this one.</param>
        /// <param name="unit">A jitter source value in [0, 1).</param>
        public static HeartbeatStep Next(DiscoveryCallResult result, int failuresBefore, double unit)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            switch (result.Outcome)
            {
                case DiscoveryOutcome.Ok:
                    return new HeartbeatStep(HeartbeatStepKind.Continue, Regular(unit), 0);
                case DiscoveryOutcome.Conflict:
                    return new HeartbeatStep(HeartbeatStepKind.Stop, TimeSpan.Zero, failuresBefore + 1);
                case DiscoveryOutcome.Cancelled:
                    return new HeartbeatStep(HeartbeatStepKind.Cancelled, TimeSpan.Zero, failuresBefore);
            }

            int failures = failuresBefore + 1;
            if (result.Outcome == DiscoveryOutcome.RateLimited)
            {
                TimeSpan wait = result.RetryAfter.HasValue ? RetryGovernor.Clamp(result.RetryAfter.Value) : Regular(unit);
                return new HeartbeatStep(HeartbeatStepKind.Continue, wait < RateLimitFloor ? RateLimitFloor : wait, failures);
            }

            if (IsTransient(result))
            {
                return new HeartbeatStep(HeartbeatStepKind.Continue, Retry(failures), failures);
            }

            return new HeartbeatStep(HeartbeatStepKind.Continue, Regular(unit), failures);
        }

        /// <summary>
        /// When the next send is due: <paramref name="regularDue"/>, or earlier for a pending change
        /// (<paramref name="changePending"/>) while the last send succeeded (<paramref name="failures"/> 0),
        /// but never sooner than <see cref="EarlySendSpacing"/> after <paramref name="lastSendAt"/>.
        /// </summary>
        public static DateTimeOffset DueAt(DateTimeOffset regularDue, bool changePending, int failures, DateTimeOffset lastSendAt)
        {
            if (!changePending || failures > 0)
            {
                return regularDue;
            }

            DateTimeOffset early = lastSendAt + EarlySendSpacing;
            return early < regularDue ? early : regularDue;
        }
    }
}
