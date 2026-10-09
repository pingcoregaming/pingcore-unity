using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;

namespace PingCore.Editor.Workspace.Api.Wire
{
    /// <summary>
    /// <c>GET /fleets/{id}/live</c>: what Discovery reports for the fleet's game servers, per
    /// member deployment. <c>counts</c> is Discovery's own app-wide object and stays untyped.
    /// </summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "GET", "/fleets/{id}/live", WireDirection.Response, 200)]
    public sealed class FleetLiveResponse
    {
        [JsonProperty("deployments", NullValueHandling = NullValueHandling.Ignore)]
        public List<FleetLiveDeploymentView> Deployments { get; set; }

        /// <summary>Game servers live in Discovery that no platform game server row explains.</summary>
        [JsonProperty("unmappedServers", NullValueHandling = NullValueHandling.Ignore)]
        public List<FleetLiveServerView> UnmappedServers { get; set; }

        [JsonProperty("counts", NullValueHandling = NullValueHandling.Ignore)]
        public JObject Counts { get; set; }

        [JsonProperty("desiredReadyServers", NullValueHandling = NullValueHandling.Include)]
        public int? DesiredReadyServers { get; set; }

        [JsonProperty("matchmaking", NullValueHandling = NullValueHandling.Ignore)]
        public FleetLiveMatchmakingView Matchmaking { get; set; }
    }

    /// <summary>Live counts of one member deployment.</summary>
    [Preserve]
    public sealed class FleetLiveDeploymentView
    {
        [JsonProperty("brandDeploymentId", NullValueHandling = NullValueHandling.Ignore)]
        public long BrandDeploymentId { get; set; }

        [JsonProperty("total", NullValueHandling = NullValueHandling.Ignore)]
        public int Total { get; set; }

        [JsonProperty("ready", NullValueHandling = NullValueHandling.Ignore)]
        public int Ready { get; set; }

        [JsonProperty("inSession", NullValueHandling = NullValueHandling.Ignore)]
        public int InSession { get; set; }

        [JsonProperty("draining", NullValueHandling = NullValueHandling.Ignore)]
        public int Draining { get; set; }

        [JsonProperty("absent", NullValueHandling = NullValueHandling.Ignore)]
        public int Absent { get; set; }

        [JsonProperty("absentServerIds", NullValueHandling = NullValueHandling.Ignore)]
        public List<long> AbsentServerIds { get; set; }

        [JsonProperty("alert", NullValueHandling = NullValueHandling.Include)]
        public string Alert { get; set; }

        [JsonProperty("provenDeadExcludedByCron", NullValueHandling = NullValueHandling.Include)]
        public int? ProvenDeadExcludedByCron { get; set; }

        /// <summary>Set while scale-up is backing off after a brand resource limit refusal, else null.</summary>
        [JsonProperty("scaleUpBlocked", NullValueHandling = NullValueHandling.Include)]
        public JObject ScaleUpBlocked { get; set; }

        [JsonProperty("servers", NullValueHandling = NullValueHandling.Ignore)]
        public List<FleetLiveServerView> Servers { get; set; }
    }

    /// <summary>One game server as Discovery reports it.</summary>
    [Preserve]
    public sealed class FleetLiveServerView
    {
        [JsonProperty("serverId", NullValueHandling = NullValueHandling.Ignore)]
        public string ServerId { get; set; }

        [JsonProperty("tier", NullValueHandling = NullValueHandling.Ignore)]
        public string Tier { get; set; }

        [JsonProperty("state", NullValueHandling = NullValueHandling.Ignore)]
        public string State { get; set; }

        [JsonProperty("name", NullValueHandling = NullValueHandling.Include)]
        public string Name { get; set; }

        [JsonProperty("ip", NullValueHandling = NullValueHandling.Include)]
        public string Ip { get; set; }

        [JsonProperty("port", NullValueHandling = NullValueHandling.Include)]
        public int? Port { get; set; }

        [JsonProperty("players", NullValueHandling = NullValueHandling.Include)]
        public int? Players { get; set; }

        [JsonProperty("maxPlayers", NullValueHandling = NullValueHandling.Include)]
        public int? MaxPlayers { get; set; }

        [JsonProperty("version", NullValueHandling = NullValueHandling.Include)]
        public string Version { get; set; }

        [JsonProperty("verified", NullValueHandling = NullValueHandling.Include)]
        public string Verified { get; set; }

        [JsonProperty("lastProbeError", NullValueHandling = NullValueHandling.Include)]
        public string LastProbeError { get; set; }

        [JsonProperty("counters", NullValueHandling = NullValueHandling.Include)]
        public JObject Counters { get; set; }

        /// <summary>Last state assertion, epoch milliseconds.</summary>
        [JsonProperty("updatedAt", NullValueHandling = NullValueHandling.Include)]
        public long? UpdatedAt { get; set; }
    }

    /// <summary>The fleet app's matchmaking backlog.</summary>
    [Preserve]
    public sealed class FleetLiveMatchmakingView
    {
        [JsonProperty("queuedTickets", NullValueHandling = NullValueHandling.Ignore)]
        public int QueuedTickets { get; set; }

        [JsonProperty("pendingAllocations", NullValueHandling = NullValueHandling.Ignore)]
        public int PendingAllocations { get; set; }

        /// <summary>Newest first, at most 25.</summary>
        [JsonProperty("recentAllocations", NullValueHandling = NullValueHandling.Ignore)]
        public List<FleetLiveAllocationView> RecentAllocations { get; set; }
    }

    /// <summary>One recent allocation of the fleet app.</summary>
    [Preserve]
    public sealed class FleetLiveAllocationView
    {
        [JsonProperty("allocationId", NullValueHandling = NullValueHandling.Ignore)]
        public string AllocationId { get; set; }

        [JsonProperty("serverId", NullValueHandling = NullValueHandling.Ignore)]
        public string ServerId { get; set; }

        /// <summary>Epoch milliseconds.</summary>
        [JsonProperty("createdAt", NullValueHandling = NullValueHandling.Ignore)]
        public long CreatedAt { get; set; }
    }
}
