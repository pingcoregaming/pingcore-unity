using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;

namespace PingCore.Editor.Workspace.Api.Wire
{
    /// <summary><c>GET /fleets</c>: every fleet of the workspace.</summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "GET", "/fleets", WireDirection.Response, 200)]
    public sealed class FleetListResponse
    {
        [JsonProperty("fleets", NullValueHandling = NullValueHandling.Ignore)]
        public List<FleetView> Fleets { get; set; }
    }

    /// <summary><c>GET /fleets/{id}</c>: one fleet with its member deployments.</summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "GET", "/fleets/{id}", WireDirection.Response, 200)]
    public sealed class FleetDetailResponse
    {
        [JsonProperty("fleet", NullValueHandling = NullValueHandling.Ignore)]
        public FleetView Fleet { get; set; }

        [JsonProperty("deployments", NullValueHandling = NullValueHandling.Ignore)]
        public List<FleetDeploymentView> Deployments { get; set; }
    }

    /// <summary>A fleet row.</summary>
    [Preserve]
    public sealed class FleetView
    {
        [JsonProperty("fleetId", NullValueHandling = NullValueHandling.Ignore)]
        public long FleetId { get; set; }

        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }

        /// <summary><c>active</c> or <c>paused</c>.</summary>
        [JsonProperty("status", NullValueHandling = NullValueHandling.Ignore)]
        public string Status { get; set; }

        [JsonProperty("gameId", NullValueHandling = NullValueHandling.Ignore)]
        public long GameId { get; set; }

        [JsonProperty("gameName", NullValueHandling = NullValueHandling.Include)]
        public string GameName { get; set; }

        [JsonProperty("discoveryAppId", NullValueHandling = NullValueHandling.Ignore)]
        public long DiscoveryAppId { get; set; }

        [JsonProperty("discoveryAppName", NullValueHandling = NullValueHandling.Include)]
        public string DiscoveryAppName { get; set; }

        [JsonProperty("discoveryPublicId", NullValueHandling = NullValueHandling.Include)]
        public string DiscoveryPublicId { get; set; }

        /// <summary>The Discovery agent config overrides, or null for the defaults. Read only: the plugin never edits a fleet (the panel and MCP <c>update_fleet</c> do).</summary>
        [JsonProperty("agentConfig", NullValueHandling = NullValueHandling.Include)]
        public JObject AgentConfig { get; set; }

        /// <summary>Only populated by the list.</summary>
        [JsonProperty("deploymentCount", NullValueHandling = NullValueHandling.Include)]
        public int? DeploymentCount { get; set; }

        /// <summary>Only populated by the list.</summary>
        [JsonProperty("serverCount", NullValueHandling = NullValueHandling.Include)]
        public int? ServerCount { get; set; }

        [JsonProperty("createdAt", NullValueHandling = NullValueHandling.Ignore)]
        public string CreatedAt { get; set; }

        [JsonProperty("updatedAt", NullValueHandling = NullValueHandling.Ignore)]
        public string UpdatedAt { get; set; }
    }

    /// <summary>A member deployment of a fleet.</summary>
    [Preserve]
    public sealed class FleetDeploymentView
    {
        [JsonProperty("brandDeploymentId", NullValueHandling = NullValueHandling.Ignore)]
        public long BrandDeploymentId { get; set; }

        [JsonProperty("friendlyName", NullValueHandling = NullValueHandling.Include)]
        public string FriendlyName { get; set; }

        [JsonProperty("deploymentStatus", NullValueHandling = NullValueHandling.Ignore)]
        public string DeploymentStatus { get; set; }

        [JsonProperty("locationId", NullValueHandling = NullValueHandling.Ignore)]
        public long LocationId { get; set; }

        [JsonProperty("locationName", NullValueHandling = NullValueHandling.Include)]
        public string LocationName { get; set; }

        [JsonProperty("serverCount", NullValueHandling = NullValueHandling.Ignore)]
        public int ServerCount { get; set; }

        [JsonProperty("minServers", NullValueHandling = NullValueHandling.Ignore)]
        public int MinServers { get; set; }

        [JsonProperty("maxServers", NullValueHandling = NullValueHandling.Ignore)]
        public int MaxServers { get; set; }

        [JsonProperty("bufferFloor", NullValueHandling = NullValueHandling.Ignore)]
        public int BufferFloor { get; set; }

        /// <summary>The deployment's player count.</summary>
        [JsonProperty("players", NullValueHandling = NullValueHandling.Ignore)]
        public int Players { get; set; }

        /// <summary>The <c>players</c> counter capacity this location advertises, and what sets it.</summary>
        [JsonProperty("playersCapacity", NullValueHandling = NullValueHandling.Ignore)]
        public PlayersCapacityView PlayersCapacity { get; set; }

        [JsonProperty("recyclePolicy", NullValueHandling = NullValueHandling.Ignore)]
        public string RecyclePolicy { get; set; }

        [JsonProperty("recycleResetPaths", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> RecycleResetPaths { get; set; }

        [JsonProperty("pinnedImageTag", NullValueHandling = NullValueHandling.Include)]
        public string PinnedImageTag { get; set; }

        [JsonProperty("pinnedContentVersion", NullValueHandling = NullValueHandling.Include)]
        public string PinnedContentVersion { get; set; }

        /// <summary><c>image</c>, <c>cdn_source</c> or <c>steam_depot</c>, or null.</summary>
        [JsonProperty("dataSourceType", NullValueHandling = NullValueHandling.Include)]
        public string DataSourceType { get; set; }

        /// <summary>What this location's game servers report running (comma separated mid-release), or null before any report. Evidence, not the pin.</summary>
        [JsonProperty("buildVersion", NullValueHandling = NullValueHandling.Include)]
        public string BuildVersion { get; set; }
    }

    /// <summary>
    /// The <c>players</c> capacity of a member deployment and its source (<c>agentConfig</c>,
    /// <c>deployment</c> or <c>none</c>); capacity null means no <c>players</c> counter at all.
    /// </summary>
    [Preserve]
    public sealed class PlayersCapacityView
    {
        [JsonProperty("capacity", NullValueHandling = NullValueHandling.Include)]
        public int? Capacity { get; set; }

        [JsonProperty("source", NullValueHandling = NullValueHandling.Ignore)]
        public string Source { get; set; }
    }
}
