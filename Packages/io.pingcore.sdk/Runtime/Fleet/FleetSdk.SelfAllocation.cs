using System;
using System.Threading;
using System.Threading.Tasks;

namespace PingCore.Fleet
{
    /// <summary>
    /// The self-allocation: <c>POST /allocate</c>, single-flight, confirmed on the watch stream. After a request whose answer
    /// was lost or whose frame did not come, no second request is sent until a frame carries an allocation or
    /// <see cref="FleetSdkOptions.SelfAllocationGrace"/> passes, because the supervisor may have made the first one and a second
    /// would replace it.
    /// </summary>
    public sealed partial class FleetSdk
    {
        /// <summary>How often the self-allocation looks at the view for its frame after a 2xx.</summary>
        internal static readonly TimeSpan SelfAllocationPoll = TimeSpan.FromMilliseconds(50);

        private SingleFlight<SelfAllocationResult> selfAllocation;
        private long allocationsReceived;

        /// <summary>Until when an unconfirmed self-allocation holds back the next <c>POST /allocate</c>; null when none is pending. Under <c>gate</c>.</summary>
        private DateTimeOffset? selfAllocationUnconfirmedUntil;

        /// <inheritdoc />
        public async Task<SelfAllocationResult> AllocateSelfAsync(CancellationToken cancellationToken)
        {
            if (!IsHosted)
            {
                return new SelfAllocationResult(FleetCallOutcome.Inert, 0, "not a hosted game server", null, false);
            }

            SingleFlight<SelfAllocationResult> flight;
            lock (gate)
            {
                if (selfAllocation == null)
                {
                    selfAllocation = new SingleFlight<SelfAllocationResult>(SelfAllocateOnceAsync,
                        e => new SelfAllocationResult(FleetCallOutcome.Unreachable, 0, "the self-allocation failed (" + e.GetType().Name + ")", null, false));
                }

                flight = selfAllocation;
            }

            try
            {
                return await flight.RunAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Only this caller's wait ended; the shared call goes on and its frame still opens the session.
                return new SelfAllocationResult(FleetCallOutcome.Cancelled, 0, "cancelled", null, false);
            }
        }

        /// <summary>The one shared self-allocation: refuse locally when allocated or when an earlier one is unconfirmed, send, then wait for the frame.</summary>
        private async Task<SelfAllocationResult> SelfAllocateOnceAsync()
        {
            AllocationInfo before;
            long receivedBefore;
            DateTimeOffset? unconfirmedUntil;
            lock (gate)
            {
                before = allocations.Current;
                receivedBefore = allocationsReceived;
                unconfirmedUntil = selfAllocationUnconfirmedUntil;
            }

            if (before != null)
            {
                // The supervisor's /allocate would replace this allocation, so the shim never sends it.
                Log(FleetLogLevel.Info, "allocate", "not sent: an allocation is already current", 0, FleetCallOutcome.Rejected);
                return new SelfAllocationResult(FleetCallOutcome.Rejected, 0, "an allocation is already current; a self-allocation would replace it", before, true);
            }

            if (unconfirmedUntil != null && scheduler.UtcNow < unconfirmedUntil.Value)
            {
                return await AwaitEarlierAsync();
            }

            LocalSdkAnswer answer = await caller.SendAsync("POST", "/allocate", "{}", CancellationToken.None);
            LogAnswer("allocate", answer, true);
            if (answer.Outcome != FleetCallOutcome.Ok)
            {
                if (answer.Outcome == FleetCallOutcome.Unreachable)
                {
                    // The answer was lost, not necessarily the request: the supervisor may have self-allocated.
                    HoldBackSelfAllocation();
                }

                return new SelfAllocationResult(answer.Outcome, answer.Status, answer.Message, null, false);
            }

            // The supervisor writes the frame before it answers, so it is usually here already.
            (AllocationInfo seen, bool disposedWhileWaiting) = await WaitForAllocationAsync(true);
            if (disposedWhileWaiting)
            {
                return new SelfAllocationResult(FleetCallOutcome.Cancelled, answer.Status, "the shim was disposed", null, false);
            }

            if (seen == null)
            {
                HoldBackSelfAllocation();
                Log(FleetLogLevel.Warning, "allocate", "accepted, but no watch frame carried the self-allocation in time", answer.Status, answer.Outcome);
                return new SelfAllocationResult(FleetCallOutcome.Ok, answer.Status, "accepted, but no watch frame carried the self-allocation within "
                    + (long)options.SelfAllocationWait.TotalMilliseconds + " ms", null, false);
            }

            long received;
            lock (gate)
            {
                received = allocationsReceived;
            }

            if (received - receivedBefore > 1)
            {
                Log(FleetLogLevel.Warning, "allocate", "another allocation arrived while the game self-allocated; the supervisor replaced it with the self-allocation",
                    answer.Status, answer.Outcome);
            }

            return new SelfAllocationResult(FleetCallOutcome.Ok, answer.Status, null, seen, false);
        }

