using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Fleet.Wire
{
    /// <summary>
    /// The body of <c>PATCH /v1beta1/counters/{name}</c>. The shim sends only <c>count</c>;
    /// the endpoint also accepts integer strings, <c>countDiff</c> and <c>capacity</c>. Every
    /// PATCH integrates the game, even a refused one.
    /// </summary>
    [Preserve]
    [WireContract(WireContractAttribute.Supervisor, "PATCH", "/v1beta1/counters/{name}", WireDirection.Request, 0)]
    public sealed class CounterPatchRequest
    {
        /// <summary><c>count</c>: the new count; the supervisor clamps a negative value to 0.</summary>
        [JsonProperty("count", NullValueHandling = NullValueHandling.Ignore)]
        public long? Count { get; set; }

        /// <summary><c>countDiff</c>: added to the current count. Not sent by the shim.</summary>
        [JsonProperty("countDiff", NullValueHandling = NullValueHandling.Ignore)]
        public long? CountDiff { get; set; }

        /// <summary><c>capacity</c>: overrides the fleet block's capacity. Not sent by the shim.</summary>
        [JsonProperty("capacity", NullValueHandling = NullValueHandling.Ignore)]
        public long? Capacity { get; set; }
    }
}
