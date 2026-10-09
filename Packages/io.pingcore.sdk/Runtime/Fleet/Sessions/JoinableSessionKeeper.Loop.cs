using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Fleet.Wire;

namespace PingCore.Fleet.Sessions
{
    /// <summary>The keeper's loop: decide (<see cref="JoinablePlan"/>), publish or withdraw, wait for a change or the next due time.</summary>
    public sealed partial class JoinableSessionKeeper
    {
        private async Task RunAsync()
        {
            CancellationToken token = lifetime.Token;
            try
            {
                while (true)
                {
                    TaskCompletionSource<bool> waitFor;
                    JoinableStep step;
                    int seats;
                    TimeSpan? due;
                    lock (gate)
                    {
                        if (stopped)
                        {
                            return;
                        }

                        waitFor = wake;
                        seats = ComputeSeatsLocked();
                        step = updated ? JoinablePlan.Next(seats, state, TtlSeconds, scheduler.UtcNow) : JoinableStep.None;
                    }

                    if (step == JoinableStep.Publish)
                    {
                        await PublishAsync(seats, token);
                        continue;
                    }

                    if (step == JoinableStep.Withdraw)
                    {
                        await WithdrawAsync(token);
                        continue;
                    }

                    lock (gate)
                    {
                        if (stopped)
                        {
                            return;
                        }

                        due = updated ? JoinablePlan.NextDue(state, TtlSeconds, scheduler.UtcNow) : null;
                        if (waitFor.Task.IsCompleted)
                        {
                            wake = NewWake();
                            continue;
                        }
                    }

                    await WaitAsync(waitFor.Task, due, token);
                    lock (gate)
                    {
                        if (wake == waitFor && waitFor.Task.IsCompleted)
                        {
                            wake = NewWake();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
            catch (ObjectDisposedException)
            {
                // Stopped while a delay was being set up.
            }
        }

        private async Task WaitAsync(Task signal, TimeSpan? due, CancellationToken token)
        {
            if (!due.HasValue)
            {
                // Stop and Dispose complete the signal too, so this wait always ends.
                await signal;
                token.ThrowIfCancellationRequested();
                return;
            }

            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task delay = scheduler.DelayAsync(due.Value, timer.Token);
                await Task.WhenAny(signal, delay);
                timer.Cancel();
            }

            token.ThrowIfCancellationRequested();
        }

        private async Task PublishAsync(int seats, CancellationToken token)
        {
            var request = new JoinableSessionRequest
            {
                Queue = Queue,
                OpenSeats = seats,
                SessionSize = SessionSize > 0 ? SessionSize : (int?)null,
                TtlSeconds = TtlSeconds,
            };
            JoinablePublishResult result = await fleet.PublishJoinableAsync(AllocationId, request, token);
            DateTimeOffset now = scheduler.UtcNow;
            lock (gate)
            {
                LastPublish = result;
                if (result.IsOk)
                {
                    state.LiveSeats = seats;
                    state.LastPublishAt = now;
                    state.MaybeLive = true;
                    state.LastFailureAt = null;
                }
                else
                {
                    // A lost answer may still have stored the record; a refusal (4xx) stored nothing new.
                    state.LiveSeats = null;
                    state.MaybeLive |= result.Outcome != FleetCallOutcome.Rejected && result.Outcome != FleetCallOutcome.Inert;
                    state.LastFailureAt = now;
                    if (result.Outcome == FleetCallOutcome.Inert || result.Outcome == FleetCallOutcome.Unsupported)
                    {
                        stopped = true;
                    }
                }
            }

            RaisePublished(result);
            token.ThrowIfCancellationRequested();
        }

        private async Task WithdrawAsync(CancellationToken token)
        {
            FleetCallResult result = await fleet.WithdrawJoinableAsync(AllocationId, token);
            DateTimeOffset now = scheduler.UtcNow;
            lock (gate)
            {
                if (result.IsOk)
                {
                    state.LiveSeats = null;
                    state.MaybeLive = false;
                    state.LastFailureAt = null;
                }
                else
                {
                    state.LastFailureAt = now;
                    if (result.Outcome == FleetCallOutcome.Inert || result.Outcome == FleetCallOutcome.Unsupported)
                    {
                        stopped = true;
                    }
                }
            }

            RaiseWithdrawn(result);
            token.ThrowIfCancellationRequested();
        }

        private void RaisePublished(JoinablePublishResult result)
        {
            try
            {
                Published?.Invoke(result);
            }
            catch (Exception)
            {
                // A subscriber's failure must not stop the keeper.
            }
        }

        private void RaiseWithdrawn(FleetCallResult result)
        {
            try
            {
                Withdrawn?.Invoke(result);
            }
            catch (Exception)
            {
                // A subscriber's failure must not stop the keeper.
            }
        }
    }
}
