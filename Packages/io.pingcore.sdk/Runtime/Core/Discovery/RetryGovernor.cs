using System;

namespace PingCore.Core.Discovery
{
    /// <summary>
    /// The one retry policy for Discovery calls, pure: given how attempt <c>n</c> ended, whether to
    /// try again and after how long. A 429 waits its <c>Retry-After</c> (the backoff when it sent
    /// none); a 503, an <see cref="DiscoveryOutcome.Unexpected"/> answer or a transport failure backs
    /// off 2, 4, 8 s, capped at 10 s. Everything else (success, refusals, cancellation) is final.
    /// The caller waits through <see cref="IScheduler"/> and resends the same
    /// <see cref="DiscoveryRequest"/>, so an idempotency anchor in its body is reused.
    /// </summary>
    public static class RetryGovernor
    {
        /// <summary>The longest backoff after a 503, an unexpected answer or a transport failure.</summary>
        public static readonly TimeSpan BackoffCap = TimeSpan.FromSeconds(10);

        /// <summary>The longest <c>Retry-After</c> the SDK honours; anything longer is treated as this.</summary>
        public static readonly TimeSpan RetryAfterCeiling = TimeSpan.FromHours(1);

        /// <summary>True for the outcomes a retry can fix: 429, 503, an unexpected answer, no answer.</summary>
        public static bool IsRetryable(DiscoveryOutcome outcome)
        {
            return outcome == DiscoveryOutcome.RateLimited
                || outcome == DiscoveryOutcome.Degraded
                || outcome == DiscoveryOutcome.Unexpected
                || outcome == DiscoveryOutcome.Unreachable;
        }

        /// <summary>The backoff after the <paramref name="failures"/>-th consecutive failure (1-based): 2, 4, 8, then 10 s.</summary>
        public static TimeSpan Backoff(int failures)
        {
            if (failures <= 1)
            {
                return TimeSpan.FromSeconds(2);
            }

            if (failures >= 4)
            {
                return BackoffCap;
            }

            double seconds = 2 * Math.Pow(2, failures - 1);
            return seconds >= BackoffCap.TotalSeconds ? BackoffCap : TimeSpan.FromSeconds(seconds);
        }

        /// <summary>
        /// Decides whether attempt <paramref name="attempt"/> (1-based) is followed by another.
        /// </summary>
        /// <param name="outcome">How the attempt ended.</param>
        /// <param name="attempt">The attempt that just ended, 1-based.</param>
        /// <param name="retryAfter">The answer's <c>Retry-After</c>, or null.</param>
        /// <param name="maxAttempts">The total attempts allowed, at least 1.</param>
        /// <param name="maxDelay">The longest wait the caller accepts; a longer one gives up so the caller sees the 429.</param>
        /// <param name="delay">The wait before the next attempt, when this returns true.</param>
        public static bool TryGetDelay(DiscoveryOutcome outcome, int attempt, TimeSpan? retryAfter, int maxAttempts, TimeSpan maxDelay, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;
            if (!IsRetryable(outcome) || attempt >= Math.Max(1, maxAttempts))
            {
                return false;
            }

            TimeSpan wait = outcome == DiscoveryOutcome.RateLimited && retryAfter.HasValue
                ? Clamp(retryAfter.Value)
                : Backoff(attempt);
            if (wait > maxDelay)
            {
                return false;
            }

            delay = wait;
            return true;
        }

        /// <summary>A received <c>Retry-After</c>, bounded to zero and <see cref="RetryAfterCeiling"/>.</summary>
        public static TimeSpan Clamp(TimeSpan retryAfter)
        {
            if (retryAfter < TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            return retryAfter > RetryAfterCeiling ? RetryAfterCeiling : retryAfter;
        }
    }
}
