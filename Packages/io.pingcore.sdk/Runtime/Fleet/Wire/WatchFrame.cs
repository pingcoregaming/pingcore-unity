using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Fleet.Wire
{
    /// <summary>
    /// One line of <c>GET /watch/gameserver</c>: <c>{"result": GameServer}</c>, the first at
    /// once, then one per change. The snapshot spec describes the stream in prose only (no JSON
    /// schema), so this carries no <c>[WireContract]</c>; the Fleet EditMode body tests pin it.
    /// </summary>
    [Preserve]
    public sealed class WatchFrame
    {
        /// <summary><c>result</c>: the GameServer view, the same projection as <c>GET /gameserver</c>.</summary>
        [JsonProperty("result", Required = Required.Always)]
        public GameServerView Result { get; set; }
    }
}
