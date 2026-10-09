using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Credentials;

namespace PingCore.Editor.Workspace.Tests.Fakes
{
    /// <summary>
    /// A scripted <see cref="IPingCoreApi"/>. Every call is recorded in <see cref="Calls"/> as
    /// <c>METHOD path</c> (the route template filled with the ids, for example
    /// <c>POST fleets/1/releases</c>) and answered by its handler property; an unset handler
    /// answers <see cref="PluginErrorKind.NotFound"/> with "not scripted", so a test fails
    /// loudly on a call it did not expect. The push token issue writes the scripted secret into the
    /// store it is given, exactly as the real client does, and answers a
    /// <see cref="SecretReceipt"/>. Values usually come from the pinned fixtures
    /// (<see cref="WorkspaceFixtures.Payload{T}"/>).
    /// </summary>
    public sealed class FakePingCoreApi : IPingCoreApi
    {
        /// <param name="host">The workspace host the fake claims to talk to.</param>
        public FakePingCoreApi(string host = "studio.app.pingcore.io")
        {
            Endpoint = WorkspaceEndpoint.ForHost(host);
        }

        public WorkspaceEndpoint Endpoint { get; }

        /// <summary>Every call in order, for example <c>GET fleets/1/live</c>.</summary>
        public List<string> Calls { get; } = new List<string>();

        /// <summary>The bodies of the write calls, in order (request DTOs).</summary>
        public List<object> Bodies { get; } = new List<object>();

        public Func<ApiResult<FleetListResponse>> ListFleets { get; set; }

        public Func<long, ApiResult<FleetDetailResponse>> GetFleet { get; set; }

        public Func<long, ApiResult<FleetLiveResponse>> GetFleetLive { get; set; }

        public Func<long, ApiResult<BuildTargetsResponse>> ListBuildTargets { get; set; }

        public Func<long, ReleaseCreateRequest, ApiResult<ReleaseCreatedResponse>> CreateRelease { get; set; }

        public Func<long, long, ApiResult<ReleaseDetailResponse>> GetRelease { get; set; }

        public Func<long, long, ApiResult<ReleaseChangedResponse>> CancelRelease { get; set; }

        public Func<long, long, ApiResult<ReleaseChangedResponse>> AcknowledgeRelease { get; set; }

        /// <summary>The push token <see cref="IssuePushTokenAsync"/> stores, or a failure to answer instead.</summary>
        public Func<long, (string Token, string Created, PluginError Error)> IssuePushToken { get; set; }

        public Func<ApiResult<DiscoveryAppListResponse>> ListDiscoveryApps { get; set; }

        public Func<long, ApiResult<DiscoveryAppDetailResponse>> GetDiscoveryApp { get; set; }

        public Func<ApiResult<CapabilitiesResponse>> GetCapabilities { get; set; }

        public Func<long, ApiResult<GameBranchesResponse>> GetGameBranches { get; set; }

        /// <summary>Answers a pasted push token's check; it receives the token so a test can assert what was sent.</summary>
        public Func<string, ApiResult<PushInfoResponse>> CheckPushToken { get; set; }

        /// <summary>The push tokens <see cref="CheckPushTokenAsync"/> was given, in order (never printed by a test).</summary>
        public List<string> CheckedTokens { get; } = new List<string>();

        public Func<long, ApiResult<DeploymentSpecListResponse>> ListDeploymentSpecs { get; set; }

        public Func<long, long, ApiResult<TemplateSetResponse>> GetTemplateSet { get; set; }

        public Func<long, ApiResult<DeploymentReadResponse>> GetDeployment { get; set; }

        public Task<ApiResult<FleetListResponse>> ListFleetsAsync(CancellationToken cancellationToken)
            => Answer(WorkspaceRouteId.ListFleets, () => ListFleets?.Invoke());

        public Task<ApiResult<FleetDetailResponse>> GetFleetAsync(long fleetId, CancellationToken cancellationToken)
            => Answer(WorkspaceRouteId.GetFleet, () => GetFleet?.Invoke(fleetId), fleetId);

        public Task<ApiResult<FleetLiveResponse>> GetFleetLiveAsync(long fleetId, CancellationToken cancellationToken)
            => Answer(WorkspaceRouteId.GetFleetLive, () => GetFleetLive?.Invoke(fleetId), fleetId);

        public Task<ApiResult<BuildTargetsResponse>> ListBuildTargetsAsync(long fleetId, CancellationToken cancellationToken)
            => Answer(WorkspaceRouteId.ListBuildTargets, () => ListBuildTargets?.Invoke(fleetId), fleetId);

        public Task<ApiResult<ReleaseCreatedResponse>> CreateReleaseAsync(long fleetId, ReleaseCreateRequest request, CancellationToken cancellationToken)
        {
            Bodies.Add(request);
            return Answer(WorkspaceRouteId.CreateRelease, () => CreateRelease?.Invoke(fleetId, request), fleetId);
        }

