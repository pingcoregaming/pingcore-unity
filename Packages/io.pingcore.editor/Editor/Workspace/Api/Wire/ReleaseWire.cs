using System.Collections.Generic;
using Newtonsoft.Json;
using PingCore.Core;

namespace PingCore.Editor.Workspace.Api.Wire
{
    /// <summary>
    /// <c>POST /fleets/{id}/releases</c> body. The plugin sends <c>targetBuildVersion</c> (from the
    /// build targets list) and leaves every other field to the handler's defaults unless set.
    /// </summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "POST", "/fleets/{id}/releases", WireDirection.Request, 0)]
    public sealed class ReleaseCreateRequest
    {
        [JsonProperty("targetBuildVersion", NullValueHandling = NullValueHandling.Ignore)]
        public string TargetBuildVersion { get; set; }

        /// <summary><c>additive</c> (default) or <c>clean</c>.</summary>
        [JsonProperty("installMode", NullValueHandling = NullValueHandling.Ignore)]
        public string InstallMode { get; set; }

        [JsonProperty("preservePaths", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> PreservePaths { get; set; }

        [JsonProperty("maxSurge", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxSurge { get; set; }

        [JsonProperty("maxUnavailable", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxUnavailable { get; set; }

        [JsonProperty("gracePeriodSeconds", NullValueHandling = NullValueHandling.Ignore)]
        public int? GracePeriodSeconds { get; set; }
    }

    /// <summary><c>POST /fleets/{id}/releases</c> answer.</summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "POST", "/fleets/{id}/releases", WireDirection.Response, 200)]
    public sealed class ReleaseCreatedResponse
    {
        [JsonProperty("release", NullValueHandling = NullValueHandling.Ignore)]
        public ReleaseView Release { get; set; }

        /// <summary>How many game servers a clean install reinstalls; 0 for an additive release.</summary>
        [JsonProperty("cleanServerCount", NullValueHandling = NullValueHandling.Ignore)]
        public int CleanServerCount { get; set; }
    }

    /// <summary><c>GET /fleets/{fleetId}/releases/{releaseId}</c>: the release and its per-location progress.</summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "GET", "/fleets/{fleetId}/releases/{releaseId}", WireDirection.Response, 200)]
    public sealed class ReleaseDetailResponse
    {
        [JsonProperty("release", NullValueHandling = NullValueHandling.Ignore)]
        public ReleaseStatusView Release { get; set; }

        [JsonProperty("deployments", NullValueHandling = NullValueHandling.Ignore)]
        public List<ReleaseDeploymentView> Deployments { get; set; }
    }

    /// <summary>The answer of cancel and acknowledge: the release after the change.</summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "POST", "/fleets/{fleetId}/releases/{releaseId}/cancel", WireDirection.Response, 200)]
    [WireContract(PingCoreApiContract.Source, "POST", "/fleets/{fleetId}/releases/{releaseId}/acknowledge", WireDirection.Response, 200)]
    public sealed class ReleaseChangedResponse
    {
        [JsonProperty("release", NullValueHandling = NullValueHandling.Ignore)]
        public ReleaseView Release { get; set; }
    }

    /// <summary>A release row.</summary>
    [Preserve]
    public class ReleaseView
    {
        [JsonProperty("releaseId", NullValueHandling = NullValueHandling.Ignore)]
        public long ReleaseId { get; set; }

        [JsonProperty("fleetId", NullValueHandling = NullValueHandling.Ignore)]
        public long FleetId { get; set; }

        /// <summary><c>pending</c>, <c>surging</c>, <c>rolling</c>, <c>completing</c>, <c>completed</c>, <c>failed</c>, <c>rolled_back</c> or <c>dismissed</c>.</summary>
        [JsonProperty("state", NullValueHandling = NullValueHandling.Ignore)]
        public string State { get; set; }

        [JsonProperty("installMode", NullValueHandling = NullValueHandling.Ignore)]
        public string InstallMode { get; set; }

        [JsonProperty("preservePaths", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> PreservePaths { get; set; }

        [JsonProperty("dataSourceType", NullValueHandling = NullValueHandling.Ignore)]
        public string DataSourceType { get; set; }

        [JsonProperty("targetBuildVersion", NullValueHandling = NullValueHandling.Ignore)]
        public string TargetBuildVersion { get; set; }

        [JsonProperty("previousBuildVersion", NullValueHandling = NullValueHandling.Include)]
        public string PreviousBuildVersion { get; set; }

        [JsonProperty("digestVerified", NullValueHandling = NullValueHandling.Ignore)]
        public bool DigestVerified { get; set; }

        [JsonProperty("maxSurge", NullValueHandling = NullValueHandling.Ignore)]
        public int MaxSurge { get; set; }

        [JsonProperty("maxUnavailable", NullValueHandling = NullValueHandling.Ignore)]
        public int MaxUnavailable { get; set; }

        [JsonProperty("gracePeriodSeconds", NullValueHandling = NullValueHandling.Ignore)]
        public int GracePeriodSeconds { get; set; }

        [JsonProperty("windowStartUtc", NullValueHandling = NullValueHandling.Include)]
        public string WindowStartUtc { get; set; }

        [JsonProperty("windowEndUtc", NullValueHandling = NullValueHandling.Include)]
        public string WindowEndUtc { get; set; }

        [JsonProperty("failedReason", NullValueHandling = NullValueHandling.Include)]
        public string FailedReason { get; set; }

        [JsonProperty("failedMessage", NullValueHandling = NullValueHandling.Include)]
        public string FailedMessage { get; set; }

        [JsonProperty("failedServerId", NullValueHandling = NullValueHandling.Include)]
        public long? FailedServerId { get; set; }

        [JsonProperty("acknowledgedAt", NullValueHandling = NullValueHandling.Include)]
        public string AcknowledgedAt { get; set; }

        [JsonProperty("createdBy", NullValueHandling = NullValueHandling.Ignore)]
        public long CreatedBy { get; set; }

        [JsonProperty("createdAt", NullValueHandling = NullValueHandling.Include)]
        public string CreatedAt { get; set; }

        [JsonProperty("updatedAt", NullValueHandling = NullValueHandling.Include)]
        public string UpdatedAt { get; set; }

        [JsonProperty("completedAt", NullValueHandling = NullValueHandling.Include)]
        public string CompletedAt { get; set; }
    }

    /// <summary>The release row of the GET answer, which adds what a pending release waits for.</summary>
    [Preserve]
    public sealed class ReleaseStatusView : ReleaseView
    {
        /// <summary>Why a pending release has not started, or null.</summary>
        [JsonProperty("blocked", NullValueHandling = NullValueHandling.Include, Order = 1)]
        public string Blocked { get; set; }
    }

    /// <summary>Per-location release progress.</summary>
    [Preserve]
    public sealed class ReleaseDeploymentView
    {
        [JsonProperty("brandDeploymentId", NullValueHandling = NullValueHandling.Ignore)]
        public long BrandDeploymentId { get; set; }

        [JsonProperty("phase", NullValueHandling = NullValueHandling.Ignore)]
        public string Phase { get; set; }

        [JsonProperty("oldCount", NullValueHandling = NullValueHandling.Ignore)]
        public int OldCount { get; set; }

        [JsonProperty("newCount", NullValueHandling = NullValueHandling.Ignore)]
        public int NewCount { get; set; }

        [JsonProperty("retiringCount", NullValueHandling = NullValueHandling.Ignore)]
        public int RetiringCount { get; set; }

        [JsonProperty("surgeServerIds", NullValueHandling = NullValueHandling.Ignore)]
        public List<long> SurgeServerIds { get; set; }

        /// <summary>Why this location is not moving, in the planner's words, or null.</summary>
        [JsonProperty("blocked", NullValueHandling = NullValueHandling.Include)]
        public string Blocked { get; set; }

        [JsonProperty("parked", NullValueHandling = NullValueHandling.Ignore)]
        public bool Parked { get; set; }
    }

    /// <summary><c>GET /fleets/{id}/build-targets</c>: what the fleet runs and what it can be released onto.</summary>
    [Preserve]
    [WireContract(PingCoreApiContract.Source, "GET", "/fleets/{id}/build-targets", WireDirection.Response, 200)]
    public sealed class BuildTargetsResponse
    {
        /// <summary><c>image</c>, <c>cdn_source</c> or <c>steam_depot</c>.</summary>
        [JsonProperty("dataSourceType", NullValueHandling = NullValueHandling.Ignore)]
        public string DataSourceType { get; set; }

        /// <summary>What the fleet's game servers report running, deduplicated (evidence).</summary>
        [JsonProperty("currentBuildVersions", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> CurrentBuildVersions { get; set; }

        /// <summary>What the first member deployment is pinned to (intent), or null.</summary>
        [JsonProperty("pinnedBuildVersion", NullValueHandling = NullValueHandling.Include)]
        public string PinnedBuildVersion { get; set; }

        /// <summary>
        /// The CDN source every member deployment delivers from, so the plugin issues the push token for the right
        /// source instead of guessing; null for a fleet that is not on <c>cdn_source</c> and for one whose members
        /// disagree. An id, not a credential.
        /// </summary>
        [JsonProperty("cdnSourceId", NullValueHandling = NullValueHandling.Include)]
        public long? CdnSourceId { get; set; }

        /// <summary>True when the answer carried <c>cdnSourceId</c> (even as null); false from a workspace older than the field.</summary>
        [JsonIgnore]
        public bool CdnSourceIdSpecified { get; set; }

        /// <summary>CDN branches only: what the content server calls current. Absent for other data sources.</summary>
        [JsonProperty("cdnCurrentVersion", NullValueHandling = NullValueHandling.Include)]
        public string CdnCurrentVersion { get; set; }

        /// <summary>True when the answer carried <c>cdnCurrentVersion</c> (even as null).</summary>
        [JsonIgnore]
        public bool CdnCurrentVersionSpecified { get; set; }

        /// <summary>Image branches only.</summary>
        [JsonProperty("repository", NullValueHandling = NullValueHandling.Include)]
        public string Repository { get; set; }

        [JsonProperty("targets", NullValueHandling = NullValueHandling.Ignore)]
        public List<BuildTargetView> Targets { get; set; }

        [JsonProperty("targetsNote", NullValueHandling = NullValueHandling.Include)]
        public string TargetsNote { get; set; }

        [JsonProperty("depotIds", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> DepotIds { get; set; }
    }

    /// <summary>One build a fleet can be released onto.</summary>
    [Preserve]
    public sealed class BuildTargetView
    {
        /// <summary>Pass this back as <c>targetBuildVersion</c>.</summary>
        [JsonProperty("buildVersion", NullValueHandling = NullValueHandling.Ignore)]
        public string BuildVersion { get; set; }

        [JsonProperty("label", NullValueHandling = NullValueHandling.Ignore)]
        public string Label { get; set; }

        [JsonProperty("pushedAt", NullValueHandling = NullValueHandling.Include)]
        public string PushedAt { get; set; }
    }
}
