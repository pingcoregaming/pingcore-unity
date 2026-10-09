using System.Collections.Generic;
using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Fleet.Wire
{
    /// <summary>
    /// The 200 echo of <c>POST /v1/sessions/{allocationId}/joinable</c>: the shadow record the
    /// supervisor stored and forwarded to Discovery, with the path id as <c>sessionId</c>.
    /// The snapshot spec gives this answer no JSON schema, so it carries no
    /// <c>[WireContract]</c>; its shape is pinned by the Fleet EditMode body tests, transcribed
    /// from the supervisor's own route test.
    /// </summary>
    [Preserve]
    public sealed class JoinableSessionRecord
    {
        /// <summary><c>queue</c>: <c>default</c> when the game left it out.</summary>
        [JsonProperty("queue", Required = Required.Always)]
        public string Queue { get; set; }

        /// <summary><c>openSeats</c>.</summary>
        [JsonProperty("openSeats", Required = Required.Always)]
        public int OpenSeats { get; set; }

        /// <summary><c>sessionSize</c>, when the game sent it.</summary>
        [JsonProperty("sessionSize", NullValueHandling = NullValueHandling.Ignore)]
        public int? SessionSize { get; set; }

        /// <summary><c>attributes</c>, when the game sent them.</summary>
        [JsonProperty("attributes", NullValueHandling = NullValueHandling.Ignore)]
        [JsonConverter(typeof(NumberMapConverter))]
        public Dictionary<string, double> Attributes { get; set; }

        /// <summary><c>sessionId</c>: the allocation id from the path.</summary>
        [JsonProperty("sessionId", NullValueHandling = NullValueHandling.Ignore)]
        public string SessionId { get; set; }

        /// <summary><c>ttlSeconds</c>, when the game sent it.</summary>
        [JsonProperty("ttlSeconds", NullValueHandling = NullValueHandling.Ignore)]
        public int? TtlSeconds { get; set; }
    }
}
