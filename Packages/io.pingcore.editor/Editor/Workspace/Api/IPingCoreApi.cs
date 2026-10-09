using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Credentials;

namespace PingCore.Editor.Workspace.Api
{
    /// <summary>
    /// Every PingCore API call the Editor plugin makes, with the brand member's <c>usr_</c> key.
    /// Each answers an <see cref="ApiResult{T}"/> and never throws for an HTTP or transport
    /// failure; continuations return to the caller's synchronization context (the Unity main
    /// thread). The routes, their brand permissions and steps are <see cref="WorkspaceRoutes"/>.
    /// The plugin connects, ships and reports: it creates nothing in the workspace except the CDN
    /// push token it needs, and that machine secret never comes back to the caller: the
    /// implementation writes it straight into the credential store it was given and answers a
    /// <see cref="SecretReceipt"/>.
    /// </summary>
    public interface IPingCoreApi
    {
        /// <summary>The workspace this client talks to.</summary>
        WorkspaceEndpoint Endpoint { get; }

        /// <summary><c>GET fleets</c> (<c>fleets.view</c>): sign-in verification and the fleet picker.</summary>
        Task<ApiResult<FleetListResponse>> ListFleetsAsync(CancellationToken cancellationToken);

        /// <summary><c>GET fleets/{id}</c> (<c>fleets.view</c>): the fleet's game, its Discovery app and its member deployments.</summary>
        Task<ApiResult<FleetDetailResponse>> GetFleetAsync(long fleetId, CancellationToken cancellationToken);

        /// <summary><c>GET fleets/{id}/live</c> (<c>fleets.view</c>).</summary>
        Task<ApiResult<FleetLiveResponse>> GetFleetLiveAsync(long fleetId, CancellationToken cancellationToken);

        /// <summary><c>GET fleets/{id}/build-targets</c> (<c>fleets.view</c>): the data source, the CDN source and the builds a release can target.</summary>
        Task<ApiResult<BuildTargetsResponse>> ListBuildTargetsAsync(long fleetId, CancellationToken cancellationToken);

        /// <summary><c>POST fleets/{id}/releases</c> (<c>fleets.manage</c>). Never retried automatically.</summary>
        Task<ApiResult<ReleaseCreatedResponse>> CreateReleaseAsync(long fleetId, ReleaseCreateRequest request, CancellationToken cancellationToken);

        /// <summary><c>GET fleets/{fleetId}/releases/{releaseId}</c> (<c>fleets.view</c>).</summary>
        Task<ApiResult<ReleaseDetailResponse>> GetReleaseAsync(long fleetId, long releaseId, CancellationToken cancellationToken);

        /// <summary><c>POST .../cancel</c> (<c>fleets.manage</c>).</summary>
        Task<ApiResult<ReleaseChangedResponse>> CancelReleaseAsync(long fleetId, long releaseId, CancellationToken cancellationToken);

        /// <summary><c>POST .../acknowledge</c> (<c>fleets.manage</c>): dismisses a failed release so it no longer holds scale-down.</summary>
        Task<ApiResult<ReleaseChangedResponse>> AcknowledgeReleaseAsync(long fleetId, long releaseId, CancellationToken cancellationToken);

        /// <summary>
        /// <c>POST cdn-sources/sources/{id}/push-token</c> (<c>cdn-sources.edit</c>). Issuing kills
        /// the previous token at once, for every holder. The new token goes straight into
        /// <paramref name="store"/> under <see cref="CredentialTargets.PushToken"/>; the answer
        /// names where, never the value.
        /// </summary>
        Task<ApiResult<SecretReceipt>> IssuePushTokenAsync(long sourceId, ICredentialStore store, CancellationToken cancellationToken);

        /// <summary><c>GET discovery/apps</c> (<c>discovery.view</c>): the community app choices and Confirm for build.</summary>
        Task<ApiResult<DiscoveryAppListResponse>> ListDiscoveryAppsAsync(CancellationToken cancellationToken);

        /// <summary><c>GET discovery/apps/{id}</c> (<c>discovery.view</c>): the app, its masked tokens and signing keys.</summary>
        Task<ApiResult<DiscoveryAppDetailResponse>> GetDiscoveryAppAsync(long discoveryAppId, CancellationToken cancellationToken);

        /// <summary><c>GET me/capabilities</c> (login only): the workspace's name, the brand member's email and the panel's address.</summary>
        Task<ApiResult<CapabilitiesResponse>> GetCapabilitiesAsync(CancellationToken cancellationToken);

        /// <summary>
        /// <c>GET my-games/{id}</c> (<c>my-games.view</c>): the game's branches, for Push (each branch's data source, CDN
        /// source and platform), and its template sets' ids and names, for the startup command check of a fleet with no
        /// deployment. Key-only: the answer carries fields the plugin has no use for, some of them sensitive; the
        /// implementation keeps only the modelled keys and drops the rest unread (<see cref="GameBranchesResponse"/>).
        /// </summary>
        Task<ApiResult<GameBranchesResponse>> GetGameBranchesAsync(long gameId, CancellationToken cancellationToken);

        /// <summary>
        /// <c>GET cdn-sources/push/info</c> sent with <paramref name="pushToken"/> as the bearer (never the brand member's
        /// key, never in the URL): which CDN source a pasted push token belongs to. Key-only: only the source's id and
        /// name are kept, the rest is dropped unread (<see cref="PushInfoResponse"/>). The token is never put into the result or an
        /// error.
        /// </summary>
        Task<ApiResult<PushInfoResponse>> CheckPushTokenAsync(string pushToken, CancellationToken cancellationToken);

        /// <summary><c>GET my-games/{id}/kubernetes/deployment-specs</c> (<c>my-games.view</c>): each spec's template set, for the startup command read.</summary>
        Task<ApiResult<DeploymentSpecListResponse>> ListDeploymentSpecsAsync(long gameId, CancellationToken cancellationToken);

        /// <summary><c>GET my-games/{gameId}/template-sets/{templateSetId}</c> (<c>my-games.templates</c>): the command-line config's process name, the game's startup command.</summary>
        Task<ApiResult<TemplateSetResponse>> GetTemplateSetAsync(long gameId, long templateSetId, CancellationToken cancellationToken);

        /// <summary>
        /// <c>GET brand/servers/deployments/{id}</c> (<c>brand.servers.view</c>): a member deployment's spec, for the startup
        /// command check of a fleet with deployments. Key-only: the answer carries fields the plugin has no use for, some of
        /// them sensitive; only the modelled keys are kept and the rest is dropped unread (<see cref="DeploymentReadResponse"/>).
        /// </summary>
        Task<ApiResult<DeploymentReadResponse>> GetDeploymentAsync(long deploymentId, CancellationToken cancellationToken);
    }

    /// <summary>Where a shown-once machine secret was stored. Never the secret.</summary>
    public sealed class SecretReceipt
    {
        /// <param name="target">The credential target it was written under.</param>
        /// <param name="storeKind">The store that holds it.</param>
        /// <param name="created">When the platform says it was created, or null.</param>
        public SecretReceipt(string target, CredentialStoreKind storeKind, string created)
        {
            Target = target;
            StoreKind = storeKind;
            Created = created;
        }

        /// <summary>The credential target.</summary>
        public string Target { get; }

        /// <summary>The store that holds the secret.</summary>
        public CredentialStoreKind StoreKind { get; }

        /// <summary>When the platform created it, or null.</summary>
        public string Created { get; }
    }
}
