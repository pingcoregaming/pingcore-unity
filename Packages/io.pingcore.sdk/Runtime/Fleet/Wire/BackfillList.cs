using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;

namespace PingCore.Fleet.Wire
{
    /// <summary>The body of <c>GET /v1/backfills</c>: every live backfill, oldest first. A read: it never integrates the game.</summary>
    [Preserve]
    [WireContract(WireContractAttribute.Supervisor, "GET", "/v1/backfills", WireDirection.Response, 200)]
    public sealed class BackfillList
    {
        /// <summary><c>backfills</c>.</summary>
        [JsonProperty("backfills", NullValueHandling = NullValueHandling.Ignore)]
        public List<BackfillView> Backfills { get; set; }
    }

    /// <summary>
    /// One backfill: players the matchmaker matched INTO this game server's running session.
    /// <c>context.roster</c> is the incoming players; authorise connecting players against it
    /// and count the ones not yet connected as expected joiners when republishing the joinable record.
    /// </summary>
    [Preserve]
    public sealed class BackfillView
    {
        private string sessionId;
        private JObject context;

        /// <summary><c>allocationId</c>: the backfill's own id; end it with <c>EndSessionAsync</c> to end only this backfill.</summary>
        [JsonProperty("allocationId", NullValueHandling = NullValueHandling.Ignore)]
        public string AllocationId { get; set; }

        /// <summary><c>sessionId</c>: the session the players join, or null.</summary>
        [JsonProperty("sessionId", NullValueHandling = NullValueHandling.Include)]
        public string SessionId { get => sessionId; set { sessionId = value; SessionIdSpecified = true; } }

        /// <summary>True when <c>sessionId</c> was on the wire (null included) or has been set.</summary>
        [JsonIgnore]
        public bool SessionIdSpecified { get; set; }

        /// <summary><c>claims</c>: the counter claims the backfill makes, for example <c>{"players": 2}</c>.</summary>
        [JsonProperty("claims", NullValueHandling = NullValueHandling.Ignore)]
        public JObject Claims { get; set; }

        /// <summary><c>context</c>: the backfill context (roster, queue, sessionId), or null.</summary>
        [JsonProperty("context", NullValueHandling = NullValueHandling.Include)]
        public JObject Context { get => context; set { context = value; ContextSpecified = true; } }

        /// <summary>True when <c>context</c> was on the wire (null included) or has been set.</summary>
        [JsonIgnore]
        public bool ContextSpecified { get; set; }

        /// <summary><c>deliveredAt</c>: epoch milliseconds.</summary>
        [JsonProperty("deliveredAt", NullValueHandling = NullValueHandling.Ignore)]
        public long? DeliveredAt { get; set; }
    }
}