        /// <summary>An earlier self-allocation is unconfirmed: send nothing, and wait one <see cref="FleetSdkOptions.SelfAllocationWait"/> for any allocation's frame.</summary>
        private async Task<SelfAllocationResult> AwaitEarlierAsync()
        {
            Log(FleetLogLevel.Info, "allocate", "not sent: an earlier self-allocation is not confirmed yet; waiting for its frame", 0, FleetCallOutcome.Rejected);
            (AllocationInfo seen, bool disposedWhileWaiting) = await WaitForAllocationAsync(false);
            if (disposedWhileWaiting)
            {
                return new SelfAllocationResult(FleetCallOutcome.Cancelled, 0, "the shim was disposed", null, false);
            }

            if (seen == null)
            {
                return new SelfAllocationResult(FleetCallOutcome.Rejected, 0, "an earlier self-allocation is not confirmed yet; nothing is sent until its frame arrives or "
                    + (long)options.SelfAllocationGrace.TotalMilliseconds + " ms pass", null, false, true);
            }

            return seen.IsSelfAllocated
                ? new SelfAllocationResult(FleetCallOutcome.Ok, 0, null, seen, false)
                : new SelfAllocationResult(FleetCallOutcome.Rejected, 0, "an allocation is already current; a self-allocation would replace it", seen, true);
        }

        /// <summary>
        /// Polls the view for up to <see cref="FleetSdkOptions.SelfAllocationWait"/>: for a self-allocation
        /// (<paramref name="selfOnly"/>) or for any allocation. Null when none came; <c>Disposed</c> when the shim was disposed.
        /// </summary>
        private async Task<(AllocationInfo Seen, bool Disposed)> WaitForAllocationAsync(bool selfOnly)
        {
            DateTimeOffset deadline = scheduler.UtcNow + options.SelfAllocationWait;
            while (true)
            {
                AllocationInfo now;
                lock (gate)
                {
                    now = allocations.Current;
                }

                if (now != null && (!selfOnly || now.IsSelfAllocated))
                {
                    return (now, false);
                }

                DateTimeOffset at = scheduler.UtcNow;
                if (at >= deadline)
                {
                    return (null, false);
                }

                TimeSpan wait = deadline - at < SelfAllocationPoll ? deadline - at : SelfAllocationPoll;
                try
                {
                    await scheduler.DelayAsync(wait, lifetime.Token);
                }
                catch (OperationCanceledException)
                {
                    return (null, true);
                }
            }
        }

        /// <summary>Holds back the next <c>POST /allocate</c> for <see cref="FleetSdkOptions.SelfAllocationGrace"/>, or until a frame carries an allocation.</summary>
        private void HoldBackSelfAllocation()
        {
            lock (gate)
            {
                selfAllocationUnconfirmedUntil = scheduler.UtcNow + options.SelfAllocationGrace;
            }
        }

        /// <summary>
        /// Under <c>gate</c>, from <see cref="ApplyView"/>: counts a received allocation, and a frame carrying one ends the
        /// hold-back. True when the platform's allocation replaced this game server's own self-allocation (the supervisor
        /// delivers an allocation without looking at what is current), which the caller logs as a warning.
        /// </summary>
        private bool ObserveSelfAllocationLocked(AllocationChange change)
        {
            if (change.Received != null)
            {
                allocationsReceived++;
                selfAllocationUnconfirmedUntil = null;
            }

            return change.Cleared != null && change.Cleared.Reason == AllocationClearedReason.ClearedByPlatform
                && AllocationInfo.IsSelfAllocationId(change.Cleared.AllocationId)
                && allocations.Current != null && !allocations.Current.IsSelfAllocated;
        }
    }
}
