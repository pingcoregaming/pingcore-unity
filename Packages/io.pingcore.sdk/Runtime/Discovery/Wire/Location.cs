using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Discovery.Client.Wire
{
    /// <summary>One platform location. <c>id</c> equals the <c>meta.location</c> hosted game servers advertise.</summary>
    [Preserve]
    public sealed class Location
    {
        /// <summary><c>id</c>: the key for <c>latency</c> maps.</summary>
        [JsonProperty("id", Required = Required.Always)]
        public string Id { get; set; }

        /// <summary><c>name</c>.</summary>
        [JsonProperty("name", Required = Required.Always)]
        public string Name { get; set; }

        /// <summary><c>pingUrl</c>: WebSocket latency beacon, or null when the zone has none.</summary>
        [JsonProperty("pingUrl", Required = Required.AllowNull, NullValueHandling = NullValueHandling.Include)]
        public string PingUrl { get; set; }

        /// <summary><c>enabled</c>.</summary>
        [JsonProperty("enabled", Required = Required.Always)]
        public bool Enabled { get; set; }
    }
}
