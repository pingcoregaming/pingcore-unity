using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Fleet;
using UnityEngine;

namespace BeaconRush.Hosting
{
    /// <summary>
    /// Writes the <c>players</c> counter (never <c>sessions</c>), one PATCH at a time, always ending on the
    /// latest count, with <see cref="CounterRetryPolicy"/> (the retry budget restarts when the count changes,
    /// <see cref="CounterRetryState"/>). Delays go through an <see cref="IScheduler"/>, so they never block a
    /// thread. Raises a <c>counter</c> event per written count and one for a final failure; its <c>phase</c> is
    /// the caller's (<c>integrate</c>, <c>afterReady</c>, <c>join</c>, <c>leave</c>) for the first count, then <c>update</c>.
    /// </summary>
    internal sealed class PlayersCounterWriter
    {
        public const string Counter = "players";

        private readonly IFleetSdk fleet;
        private readonly Func<long> players;
        private readonly Func<bool> stopping;
        private readonly IScheduler scheduler;
        private readonly CancellationToken token;
        private readonly Action<FleetCallOutcome> writeReturned;
        private long written = -1;
        private bool inFlight;

        /// <param name="writeReturned">Called with the outcome as each PATCH returns, before anything else (<see cref="ServerInstrumentation.PlayersWriteReturned"/>).</param>
        public PlayersCounterWriter(IFleetSdk fleet, Func<long> players, Func<bool> stopping, IScheduler scheduler, CancellationToken token,
            Action<FleetCallOutcome> writeReturned)
        {
            this.fleet = fleet;
            this.players = players;
            this.stopping = stopping;
            this.scheduler = scheduler;
            this.token = token;
            this.writeReturned = writeReturned;
        }

        /// <summary>Starts a write from an event handler; a write already running picks the new count up.</summary>
        public async void Request(string phase)
        {
            try
            {
                await WriteAsync(phase);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>Writes until the endpoint holds the current count, gives up, or stops.</summary>
        public async Task WriteAsync(string phase)
        {
            if (fleet == null || !fleet.IsHosted || inFlight)
            {
                return;
            }

            inFlight = true;
            try
            {
                var retry = new CounterRetryState();
                while (!stopping() && written != players())
                {
                    long target = players();
                    CounterResult result = await fleet.SetCounterAsync(Counter, target, token);
                    writeReturned?.Invoke(result.Outcome);
                    switch (retry.After(target, result.Outcome))
                    {
                        case CounterWriteStep.Written:
                            written = target;
                            FleetEventBridge.RaiseCounter(Counter, result, target, phase);
                            phase = "update";
                            break;
                        case CounterWriteStep.Retry:
                            try
                            {
                                await scheduler.DelayAsync(CounterRetryPolicy.RetryDelay, token);
                            }
                            catch (OperationCanceledException)
                            {
                                return;
                            }

                            break;
                        case CounterWriteStep.GiveUp:
                            Debug.LogWarning("[BeaconRush] players = " + target + " was not written after " + (CounterRetryPolicy.MaxRetries + 1) + " tries (" + result + "); the next join or leave tries again");
                            FleetEventBridge.RaiseCounter(Counter, result, target, phase);
                            return;
                        default:
                            FleetEventBridge.RaiseCounter(Counter, result, target, phase);
                            return;
                    }
                }
            }
            finally
            {
                inFlight = false;
            }
        }
    }
}
