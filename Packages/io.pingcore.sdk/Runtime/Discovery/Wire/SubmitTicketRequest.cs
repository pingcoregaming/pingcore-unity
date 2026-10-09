using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;

namespace PingCore.Discovery.Client.Wire
{
    /// <summary>
    /// A matchmaking ticket submission. Unset optional fields are not sent. The SDK mints
    /// <c>ticketId</c> itself and never lets the caller choose it.
    /// </summary>
    [Preserve]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/tickets", WireDirection.Request, 0)]
    public sealed class SubmitTicketRequest
    {
        private TicketFilters filters;
        private JObject context;

        /// <summary><c>ticketId</c>: idempotency anchor.</summary>
        [JsonProperty("ticketId", NullValueHandling = NullValueHandling.Ignore)]
        public string TicketId { get; set; }

        /// <summary><c>queue</c>: defaults to <c>default</c> on the server.</summary>
        [JsonProperty("queue", NullValueHandling = NullValueHandling.Ignore)]
        public string Queue { get; set; }

        /// <summary><c>sessionSize</c>: players per formed match.</summary>
        [JsonProperty("sessionSize", Required = Required.Always)]
        public int SessionSize { get; set; }

        /// <summary><c>partySize</c>: players on this ticket.</summary>
        [JsonProperty("partySize", NullValueHandling = NullValueHandling.Ignore)]
        public int? PartySize { get; set; }

        /// <summary><c>filters</c>: object or null.</summary>
        [JsonProperty("filters", NullValueHandling = NullValueHandling.Include)]
        public TicketFilters Filters { get => filters; set { filters = value; FiltersSpecified = true; } }

        /// <summary>True when <c>filters</c> is sent (null included).</summary>
        [JsonIgnore]
        public bool FiltersSpecified { get; set; }

        /// <summary><c>context</c>: merged into the allocation context; object or null.</summary>
        [JsonProperty("context", NullValueHandling = NullValueHandling.Include)]
        public JObject Context { get => context; set { context = value; ContextSpecified = true; } }

        /// <summary>True when <c>context</c> is sent (null included).</summary>
        [JsonIgnore]
        public bool ContextSpecified { get; set; }

        /// <summary><c>minSessionSize</c>: requires <see cref="RelaxAfterSeconds"/>.</summary>
        [JsonProperty("minSessionSize", NullValueHandling = NullValueHandling.Ignore)]
        public int? MinSessionSize { get; set; }

        /// <summary><c>relaxAfterSeconds</c>: requires <see cref="MinSessionSize"/>.</summary>
        [JsonProperty("relaxAfterSeconds", NullValueHandling = NullValueHandling.Ignore)]
        public int? RelaxAfterSeconds { get; set; }

        /// <summary><c>attributes</c>: numbers the matcher can compare.</summary>
        [JsonProperty("attributes", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, double> Attributes { get; set; }

        /// <summary><c>rules</c>: per-attribute windows imposed on peers.</summary>
        [JsonProperty("rules", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, MatchRule> Rules { get; set; }

        /// <summary><c>latency</c>: measured round trip in milliseconds per location id.</summary>
        [JsonProperty("latency", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, int> Latency { get; set; }

        /// <summary><c>maxLatencyMs</c>: hard ceiling; requires <see cref="Latency"/>.</summary>
        [JsonProperty("maxLatencyMs", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxLatencyMs { get; set; }

        /// <summary><c>joinInProgress</c>: opt in to backfill.</summary>
        [JsonProperty("joinInProgress", NullValueHandling = NullValueHandling.Ignore)]
        public bool? JoinInProgress { get; set; }
    }

    /// <summary>Ticket <c>filters</c>: exact-match filters on the game servers a match may use.</summary>
    [Preserve]
    public sealed class TicketFilters
    {
        /// <summary><c>version</c>.</summary>
        [JsonProperty("version", NullValueHandling = NullValueHandling.Ignore)]
        public string Version { get; set; }

        /// <summary><c>meta</c>: exact-match meta filters, string values only.</summary>
        [JsonProperty("meta", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, string> Meta { get; set; }
    }

    /// <summary>One ticket <c>rules</c> entry: the window this ticket imposes on a peer's attribute.</summary>
    [Preserve]
    public sealed class MatchRule
    {
        /// <summary><c>maxDifference</c>: the window at submit time.</summary>
        [JsonProperty("maxDifference", Required = Required.Always)]
        public double MaxDifference { get; set; }

        /// <summary><c>widenPerSecond</c>: added to the window per second of this ticket's wait.</summary>
        [JsonProperty("widenPerSecond", NullValueHandling = NullValueHandling.Ignore)]
        public double? WidenPerSecond { get; set; }

        /// <summary><c>maxWiden</c>: cap on the widening.</summary>
        [JsonProperty("maxWiden", NullValueHandling = NullValueHandling.Ignore)]
        public double? MaxWiden { get; set; }
    }
}
