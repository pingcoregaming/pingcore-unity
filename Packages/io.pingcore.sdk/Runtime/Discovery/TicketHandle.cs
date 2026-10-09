using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Core.Handshake;
using PingCore.Discovery.Client.Wire;

namespace PingCore.Discovery.Client
{
    /// <summary>
    /// A submitted matchmaking ticket. <see cref="WaitAsync"/> polls it every 2 s through the
    /// client's scheduler until it is matched, expired, cancelled or failed: a 429 waits
    /// <c>max(Retry-After, 2 s)</c>; a 429 without a readable <c>Retry-After</c>, a 503, an unexpected
    /// answer or no answer backs off through <see cref="RetryGovernor"/>, and five in a row end
    /// <see cref="TicketState.Failed"/>; a 404 ends <see cref="TicketState.Expired"/>; a poll or cancel
    /// that the SDK could not resend because a 401 re-issue changed the player id
    /// (<see cref="DiscoveryCallResult.IdentityChanged"/>) ends <see cref="TicketState.Failed"/> with that
    /// result; polling stops at the ticket's <c>expiresAt</c> plus 10 s.
    /// A <c>matched</c> answer without allocation fields (a replayed submit) is polled once more at
    /// once. The ticket id is a bearer secret: logs and events carry <see cref="TicketRef"/> only.
    /// Disposing a queued handle cancels the ticket, best effort.
    /// </summary>
    public sealed partial class TicketHandle : IDisposable
    {
        /// <summary>The poll cadence.</summary>
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

        /// <summary>Consecutive failed polls that end the ticket <see cref="TicketState.Failed"/>.</summary>
        public const int MaxConsecutiveFailures = 5;

        /// <summary>How long after <c>expiresAt</c> polling carries on (clock skew).</summary>
        public static readonly TimeSpan ExpiryGrace = TimeSpan.FromSeconds(10);

        private readonly object sync = new object();
        private readonly DiscoveryClient client;
        private readonly IScheduler scheduler;
        private readonly string ticketId;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private TicketState state = TicketState.Queued;
        private MatchAssignment match;
        private DiscoveryCallResult lastError;
        private Task loop;
        private bool pollAtOnce;
        private bool disposed;

        internal TicketHandle(DiscoveryClient client, IScheduler scheduler, string ticketId, string playerId, TicketResponse submitted)
        {
            this.client = client;
            this.scheduler = scheduler;
            this.ticketId = ticketId;
            PlayerId = playerId;
            TicketRef = SecureIds.Ref(ticketId);
            Queue = submitted.Queue;
            SubmittedAt = scheduler.UtcNow;
            ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(submitted.ExpiresAt);
            if (string.Equals(submitted.Status, "matched", StringComparison.Ordinal))
            {
                if (TryMatch(submitted, out MatchAssignment placed))
                {
                    match = placed;
                    state = TicketState.Matched;
                }
                else
                {
                    pollAtOnce = true;
                }
            }
        }

        /// <summary>Raised on every state change and when the match arrives.</summary>
        public event Action<TicketHandle> Changed;

        /// <summary>Where the ticket stands.</summary>
        public TicketState State
        {
            get
            {
                lock (sync)
                {
                    return state;
                }
            }
        }

        /// <summary><c>SecureIds.Ref(ticketId)</c>: the only form of the ticket id that logs and events carry.</summary>
        public string TicketRef { get; }

        /// <summary>The queue the service placed the ticket in.</summary>
        public string Queue { get; }

        /// <summary>The player id of the token that submitted the ticket.</summary>
        public string PlayerId { get; }

        /// <summary>When the ticket was submitted (scheduler clock).</summary>
        public DateTimeOffset SubmittedAt { get; }

        /// <summary>When Discovery expires the ticket if it is not matched.</summary>
        public DateTimeOffset ExpiresAt { get; private set; }

        /// <summary>The placement, once <see cref="TicketState.Matched"/>; otherwise null.</summary>
        public MatchAssignment Match
        {
            get
            {
                lock (sync)
                {
                    return match;
                }
            }
        }

        /// <summary>The last failed poll or cancel, or null.</summary>
        public DiscoveryCallResult LastError
        {
            get
            {
                lock (sync)
                {
                    return lastError;
                }
            }
        }

        /// <summary>True once the ticket is matched, expired, cancelled or failed.</summary>
        public bool IsTerminal => State != TicketState.Queued;

        /// <summary>The ticket id, for the join ticket only.</summary>
        internal string TicketId => ticketId;

