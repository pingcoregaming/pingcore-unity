using System.Collections.Generic;
using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Fleet.Wire
{
    /// <summary>
    /// The body of <c>POST /v1/sessions/{allocationId}/joinable</c>: the running session
    /// accepts <see cref="OpenSeats"/> more players from the matchmaker queue
    /// <see cref="Queue"/>. Republish <c>openSeats = maxPlayers - connected - expectedJoiners</c>
    /// whenever seats change and at least every <c>ttlSeconds / 2</c>, where
    /// <c>expectedJoiners</c> counts every backfill roster entry that has not yet connected.
    /// </summary>
    [Preserve]
    [WireContract(WireContractAttribute.Supervisor, "POST", "/v1/sessions/{allocationId}/joinable", WireDirection.Request, 0)]
    public sealed class JoinableSessionRequest
    {
        /// <summary><c>queue</c>: the matchmaker queue; the supervisor uses <c>default</c> when it is left out.</summary>
        [JsonProperty("queue", NullValueHandling = NullValueHandling.Ignore)]
        public string Queue { get; set; }

        /// <summary><c>openSeats</c>: a non-negative integer. Required.</summary>
        [JsonProperty("openSeats", Required = Required.Always)]
        public int OpenSeats { get; set; }

        /// <summary><c>sessionSize</c>: a positive integer, or left out.</summary>
        [JsonProperty("sessionSize", NullValueHandling = NullValueHandling.Ignore)]
        public int? SessionSize { get; set; }

        /// <summary><c>attributes</c>: numbers by name, matched against queue rules; or left out.</summary>
        [JsonProperty("attributes", NullValueHandling = NullValueHandling.Ignore)]
        [JsonConverter(typeof(NumberMapConverter))]
        public Dictionary<string, double> Attributes { get; set; }

        /// <summary><c>ttlSeconds</c>: record lifetime; supervisor 1.3.4 refuses less than 5 locally (Discovery's own bound).</summary>
        [JsonProperty("ttlSeconds", NullValueHandling = NullValueHandling.Ignore)]
        public int? TtlSeconds { get; set; }
    }
}
