using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Discovery.Client.Wire;

namespace PingCore.Discovery.Client
{
    /// <summary>Creation options for <see cref="DiscoveryClient.Create(DiscoveryClientOptions)"/>.</summary>
    public sealed class DiscoveryClientOptions
    {
        /// <summary>Discovery base URL, for example <c>https://discovery.pingcore.io</c>. Required.</summary>
        public string BaseUrl { get; set; }

        /// <summary>The Discovery app's public id (<c>dscp_...</c>). Required. One client per app, because player tokens are per app.</summary>
        public string AppPublicId { get; set; }

        /// <summary>HTTP seam; default <c>UnityWebRequestTransport</c> with <see cref="CallTimeout"/>.</summary>
        public IHttpTransport Transport { get; set; }

        /// <summary>Clock and delays; default <c>AwaitableScheduler</c> (Unity main thread).</summary>
        public IScheduler Scheduler { get; set; }

        /// <summary>Where the anonymous player token is kept between runs; default <c>PlayerPrefsTokenStore</c>.</summary>
        public IPlayerTokenStore TokenStore { get; set; }

        /// <summary>
        /// Separates player identities that share one token store (several clients on one PC).
        /// Empty by default; otherwise 1 to 32 characters from <c>[A-Za-z0-9_-]</c>.
        /// </summary>
        public string Profile { get; set; } = string.Empty;

        /// <summary>Log sink; default none. Entries never carry a token, a ticket id, a header or a body.</summary>
        public Action<DiscoveryLogEntry> Log { get; set; }

        /// <summary>Per-call timeout of the default transport.</summary>
        public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Total attempts per call when the answer is retryable (429 within <see cref="MaxRetryDelay"/>, 503, unexpected, no answer). At least 1.</summary>
        public int MaxAttempts { get; set; } = 3;

        /// <summary>The longest wait between attempts; a longer <c>Retry-After</c> is returned to the caller instead.</summary>
        public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(10);
    }

    /// <summary>Options for <see cref="DiscoveryClient.ReserveAsync"/>.</summary>
    public sealed class ReserveOptions
    {
        /// <summary>Seats to hold, 1 to 8 for a player token.</summary>
        public int Seats { get; set; } = 1;

        /// <summary>One id per seat. When null and <see cref="Seats"/> is 1, the SDK sends the token's own player id.</summary>
        public IReadOnlyList<string> PlayerIds { get; set; }

        /// <summary>Hold length in seconds, 5 to 300; null for the service default (60).</summary>
        public int? TtlSeconds { get; set; }

        /// <summary>Opaque object delivered to the game server; at most 1024 bytes of JSON for a player token.</summary>
        public JObject Context { get; set; }

        /// <summary>Idempotency anchor; minted by the SDK when null and reused on its own retry. Pass the same id to replay a hold.</summary>
        public string ReservationId { get; set; }
    }

    /// <summary>Options for <see cref="DiscoveryClient.QuickJoinAsync"/>.</summary>
    public sealed class QuickJoinOptions
    {
        /// <summary>Idempotency key; minted by the SDK when null and reused on its own retry. Pass the same key to replay.</summary>
        public string IdempotencyKey { get; set; }

        /// <summary>Seats to hold, 1 to 8 for a player token.</summary>
        public int Seats { get; set; } = 1;

        /// <summary>One id per seat. When null and <see cref="Seats"/> is 1, the SDK sends the token's own player id.</summary>
        public IReadOnlyList<string> PlayerIds { get; set; }

        /// <summary>Version and meta filters (operator objects allowed).</summary>
        public QuickJoinFilters Filters { get; set; }

        /// <summary>Opaque object delivered to the game server; at most 1024 bytes of JSON for a player token.</summary>
        public JObject Context { get; set; }

        /// <summary>Measured latency per location id (for example <see cref="LatencyResult.Medians"/>); at most 32 entries.</summary>
        public IReadOnlyDictionary<string, int> Latency { get; set; }

        /// <summary>Hard ceiling in ms; requires <see cref="Latency"/>.</summary>
        public int? MaxLatencyMs { get; set; }
    }