        public Task<ApiResult<ReleaseDetailResponse>> GetReleaseAsync(long fleetId, long releaseId, CancellationToken cancellationToken)
            => Answer(WorkspaceRouteId.GetRelease, () => GetRelease?.Invoke(fleetId, releaseId), fleetId, releaseId);

        public Task<ApiResult<ReleaseChangedResponse>> CancelReleaseAsync(long fleetId, long releaseId, CancellationToken cancellationToken)
            => Answer(WorkspaceRouteId.CancelRelease, () => CancelRelease?.Invoke(fleetId, releaseId), fleetId, releaseId);

        public Task<ApiResult<ReleaseChangedResponse>> AcknowledgeReleaseAsync(long fleetId, long releaseId, CancellationToken cancellationToken)
            => Answer(WorkspaceRouteId.AcknowledgeRelease, () => AcknowledgeRelease?.Invoke(fleetId, releaseId), fleetId, releaseId);

        public Task<ApiResult<SecretReceipt>> IssuePushTokenAsync(long sourceId, ICredentialStore store, CancellationToken cancellationToken)
        {
            return Answer(WorkspaceRouteId.IssuePushToken, () =>
            {
                if (IssuePushToken == null)
                {
                    return null;
                }

                (string token, string created, PluginError error) = IssuePushToken(sourceId);
                if (error != null)
                {
                    return ApiResult<SecretReceipt>.Failure(error);
                }

                string target = CredentialTargets.PushToken(Endpoint.Host, sourceId);
                store.Write(target, token);
                return ApiResult<SecretReceipt>.Success(new SecretReceipt(target, store.Kind, created));
            }, sourceId);
        }

        public Task<ApiResult<DiscoveryAppListResponse>> ListDiscoveryAppsAsync(CancellationToken cancellationToken)
            => Answer(WorkspaceRouteId.ListDiscoveryApps, () => ListDiscoveryApps?.Invoke());

        public Task<ApiResult<DiscoveryAppDetailResponse>> GetDiscoveryAppAsync(long discoveryAppId, CancellationToken cancellationToken)
            => Answer(WorkspaceRouteId.GetDiscoveryApp, () => GetDiscoveryApp?.Invoke(discoveryAppId), discoveryAppId);

        public Task<ApiResult<CapabilitiesResponse>> GetCapabilitiesAsync(CancellationToken cancellationToken)
            => Answer(WorkspaceRouteId.GetCapabilities, () => GetCapabilities?.Invoke());

        public Task<ApiResult<GameBranchesResponse>> GetGameBranchesAsync(long gameId, CancellationToken cancellationToken)
            => Answer(WorkspaceRouteId.GetGame, () => GetGameBranches?.Invoke(gameId), gameId);

        public Task<ApiResult<PushInfoResponse>> CheckPushTokenAsync(string pushToken, CancellationToken cancellationToken)
        {
            CheckedTokens.Add(pushToken);
            return Answer(WorkspaceRouteId.CheckPushToken, () => CheckPushToken?.Invoke(pushToken));
        }

        public Task<ApiResult<DeploymentSpecListResponse>> ListDeploymentSpecsAsync(long gameId, CancellationToken cancellationToken)
            => Answer(WorkspaceRouteId.ListDeploymentSpecs, () => ListDeploymentSpecs?.Invoke(gameId), gameId);

        public Task<ApiResult<TemplateSetResponse>> GetTemplateSetAsync(long gameId, long templateSetId, CancellationToken cancellationToken)
            => Answer(WorkspaceRouteId.GetTemplateSet, () => GetTemplateSet?.Invoke(gameId, templateSetId), gameId, templateSetId);

        public Task<ApiResult<DeploymentReadResponse>> GetDeploymentAsync(long deploymentId, CancellationToken cancellationToken)
            => Answer(WorkspaceRouteId.GetDeployment, () => GetDeployment?.Invoke(deploymentId), deploymentId);

        /// <summary>A failure as the real client would report it for <paramref name="id"/>.</summary>
        public static PluginError Failure(WorkspaceRouteId id, PluginErrorKind kind, string message, int status = 0)
        {
            WorkspaceRoute route = WorkspaceRoutes.Get(id);
            return new PluginError(route.Step, kind, message, null)
            {
                HttpStatus = status,
                Permission = kind == PluginErrorKind.MissingBrandPermission ? route.Permission : null,
            };
        }

        private Task<ApiResult<T>> Answer<T>(WorkspaceRouteId id, Func<ApiResult<T>> handler, params long[] ids)
            where T : class
        {
            WorkspaceRoute route = WorkspaceRoutes.Get(id);
            Calls.Add(route.Method + " " + route.PathFor(ids));
            ApiResult<T> result = handler();
            return Task.FromResult(result ?? ApiResult<T>.Failure(Failure(id, PluginErrorKind.NotFound, $"not scripted: {route.Method} {route.Template}", 404)));
        }
    }
}
