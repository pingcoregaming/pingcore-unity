using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;

namespace PingCore.Discovery.Host.Wire
{
    /// <summary>A self-hosted game server's heartbeat. Unset optional fields are not sent.</summary>
    [Preserve]
    [WireContract(WireContractAttribute.Discovery, "POST", "/v1/heartbeat", WireDirection.Request, 0)]
    public sealed class HeartbeatRequest
    {
        private int? queryPort;
        private string version;
        private JObject meta;

        /// <summary><c>serverId</c>: stable identifier; Discovery defaults it to <c>ip:port</c>.</summary>
        [JsonProperty("serverId", NullValueHandling = NullValueHandling.Ignore)]
        public string ServerId { get; set; }

        /// <summary><c>name</c>.</summary>
        [JsonProperty("name", Required = Required.Always)]
        public string Name { get; set; }

        /// <summary><c>ip</c>: Discovery defaults it to the observed source address.</summary>
        [JsonProperty("ip", NullValueHandling = NullValueHandling.Ignore)]
        public string Ip { get; set; }

        /// <summary><c>port</c>: the game port.</summary>
        [JsonProperty("port", Required = Required.Always)]
        public int Port { get; set; }

        /// <summary><c>queryPort</c>: probed instead of <c>port</c> for <c>udp-echo</c>; integer or null.</summary>
        [JsonProperty("queryPort", NullValueHandling = NullValueHandling.Include)]
        public int? QueryPort { get => queryPort; set { queryPort = value; QueryPortSpecified = true; } }

        /// <summary>True when <c>queryPort</c> is sent (null included).</summary>
        [JsonIgnore]
        public bool QueryPortSpecified { get; set; }

        /// <summary><c>players</c>.</summary>
        [JsonProperty("players", Required = Required.Always)]
        public int Players { get; set; }

        /// <summary><c>maxPlayers</c>.</summary>
        [JsonProperty("maxPlayers", Required = Required.Always)]
        public int MaxPlayers { get; set; }

        /// <summary><c>version</c>: the game's own version string, or null.</summary>
        [JsonProperty("version", NullValueHandling = NullValueHandling.Include)]
        public string Version { get => version; set { version = value; VersionSpecified = true; } }

        /// <summary>True when <c>version</c> is sent (null included).</summary>
        [JsonIgnore]
        public bool VersionSpecified { get; set; }

        /// <summary><c>meta</c>: studio-defined filterable fields, or null.</summary>
        [JsonProperty("meta", NullValueHandling = NullValueHandling.Include)]
        public JObject Meta { get => meta; set { meta = value; MetaSpecified = true; } }

        /// <summary>True when <c>meta</c> is sent (null included).</summary>
        [JsonIgnore]
        public bool MetaSpecified { get; set; }
    }
}
