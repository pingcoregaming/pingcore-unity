using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core.Discovery;
using PingCore.Discovery.Client.Wire;

namespace PingCore.Discovery.Client
{
    /// <summary>The poll loop: the 2 s cadence, the 429 wait, the failure backoff and the expiry stop.</summary>
    public sealed partial class TicketHandle
    {
        private async Task PollLoopAsync(CancellationToken callerToken)
        {
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, stop.Token))
            {
                CancellationToken token = linked.Token;
                int failures = 0;
                bool atOnce;
                lock (sync)
                {
                    atOnce = pollAtOnce;
                    pollAtOnce = false;
                }

                TimeSpan delay = PollInterval;
                while (State == TicketState.Queued)
                {
                    if (!atOnce)
                    {
                        try
                        {
                            await scheduler.DelayAsync(delay, token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }

                    atOnce = false;
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (scheduler.UtcNow >= ExpiresAt + ExpiryGrace)
                    {
                        Finish(TicketState.Expired, null, DiscoveryCallResult.Local(DiscoveryOutcome.NotFound, "the ticket passed its expiry without a match"));
                        return;
                    }

                    DiscoveryResult<TicketResponse> poll = await client.PollTicketAsync(ticketId, TicketRef, token);
                    switch (poll.Outcome)
                    {
                        case DiscoveryOutcome.Ok:
                            failures = 0;
                            delay = PollInterval;
                            if (poll.Value.ExpiresAt > 0)
                            {
                                ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(poll.Value.ExpiresAt);
                            }

                            if (TryMatch(poll.Value, out MatchAssignment placed))
                            {
                                Finish(TicketState.Matched, placed, null);
                                return;
                            }

                            break;
                        case DiscoveryOutcome.NotFound:
                            Finish(TicketState.Expired, null, poll);
                            return;
                        case DiscoveryOutcome.RateLimited when poll.RetryAfter.HasValue:
                            Record(poll);
                            TimeSpan wait = RetryGovernor.Clamp(poll.RetryAfter.Value);
                            delay = wait > PollInterval ? wait : PollInterval;
                            break;
                        case DiscoveryOutcome.RateLimited:
                            // A 429 with no readable Retry-After (WebGL hides the header unless Discovery
                            // exposes it to the page): back off like any failure, never every 2 s for ever.
                        case DiscoveryOutcome.Degraded:
                        case DiscoveryOutcome.Unexpected:
                        case DiscoveryOutcome.Unreachable:
                            Record(poll);
                            failures++;
                            if (failures >= MaxConsecutiveFailures)
                            {
                                Finish(TicketState.Failed, null, poll);
                                return;
                            }

                            delay = RetryGovernor.Backoff(failures);
                            break;
                        case DiscoveryOutcome.Cancelled:
                            return;
                        default:
                            Finish(TicketState.Failed, null, poll);
                            return;
                    }
                }
            }
        }

        private void Record(DiscoveryCallResult failure)
        {
            lock (sync)
            {
                lastError = failure;
            }
        }
    }
}
