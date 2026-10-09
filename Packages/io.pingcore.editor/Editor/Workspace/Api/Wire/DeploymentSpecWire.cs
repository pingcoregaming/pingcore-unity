using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;

namespace PingCore.Editor.Workspace.Api.Wire
{
    /// <summary><c>GET /my-games/{id}/kubernetes/deployment-specs</c>: the game's specs. The Push check maps each member deployment's spec to its template set to read the startup command.</summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "GET", "/my-games/{id}/kubernetes/deployment-specs", WireDirection.Response, 200)]
    public sealed class DeploymentSpecListResponse
    {
        [JsonProperty("specs", NullValueHandling = NullValueHandling.Ignore)]
        public List<DeploymentSpecView> Specs { get; set; }

        [JsonProperty("count", NullValueHandling = NullValueHandling.Ignore)]
        public int Count { get; set; }
    }

    /// <summary>A deployment spec of the game.</summary>
    [Preserve]
    public sealed class DeploymentSpecView
    {
        [JsonProperty("specId", NullValueHandling = NullValueHandling.Ignore)]
        public long SpecId { get; set; }

        [JsonProperty("gameId", NullValueHandling = NullValueHandling.Ignore)]
        public long GameId { get; set; }

        [JsonProperty("gameBranchId", NullValueHandling = NullValueHandling.Include)]
        public long? GameBranchId { get; set; }

        [JsonProperty("templateSetId", NullValueHandling = NullValueHandling.Include)]
        public long? TemplateSetId { get; set; }

        [JsonProperty("branchName", NullValueHandling = NullValueHandling.Include)]
        public string BranchName { get; set; }

        [JsonProperty("templateSetName", NullValueHandling = NullValueHandling.Include)]
        public string TemplateSetName { get; set; }

        [JsonProperty("specName", NullValueHandling = NullValueHandling.Ignore)]
        public string SpecName { get; set; }

        [JsonProperty("resourceType", NullValueHandling = NullValueHandling.Ignore)]
        public string ResourceType { get; set; }

        [JsonProperty("strategyType", NullValueHandling = NullValueHandling.Ignore)]
        public string StrategyType { get; set; }

        [JsonProperty("replicasDefault", NullValueHandling = NullValueHandling.Ignore)]
        public int ReplicasDefault { get; set; }

        [JsonProperty("terminationGracePeriodSeconds", NullValueHandling = NullValueHandling.Ignore)]
        public int TerminationGracePeriodSeconds { get; set; }

        /// <summary>The decoded selector: an object, or <c>[]</c> when empty.</summary>
        [JsonProperty("nodeSelector", NullValueHandling = NullValueHandling.Include)]
        public JToken NodeSelector { get; set; }

        [JsonProperty("labels", NullValueHandling = NullValueHandling.Include)]
        public JToken Labels { get; set; }

        [JsonProperty("annotations", NullValueHandling = NullValueHandling.Include)]
        public JToken Annotations { get; set; }

        /// <summary>Kubernetes secret names (comma separated), never key material.</summary>
        [JsonProperty("imagePullSecrets", NullValueHandling = NullValueHandling.Include)]
        public string ImagePullSecrets { get; set; }

        [JsonProperty("serviceAccountName", NullValueHandling = NullValueHandling.Include)]
        public string ServiceAccountName { get; set; }

        [JsonProperty("description", NullValueHandling = NullValueHandling.Include)]
        public string Description { get; set; }

        [JsonProperty("sortOrder", NullValueHandling = NullValueHandling.Ignore)]
        public int SortOrder { get; set; }

        [JsonProperty("active", NullValueHandling = NullValueHandling.Ignore)]
        public int Active { get; set; }

        [JsonProperty("created", NullValueHandling = NullValueHandling.Include)]
        public string Created { get; set; }

        [JsonProperty("updated", NullValueHandling = NullValueHandling.Include)]
        public string Updated { get; set; }

        [JsonProperty("removed", NullValueHandling = NullValueHandling.Ignore)]
        public int Removed { get; set; }

        [JsonProperty("serverCount", NullValueHandling = NullValueHandling.Ignore)]
        public int ServerCount { get; set; }
    }
}
