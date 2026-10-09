using Newtonsoft.Json;
using PingCore.Core;
using PingCore.Core.Wire;

namespace PingCore.Discovery.Host.Wire
{
    /// <summary>
    /// The answer to a delist (<c>DELETE /v1/servers/{serverId}</c>), sent once on a clean stop so
    /// players stop seeing the game server at once instead of waiting out the heartbeat TTL.
    /// Idempotent: <c>removed</c> is false when the entry was already gone.
    /// </summary>
    [Preserve]
    [WireContract(WireContractAttribute.Discovery, "DELETE", "/v1/servers/{serverId}", WireDirection.Response, 200)]
    public sealed class DelistResponse : WireResponse
    {
        /// <summary><c>serverId</c>: the id that was delisted, as sent.</summary>
        [JsonProperty("serverId", Required = Required.Always)]
        public string ServerId { get; set; }

        /// <summary><c>removed</c>: true when an entry was removed, false when it was already gone.</summary>
        [JsonProperty("removed", Required = Required.Always)]
        public bool Removed { get; set; }
    }
}
