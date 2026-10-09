using Newtonsoft.Json;
using PingCore.Core;
using PingCore.Core.Wire;

namespace PingCore.Discovery.Client.Wire
{
    /// <summary>
    /// A matchmaking ticket, as answered by submit (<c>POST .../tickets</c>) and by poll
    /// (<c>GET .../tickets/{ticketId}</c>). The two answers share the identity fields; each
    /// adds its own (submit: <c>expiresIn</c>, <c>joinInProgress</c>, <c>minSessionSize</c>,
    /// <c>relaxAfterSeconds</c>; poll: the match placement fields). A field one answer does not
    /// send stays absent on re-serialization, and a <c>...Specified</c> flag tells a field sent
    /// as null apart from a field not sent.
    /// </summary>
    [Preserve]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/apps/{publicId}/tickets", WireDirection.Response, 200)]
    [WireContract(WireContractAttribute.Discovery, "GET", "/v1/apps/{publicId}/tickets/{ticketId}", WireDirection.Response, 200)]
    public sealed class TicketResponse : WireResponse
    {
        private int? minSessionSize;
        private int? relaxAfterSeconds;
        private string allocationId;
        private string serverId;
        private string ip;
        private int? port;
        private string location;
        private long? matchedAt;

        /// <summary><c>ticketId</c>.</summary>
        [JsonProperty("ticketId", Required = Required.Always)]
        public string TicketId { get; set; }

        /// <summary><c>status</c>: <c>queued</c> or <c>matched</c>.</summary>
        [JsonProperty("status", Required = Required.Always)]
        public string Status { get; set; }

        /// <summary><c>queue</c>.</summary>
        [JsonProperty("queue", Required = Required.Always)]
        public string Queue { get; set; }

        /// <summary><c>expiresIn</c> (submit only): seconds until the ticket expires.</summary>
        [JsonProperty("expiresIn", NullValueHandling = NullValueHandling.Ignore)]
        public int? ExpiresIn { get; set; }

        /// <summary><c>allocationId</c> (poll only): string, or null until matched.</summary>
        [JsonProperty("allocationId", NullValueHandling = NullValueHandling.Include)]
        public string AllocationId { get => allocationId; set { allocationId = value; AllocationIdSpecified = true; } }

        /// <summary>True when <c>allocationId</c> was on the wire (null included) or has been set.</summary>
        [JsonIgnore]
        public bool AllocationIdSpecified { get; set; }

        /// <summary><c>serverId</c> (poll only): string, or null until matched.</summary>
        [JsonProperty("serverId", NullValueHandling = NullValueHandling.Include)]
        public string ServerId { get => serverId; set { serverId = value; ServerIdSpecified = true; } }

        /// <summary>True when <c>serverId</c> was on the wire (null included) or has been set.</summary>
        [JsonIgnore]
        public bool ServerIdSpecified { get; set; }

        /// <summary><c>ip</c> (poll only): string, or null until matched.</summary>
        [JsonProperty("ip", NullValueHandling = NullValueHandling.Include)]
        public string Ip { get => ip; set { ip = value; IpSpecified = true; } }

        /// <summary>True when <c>ip</c> was on the wire (null included) or has been set.</summary>
        [JsonIgnore]
        public bool IpSpecified { get; set; }

        /// <summary><c>port</c> (poll only): integer, or null until matched.</summary>
        [JsonProperty("port", NullValueHandling = NullValueHandling.Include)]
        public int? Port { get => port; set { port = value; PortSpecified = true; } }

        /// <summary>True when <c>port</c> was on the wire (null included) or has been set.</summary>
        [JsonIgnore]
        public bool PortSpecified { get; set; }

        /// <summary><c>backfill</c> (poll only): true when placed into a running session.</summary>
        [JsonProperty("backfill", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Backfill { get; set; }

        /// <summary><c>location</c> (poll only): the placement location id, or null.</summary>
        [JsonProperty("location", NullValueHandling = NullValueHandling.Include)]
        public string Location { get => location; set { location = value; LocationSpecified = true; } }

        /// <summary>True when <c>location</c> was on the wire (null included) or has been set.</summary>
        [JsonIgnore]
        public bool LocationSpecified { get; set; }

        /// <summary><c>matchedAt</c> (poll only): epoch milliseconds, or null until matched.</summary>
        [JsonProperty("matchedAt", NullValueHandling = NullValueHandling.Include)]
        public long? MatchedAt { get => matchedAt; set { matchedAt = value; MatchedAtSpecified = true; } }

        /// <summary>True when <c>matchedAt</c> was on the wire (null included) or has been set.</summary>
        [JsonIgnore]
        public bool MatchedAtSpecified { get; set; }

        /// <summary><c>ownerKind</c>: <c>backend</c> or <c>player</c>.</summary>
        [JsonProperty("ownerKind", Required = Required.Always)]
        public string OwnerKind { get; set; }

        /// <summary><c>ownerPlayerId</c>: the owning player's id, or null for a backend ticket.</summary>
        [JsonProperty("ownerPlayerId", Required = Required.AllowNull, NullValueHandling = NullValueHandling.Include)]
        public string OwnerPlayerId { get; set; }

        /// <summary><c>joinInProgress</c> (submit only).</summary>
        [JsonProperty("joinInProgress", NullValueHandling = NullValueHandling.Ignore)]
        public bool? JoinInProgress { get; set; }

        /// <summary><c>minSessionSize</c> (submit only): integer or null.</summary>
        [JsonProperty("minSessionSize", NullValueHandling = NullValueHandling.Include)]
        public int? MinSessionSize { get => minSessionSize; set { minSessionSize = value; MinSessionSizeSpecified = true; } }

        /// <summary>True when <c>minSessionSize</c> was on the wire (null included) or has been set.</summary>
        [JsonIgnore]
        public bool MinSessionSizeSpecified { get; set; }

        /// <summary><c>relaxAfterSeconds</c> (submit only): the value in force, or null.</summary>
        [JsonProperty("relaxAfterSeconds", NullValueHandling = NullValueHandling.Include)]
        public int? RelaxAfterSeconds { get => relaxAfterSeconds; set { relaxAfterSeconds = value; RelaxAfterSecondsSpecified = true; } }

        /// <summary>True when <c>relaxAfterSeconds</c> was on the wire (null included) or has been set.</summary>
        [JsonIgnore]
        public bool RelaxAfterSecondsSpecified { get; set; }

        /// <summary><c>createdAt</c>: epoch milliseconds.</summary>
        [JsonProperty("createdAt", Required = Required.Always)]
        public long CreatedAt { get; set; }

        /// <summary><c>expiresAt</c>: epoch milliseconds.</summary>
        [JsonProperty("expiresAt", Required = Required.Always)]
        public long ExpiresAt { get; set; }
    }
}
