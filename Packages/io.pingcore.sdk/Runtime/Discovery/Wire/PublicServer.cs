using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;

namespace PingCore.Discovery.Client.Wire
{
    /// <summary>A game server as players see it on the public list (spec schema <c>PublicServer</c>).</summary>
    [Preserve]
    public sealed class PublicServer
    {
        /// <summary><c>serverId</c>.</summary>
        [JsonProperty("serverId", Required = Required.Always)]
        public string ServerId { get; set; }

        /// <summary><c>name</c>.</summary>
        [JsonProperty("name", Required = Required.Always)]
        public string Name { get; set; }

        /// <summary><c>ip</c>.</summary>
        [JsonProperty("ip", Required = Required.Always)]
        public string Ip { get; set; }

        /// <summary><c>port</c>: the game port.</summary>
        [JsonProperty("port", Required = Required.Always)]
        public int Port { get; set; }

        /// <summary><c>players</c>.</summary>
        [JsonProperty("players", Required = Required.Always)]
        public int Players { get; set; }

        /// <summary><c>maxPlayers</c>.</summary>
        [JsonProperty("maxPlayers", Required = Required.Always)]
        public int MaxPlayers { get; set; }

        /// <summary><c>version</c>: string or null.</summary>
        [JsonProperty("version", Required = Required.AllowNull, NullValueHandling = NullValueHandling.Include)]
        public string Version { get; set; }

        /// <summary><c>meta</c>: studio-defined fields, or null.</summary>
        [JsonProperty("meta", Required = Required.AllowNull, NullValueHandling = NullValueHandling.Include)]
        public JObject Meta { get; set; }
    }
}
