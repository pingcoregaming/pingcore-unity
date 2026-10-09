using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Fleet.Wire
{
    /// <summary>
    /// One counter (spec schema <c>Counter</c>): the body of <c>GET /v1beta1/counters/{name}</c>,
    /// the echo of <c>PATCH /v1beta1/counters/{name}</c>, and each value of GameServer <c>status.counters</c>, where <c>name</c> is absent.
    /// Agones int64 fields are strings on the wire, so <c>count</c> and <c>capacity</c> stay strings.
    /// </summary>
    [Preserve]
    [WireContract(WireContractAttribute.Supervisor, "GET", "/v1beta1/counters/{name}", WireDirection.Response, 200)]
    [WireContract(WireContractAttribute.Supervisor, "PATCH", "/v1beta1/counters/{name}", WireDirection.Response, 200)]
    public sealed class CounterView
    {
        /// <summary><c>name</c>: absent inside <c>status.counters</c>.</summary>
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }

        /// <summary><c>count</c>: int64 as a string.</summary>
        [JsonProperty("count", NullValueHandling = NullValueHandling.Ignore)]
        public string Count { get; set; }

        /// <summary><c>capacity</c>: int64 as a string.</summary>
        [JsonProperty("capacity", NullValueHandling = NullValueHandling.Ignore)]
        public string Capacity { get; set; }
    }
}
