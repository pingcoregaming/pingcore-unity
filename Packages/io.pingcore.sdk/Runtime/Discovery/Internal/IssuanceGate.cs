using System;

namespace PingCore.Discovery.Client
{
    /// <summary>
    /// The process-wide pacing of anonymous token issuance. Discovery allows 10 issues per minute
    /// per source IP, shared by every app (<c>PLAYER_TOKEN_ISSUE_RATE_LIMIT_MAX</c>), so every
    /// <see cref="PlayerTokenCache"/> in the process shares one gate: issue attempts are at least
    /// <see cref="Floor"/> apart, and none is sent before a received <c>Retry-After</c> has passed.
    /// Times are the caller's scheduler clock. Pure apart from its own lock.
    /// </summary>
    internal sealed class IssuanceGate
    {
        /// <summary>The smallest gap between two issue attempts in one process.</summary>
        public static readonly TimeSpan Floor = TimeSpan.FromSeconds(6);

        /// <summary>The wait assumed when a 429 carries no <c>Retry-After</c>.</summary>
        public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

        private readonly object sync = new object();
        private DateTimeOffset? lastSlot;
        private DateTimeOffset? blockedUntil;

        /// <summary>The gate every client in the process shares by default.</summary>
        public static IssuanceGate Shared { get; } = new IssuanceGate();

        /// <summary>
        /// How long the caller must still honour a received <c>Retry-After</c>, or null when it may
        /// issue. A caller told to wait must not send.
        /// </summary>
        public TimeSpan? BlockedFor(DateTimeOffset now)
        {
            lock (sync)
            {
                if (blockedUntil.HasValue && blockedUntil.Value > now)
                {
                    return blockedUntil.Value - now;
                }

                return null;
            }
        }

        /// <summary>
        /// Reserves the next issue slot: at <paramref name="now"/>, or <see cref="Floor"/> after the
        /// previous slot, whichever is later. Returns how long to wait before sending (zero for now).
        /// </summary>
        public TimeSpan Reserve(DateTimeOffset now)
        {
            lock (sync)
            {
                DateTimeOffset slot = now;
                if (lastSlot.HasValue && lastSlot.Value + Floor > slot)
                {
                    slot = lastSlot.Value + Floor;
                }

                lastSlot = slot;
                return slot - now;
            }
        }

        /// <summary>Records a 429: nothing is sent before <paramref name="now"/> plus the delay.</summary>
        public void RecordRateLimited(DateTimeOffset now, TimeSpan? retryAfter)
        {
            TimeSpan wait = retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero ? retryAfter.Value : DefaultRetryAfter;
            lock (sync)
            {
                DateTimeOffset until = now + wait;
                if (!blockedUntil.HasValue || until > blockedUntil.Value)
                {
                    blockedUntil = until;
                }
            }
        }
    }
}