    /// <summary>Options for <see cref="DiscoveryClient.SubmitTicketAsync"/>. The SDK always mints the ticket id.</summary>
    public sealed class TicketOptions
    {
        /// <summary>Queue name, 1 to 50 characters; null for <c>default</c>.</summary>
        public string Queue { get; set; }

        /// <summary>Players per formed match; at least 2 for a player token.</summary>
        public int SessionSize { get; set; }

        /// <summary>Smallest session accepted once relaxation is due; sent together with <see cref="RelaxAfterSeconds"/>.</summary>
        public int? MinSessionSize { get; set; }

        /// <summary>Seconds before a smaller session may form; sent together with <see cref="MinSessionSize"/>. Discovery raises a value below 10 to 10.</summary>
        public int? RelaxAfterSeconds { get; set; }

        /// <summary>Players on this ticket, 1 to 8, strictly below <see cref="SessionSize"/> and <see cref="MinSessionSize"/>.</summary>
        public int PartySize { get; set; } = 1;

        /// <summary>Opt in to backfill into a running session.</summary>
        public bool JoinInProgress { get; set; }

        /// <summary>Measured latency per location id; at most 32 entries.</summary>
        public IReadOnlyDictionary<string, int> Latency { get; set; }

        /// <summary>Hard latency ceiling in ms; requires <see cref="Latency"/> with at least one value at or below it.</summary>
        public int? MaxLatencyMs { get; set; }

        /// <summary>Exact-match filters on the game servers a match may use.</summary>
        public TicketFilters Filters { get; set; }

        /// <summary>Merged into the allocation context; at most 1024 bytes of JSON for a player token.</summary>
        public JObject Context { get; set; }

        /// <summary>
        /// Skips the SDK's local floor checks so the service answers the 400 itself. Only for tests
        /// that prove the service refuses; a game never sets it.
        /// </summary>
        public bool SkipLocalFloors { get; set; }
    }

    /// <summary>One page of the server list.</summary>
    public sealed class ServerPage
    {
        /// <summary>Creates a page.</summary>
        public ServerPage(IReadOnlyList<PublicServer> servers, int totalServers, int returned, int limit, int offset, ServerListQuery next)
        {
            Servers = servers ?? Array.Empty<PublicServer>();
            TotalServers = totalServers;
            Returned = returned;
            Limit = limit;
            Offset = offset;
            Next = next;
        }

        /// <summary>The game servers on this page.</summary>
        public IReadOnlyList<PublicServer> Servers { get; }

        /// <summary>Matches after filtering, before paging.</summary>
        public int TotalServers { get; }

        /// <summary>Entries on this page.</summary>
        public int Returned { get; }

        /// <summary>Page size in force.</summary>
        public int Limit { get; }

        /// <summary>Offset in force.</summary>
        public int Offset { get; }

        /// <summary>True when more matches follow this page.</summary>
        public bool HasMore => Next != null;

        /// <summary>The query for the next page, or null on the last page.</summary>
        public ServerListQuery Next { get; }
    }

    /// <summary>Severity of a <see cref="DiscoveryLogEntry"/>.</summary>
    public enum DiscoveryLogLevel
    {
        /// <summary>Normal operation.</summary>
        Info,

        /// <summary>Something is wrong but the call carries on or returns a typed result.</summary>
        Warning,

        /// <summary>A failure the game should know about.</summary>
        Error,
    }

    /// <summary>One log line from the Discovery client. Never carries a token, a ticket id, a header or a body.</summary>
    public sealed class DiscoveryLogEntry
    {
        /// <summary>Creates an entry.</summary>
        public DiscoveryLogEntry(DiscoveryLogLevel level, string call, string message)
        {
            Level = level;
            Call = call;
            Message = message;
        }

        /// <summary>Severity.</summary>
        public DiscoveryLogLevel Level { get; }

        /// <summary>The call, for example <c>tickets.poll</c>.</summary>
        public string Call { get; }

        /// <summary>What happened.</summary>
        public string Message { get; }

        /// <inheritdoc />
        public override string ToString() => $"{Level} {Call}: {Message}";
    }
}
