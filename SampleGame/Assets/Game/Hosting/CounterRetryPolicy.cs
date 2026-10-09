using System;
using PingCore.Fleet;

namespace BeaconRush.Hosting
{
    /// <summary>What the <c>players</c> writer does after one PATCH.</summary>
    public enum CounterWriteStep
    {
        /// <summary>2xx: the count is written.</summary>
        Written,

        /// <summary>A failure worth another try after <see cref="CounterRetryPolicy.RetryDelay"/>.</summary>
        Retry,

        /// <summary>The last retry failed too: report it once and stop until the next join or leave.</summary>
        GiveUp,

        /// <summary>Inert, cancelled or the endpoint closed (the process is stopping): report it and stop, no retry.</summary>
        Stop,
    }

    /// <summary>
    /// The <c>players</c> write retry policy, pure. A failed PATCH is retried <see cref="MaxRetries"/> times,
    /// <see cref="RetryDelay"/> apart; then the writer gives up and reports once. A count is recorded as
    /// written only on a 2xx, so a later join or leave tries again.
    /// </summary>
    public static class CounterRetryPolicy
    {
        public const int MaxRetries = 3;

        public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

        /// <param name="outcome">The outcome of the PATCH just made.</param>
        /// <param name="retriesSoFar">How many retries of this count were already made.</param>
        public static CounterWriteStep Next(FleetCallOutcome outcome, int retriesSoFar)
        {
            switch (outcome)
            {
                case FleetCallOutcome.Ok:
                    return CounterWriteStep.Written;
                case FleetCallOutcome.Inert:
                case FleetCallOutcome.Cancelled:
                case FleetCallOutcome.EndpointClosed:
                    return CounterWriteStep.Stop;
                default:
                    return retriesSoFar < MaxRetries ? CounterWriteStep.Retry : CounterWriteStep.GiveUp;
            }
        }
    }

    /// <summary>
    /// The writer's retry count across attempts, pure. The budget of <see cref="CounterRetryPolicy.MaxRetries"/>
    /// belongs to one count: when the count to write changes between attempts (a join or leave during the
    /// retries), the new count starts again from zero, and a write reset it too.
    /// </summary>
    public sealed class CounterRetryState
    {
        private long? lastTarget;

        /// <summary>Retries already made for the current count.</summary>
        public int Retries { get; private set; }

        /// <summary>The step after a PATCH of <paramref name="target"/> that ended with <paramref name="outcome"/>.</summary>
        public CounterWriteStep After(long target, FleetCallOutcome outcome)
        {
            if (lastTarget != target)
            {
                Retries = 0;
                lastTarget = target;
            }

            CounterWriteStep step = CounterRetryPolicy.Next(outcome, Retries);
            switch (step)
            {
                case CounterWriteStep.Retry:
                    Retries++;
                    break;
                case CounterWriteStep.Written:
                    Retries = 0;
                    break;
            }

            return step;
        }
    }
}
