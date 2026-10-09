using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Editor.Workspace.Api.Wire
{
    /// <summary>
    /// <c>GET /brand/servers/deployments/{id}</c>, modelled key-only: the deployment's id, its name and its deployment
    /// spec, which names the template set holding the startup command the Push check reads for a fleet with deployments.
    /// The answer carries fields the plugin has no use for, some of them sensitive, so the client keeps these keys and
    /// drops the rest unread (<see cref="PingCoreApiClient.GetDeploymentAsync"/>).
    /// </summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "GET", "/brand/servers/deployments/{id}", WireDirection.Response, 200)]
    public sealed class DeploymentReadResponse
    {
        [JsonProperty("deployment", NullValueHandling = NullValueHandling.Ignore)]
        public DeploymentReadView Deployment { get; set; }
    }

    /// <summary>A deployment, key-only (the rest of the answer is dropped unread).</summary>
    [Preserve]
    public sealed class DeploymentReadView
    {
        [JsonProperty("brandDeploymentId", NullValueHandling = NullValueHandling.Ignore)]
        public long BrandDeploymentId { get; set; }

        /// <summary>The deployment's spec (whose template set holds the startup command), or 0.</summary>
        [JsonProperty("deploymentSpecId", NullValueHandling = NullValueHandling.Ignore)]
        public long DeploymentSpecId { get; set; }

        [JsonProperty("friendlyName", NullValueHandling = NullValueHandling.Include)]
        public string FriendlyName { get; set; }
    }
}