        /// <summary>
        /// Polls until the ticket is terminal and returns this handle. Cancelling
        /// <paramref name="cancellationToken"/> stops the wait (the ticket stays queued; call again to
        /// resume). Concurrent waits share one poll loop. Never throws for an HTTP or transport failure.
        /// </summary>
        public async Task<TicketHandle> WaitAsync(CancellationToken cancellationToken)
        {
            Task running;
            lock (sync)
            {
                if (state != TicketState.Queued || disposed)
                {
                    return this;
                }

                if (loop == null || loop.IsCompleted)
                {
                    loop = PollLoopAsync(cancellationToken);
                }

                running = loop;
            }

            await running;
            return this;
        }

        /// <summary>
        /// Cancels the ticket. 200: <see cref="TicketState.Cancelled"/>. 409: it already matched, so one
        /// poll reads the match. 404: <see cref="TicketState.Expired"/>. A 401 whose re-issue changed the
        /// player id (<see cref="DiscoveryCallResult.IdentityChanged"/>): <see cref="TicketState.Failed"/>,
        /// since the new player id cannot reach the ticket. A terminal ticket sends nothing.
        /// </summary>
        public async Task<DiscoveryResult<CancelTicketResponse>> CancelAsync(CancellationToken cancellationToken)
        {
            if (IsTerminal)
            {
                return DiscoveryResult<CancelTicketResponse>.Refused(DiscoveryReason.Unknown, "the ticket is already " + State);
            }

            DiscoveryResult<CancelTicketResponse> result = await client.CancelTicketCallAsync(ticketId, TicketRef, cancellationToken);
            switch (result.Outcome)
            {
                case DiscoveryOutcome.Ok:
                    Finish(TicketState.Cancelled, null, null);
                    break;
                case DiscoveryOutcome.Conflict:
                    DiscoveryResult<TicketResponse> poll = await client.PollTicketAsync(ticketId, TicketRef, cancellationToken);
                    if (poll.IsOk && TryMatch(poll.Value, out MatchAssignment placed))
                    {
                        Finish(TicketState.Matched, placed, null);
                    }

                    break;
                case DiscoveryOutcome.NotFound:
                    Finish(TicketState.Expired, null, result);
                    break;
                case DiscoveryOutcome.Unauthorized when result.IdentityChanged:
                    Finish(TicketState.Failed, null, result);
                    break;
                default:
                    lock (sync)
                    {
                        lastError = result;
                    }

                    break;
            }

            return result;
        }

        /// <summary>
        /// The join ticket for the matched game server: kind <c>backfill</c> when <see cref="MatchAssignment.Backfill"/>,
        /// else <c>match</c>. Throws <see cref="InvalidOperationException"/> before the match.
        /// </summary>
        public JoinTicket CreateJoinTicket(int protocolVersion, string displayName)
        {
            MatchAssignment placed = Match;
            if (placed == null)
            {
                throw new InvalidOperationException("the ticket has no match yet");
            }

            return placed.Backfill
                ? JoinTicket.ForBackfill(ticketId, placed.AllocationId, PlayerId, protocolVersion, displayName)
                : JoinTicket.ForMatch(ticketId, placed.AllocationId, PlayerId, protocolVersion, displayName);
        }

        /// <summary>Stops polling; a still-queued ticket is cancelled best effort (fire and forget).</summary>
        public void Dispose()
        {
            bool cancel;
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                cancel = state == TicketState.Queued;
            }

            stop.Cancel();
            if (cancel)
            {
                Task<DiscoveryResult<CancelTicketResponse>> pending = client.CancelTicketCallAsync(ticketId, TicketRef, CancellationToken.None);
                pending.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        /// <inheritdoc />
        public override string ToString() => $"Ticket({TicketRef}, {State})";

        /// <summary>True when a poll or submit answer is matched and carries its placement.</summary>
        internal static bool TryMatch(TicketResponse response, out MatchAssignment placed)
        {
            placed = null;
            if (response == null || !string.Equals(response.Status, "matched", StringComparison.Ordinal)
                || string.IsNullOrEmpty(response.AllocationId) || string.IsNullOrEmpty(response.Ip) || !response.Port.HasValue)
            {
                return false;
            }

            placed = new MatchAssignment(response.AllocationId, response.ServerId, response.Ip, response.Port.Value, response.Backfill == true, response.Location);
            return true;
        }

        private void Finish(TicketState terminal, MatchAssignment placed, DiscoveryCallResult error)
        {
            lock (sync)
            {
                if (state != TicketState.Queued)
                {
                    return;
                }

                state = terminal;
                if (placed != null)
                {
                    match = placed;
                }

                if (error != null)
                {
                    lastError = error;
                }
            }

            stop.Cancel();
            client.LogTicket(this, terminal);
            Action<TicketHandle> handler = Changed;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this);
            }
            catch (Exception e)
            {
                client.LogHandlerFailure("TicketHandle.Changed", e);
            }
        }
    }
}
