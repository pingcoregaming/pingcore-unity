using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Discovery.Client.Wire;

namespace PingCore.Discovery.Client
{
    /// <summary>Matchmaking tickets.</summary>
    public sealed partial class DiscoveryClient
    {
        /// <summary>
        /// Submits a matchmaking ticket. The SDK mints the ticket id (<see cref="SecureIds.NewId128"/>)
        /// and reuses it on its own retry; the caller never sets it. The player floors are checked
        /// first (session at least 2, the relaxation pair, party strictly below the session and at
        /// most 8, context at most 1024 bytes, the latency rules): a violation is a local
        /// <see cref="DiscoveryOutcome.InvalidRequest"/> with the reason the service would give and
        /// sends nothing. On success, <see cref="TicketHandle.WaitAsync"/> polls it to a match. A 401
        /// re-issue under another player id is resent only when no earlier attempt may have reached
        /// Discovery; otherwise it answers Unauthorized with <see cref="DiscoveryCallResult.IdentityChanged"/>
        /// and no handle (a ticket the old player may hold stays out of reach).
        /// </summary>
        public async Task<DiscoveryResult<TicketHandle>> SubmitTicketAsync(TicketOptions ticketOptions, CancellationToken cancellationToken)
        {
            TicketOptions o = ticketOptions ?? new TicketOptions();
            if (!o.SkipLocalFloors)
            {
                DiscoveryCallResult refused = RequestChecks.CheckTicket(o);
                if (refused != null)
                {
                    Write(DiscoveryLogLevel.Info, "tickets", "refused locally: " + refused);
                    return DiscoveryResult<TicketHandle>.From(refused);
                }
            }

            string ticketId = SecureIds.NewId128();
            var body = new SubmitTicketRequest
            {
                TicketId = ticketId,
                Queue = o.Queue?.Trim(),
                SessionSize = o.SessionSize,
                PartySize = o.PartySize,
                MinSessionSize = o.MinSessionSize,
                RelaxAfterSeconds = o.RelaxAfterSeconds,
                Latency = o.Latency != null && o.Latency.Count > 0 ? new Dictionary<string, int>(ToDictionary(o.Latency)) : null,
                MaxLatencyMs = o.MaxLatencyMs,
                JoinInProgress = o.JoinInProgress ? true : (bool?)null,
            };
            if (o.Filters != null)
            {
                body.Filters = o.Filters;
            }

            if (o.Context != null)
            {
                body.Context = o.Context;
            }

            DiscoveryRequest request = DiscoveryRequest.Json("POST", AppPath + "/tickets", body, "POST /v1/apps/{publicId}/tickets");
            string playerId = null;
            DiscoveryResult<TicketResponse> answer = await SendAuthorisedAsync<TicketResponse>(
                token =>
                {
                    playerId = token.PlayerId;
                    return request;
                },
                "tickets.submit",
                true,
                OwnerBinding.OnceAnAttemptMayHaveLanded,
                cancellationToken);
            if (!answer.IsOk)
            {
                Write(DiscoveryLogLevel.Warning, "tickets", "submit " + SecureIds.Ref(ticketId) + " answered " + answer);
                return DiscoveryResult<TicketHandle>.From(answer);
            }

            var handle = new TicketHandle(this, scheduler, ticketId, playerId, answer.Value);
            Write(DiscoveryLogLevel.Info, "tickets", "submitted " + handle.TicketRef + " to queue " + handle.Queue + ": " + handle.State);
            return new DiscoveryResult<TicketHandle>(DiscoveryOutcome.Ok, answer.Status, null, null, null, null, answer.RateLimit, handle);
        }

        /// <summary>One poll, no retry (the handle's loop owns the cadence); the 401 re-issue still applies, but a poll is owner-bound, so it is not resent under another player id.</summary>
        internal Task<DiscoveryResult<TicketResponse>> PollTicketAsync(string ticketId, string ticketRef, CancellationToken cancellationToken)
        {
            DiscoveryRequest request = DiscoveryRequest.Create("GET", AppPath + "/tickets/" + DiscoveryCaller.Segment(ticketId), "GET /v1/apps/{publicId}/tickets/{ticketId}");
            return SendAuthorisedAsync<TicketResponse>(_ => request, "tickets.poll " + ticketRef, false, OwnerBinding.Always, cancellationToken);
        }

        /// <summary>The cancel call, retried like every idempotent write; owner-bound like the poll.</summary>
        internal Task<DiscoveryResult<CancelTicketResponse>> CancelTicketCallAsync(string ticketId, string ticketRef, CancellationToken cancellationToken)
        {
            DiscoveryRequest request = DiscoveryRequest.Create("DELETE", AppPath + "/tickets/" + DiscoveryCaller.Segment(ticketId), "DELETE /v1/apps/{publicId}/tickets/{ticketId}");
            return SendAuthorisedAsync<CancelTicketResponse>(_ => request, "tickets.cancel " + ticketRef, true, OwnerBinding.Always, cancellationToken);
        }
    }
}
