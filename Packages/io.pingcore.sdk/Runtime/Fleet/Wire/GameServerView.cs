using System.Collections.Generic;
using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Fleet.Wire
{
    /// <summary>
    /// The Agones-style GameServer projection the local SDK endpoint serves on
    /// <c>GET /gameserver</c> and on each watch-stream line. No envelope: local SDK endpoint
    /// bodies have no <c>error</c> field. Every field is optional in the spec.
    /// </summary>
    [Preserve]
    [WireContract(WireContractAttribute.Supervisor, "GET", "/gameserver", WireDirection.Response, 200)]
    public sealed class GameServerView
    {
        /// <summary><c>object_meta</c>: name, labels and annotations (the allocation context rides here).</summary>
        [JsonProperty("object_meta", NullValueHandling = NullValueHandling.Ignore)]
        public GameServerObjectMeta ObjectMeta { get; set; }

        /// <summary><c>status</c>: lifecycle state, address, ports and counters.</summary>
        [JsonProperty("status", NullValueHandling = NullValueHandling.Ignore)]
        public GameServerStatus Status { get; set; }
    }

    /// <summary>GameServer <c>object_meta</c>.</summary>
    [Preserve]
    public sealed class GameServerObjectMeta
    {
        /// <summary><c>name</c>.</summary>
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }

        /// <summary><c>namespace</c>.</summary>
        [JsonProperty("namespace", NullValueHandling = NullValueHandling.Ignore)]
        public string Namespace { get; set; }

        /// <summary><c>labels</c>.</summary>
        [JsonProperty("labels", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, string> Labels { get; set; }

        /// <summary><c>annotations</c>: string values; <c>pingcore.io/allocation-context</c> holds JSON inside a string.</summary>
        [JsonProperty("annotations", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, string> Annotations { get; set; }
    }

    /// <summary>GameServer <c>status</c>.</summary>
    [Preserve]
    public sealed class GameServerStatus
    {
        /// <summary><c>state</c>: <c>Scheduled</c>, <c>Ready</c>, <c>Allocated</c> or <c>Shutdown</c>.</summary>
        [JsonProperty("state", NullValueHandling = NullValueHandling.Ignore)]
        public string State { get; set; }

        /// <summary><c>address</c>.</summary>
        [JsonProperty("address", NullValueHandling = NullValueHandling.Ignore)]
        public string Address { get; set; }

        /// <summary><c>ports</c>.</summary>
        [JsonProperty("ports", NullValueHandling = NullValueHandling.Ignore)]
        public List<GameServerPort> Ports { get; set; }

        /// <summary><c>counters</c>: by counter name.</summary>
        [JsonProperty("counters", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, CounterView> Counters { get; set; }
    }

    /// <summary>One entry of GameServer <c>status.ports</c>.</summary>
    [Preserve]
    public sealed class GameServerPort
    {
        /// <summary><c>name</c>.</summary>
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }

        /// <summary><c>port</c>.</summary>
        [JsonProperty("port", NullValueHandling = NullValueHandling.Ignore)]
        public int? Port { get; set; }
    }
}
