using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Credentials;
using PingCore.Editor.Workspace.Redaction;

namespace PingCore.Editor.Workspace.Api
{
    /// <summary>
    /// The PingCore API client. Every call reads the brand member's key from the credential
    /// store at the moment it sends (the key is never cached on this object), sends
    /// <c>Authorization: Bearer</c> to <c>https://&lt;host&gt;/api/&lt;route&gt;</c> through the
    /// transport, and classifies the answer with the envelope rule (<see cref="ApiEnvelope"/>).
    /// It never throws for an HTTP or transport failure, never logs, and never puts a body, a
    /// header or a URL with ids into an error: a transport failure names its exception type and
    /// the route template only.
    /// </summary>
    public sealed class PingCoreApiClient : IPingCoreApi
    {
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(PingCoreJson.Settings);

        private readonly IHttpTransport transport;
        private readonly ICredentialStore keyStore;
        private readonly Func<DateTimeOffset> clock;

        /// <param name="endpoint">The workspace.</param>
        /// <param name="keyStore">Where the <c>usr_</c> key lives (<see cref="CredentialTargets.UserKey"/>).</param>
        /// <param name="transport">The HTTP seam; <see cref="EditorHttpTransport"/> in the Editor, a fake in tests.</param>
        /// <param name="clock">For <c>Retry-After</c> dates; <see cref="DateTimeOffset.UtcNow"/> when null.</param>
        public PingCoreApiClient(WorkspaceEndpoint endpoint, ICredentialStore keyStore, IHttpTransport transport, Func<DateTimeOffset> clock = null)
        {
            Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            this.keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        /// <inheritdoc />
        public WorkspaceEndpoint Endpoint { get; }

        public Task<ApiResult<FleetListResponse>> ListFleetsAsync(CancellationToken cancellationToken)
            => SendAsync<FleetListResponse>(WorkspaceRouteId.ListFleets, null, cancellationToken);

        public Task<ApiResult<FleetDetailResponse>> GetFleetAsync(long fleetId, CancellationToken cancellationToken)
            => SendAsync<FleetDetailResponse>(WorkspaceRouteId.GetFleet, null, cancellationToken, fleetId);

        public Task<ApiResult<FleetLiveResponse>> GetFleetLiveAsync(long fleetId, CancellationToken cancellationToken)
            => SendAsync<FleetLiveResponse>(WorkspaceRouteId.GetFleetLive, null, cancellationToken, fleetId);

        public Task<ApiResult<BuildTargetsResponse>> ListBuildTargetsAsync(long fleetId, CancellationToken cancellationToken)
            => SendAsync<BuildTargetsResponse>(WorkspaceRouteId.ListBuildTargets, null, cancellationToken, fleetId);

        public Task<ApiResult<ReleaseCreatedResponse>> CreateReleaseAsync(long fleetId, ReleaseCreateRequest request, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.TargetBuildVersion))
            {
                return Task.FromResult(Refused<ReleaseCreatedResponse>(WorkspaceRouteId.CreateRelease, "A release needs a target build version."));
            }

            return SendAsync<ReleaseCreatedResponse>(WorkspaceRouteId.CreateRelease, request, cancellationToken, fleetId);
        }

        public Task<ApiResult<ReleaseDetailResponse>> GetReleaseAsync(long fleetId, long releaseId, CancellationToken cancellationToken)
            => SendAsync<ReleaseDetailResponse>(WorkspaceRouteId.GetRelease, null, cancellationToken, fleetId, releaseId);

        public Task<ApiResult<ReleaseChangedResponse>> CancelReleaseAsync(long fleetId, long releaseId, CancellationToken cancellationToken)
            => SendAsync<ReleaseChangedResponse>(WorkspaceRouteId.CancelRelease, EmptyBody, cancellationToken, fleetId, releaseId);

        public Task<ApiResult<ReleaseChangedResponse>> AcknowledgeReleaseAsync(long fleetId, long releaseId, CancellationToken cancellationToken)
            => SendAsync<ReleaseChangedResponse>(WorkspaceRouteId.AcknowledgeRelease, EmptyBody, cancellationToken, fleetId, releaseId);

        public async Task<ApiResult<SecretReceipt>> IssuePushTokenAsync(long sourceId, ICredentialStore store, CancellationToken cancellationToken)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            ApiResult<PushTokenIssueResponse> issued = await SendAsync<PushTokenIssueResponse>(WorkspaceRouteId.IssuePushToken, EmptyBody, cancellationToken, sourceId);
            if (!issued.Ok)
            {
                return ApiResult<SecretReceipt>.Failure(issued.Error);
            }

            PushTokenIssueResponse answer = issued.Value;
            string token = answer.Token;
            answer.Token = null;
            if (string.IsNullOrEmpty(token) || !token.StartsWith("cdnpush_", StringComparison.Ordinal))
            {
                return Failure<SecretReceipt>(WorkspaceRouteId.IssuePushToken, PluginErrorKind.Envelope, "The workspace answered without a cdnpush_ token.", issued.HttpStatus);
            }

            string target = CredentialTargets.PushToken(Endpoint.Host, sourceId);
            ApiResult<SecretReceipt> stored = Store(WorkspaceRouteId.IssuePushToken, store, target, token, null, issued.HttpStatus, "The push token was issued (the previous one no longer works) but could not be stored; issue a new one.");
            return stored ?? ApiResult<SecretReceipt>.Success(new SecretReceipt(target, store.Kind, answer.Created), issued.Message, issued.HttpStatus);
        }

        public Task<ApiResult<DiscoveryAppListResponse>> ListDiscoveryAppsAsync(CancellationToken cancellationToken)
            => SendAsync<DiscoveryAppListResponse>(WorkspaceRouteId.ListDiscoveryApps, null, cancellationToken);

        public Task<ApiResult<DiscoveryAppDetailResponse>> GetDiscoveryAppAsync(long discoveryAppId, CancellationToken cancellationToken)
            => SendAsync<DiscoveryAppDetailResponse>(WorkspaceRouteId.GetDiscoveryApp, null, cancellationToken, discoveryAppId);

        public Task<ApiResult<CapabilitiesResponse>> GetCapabilitiesAsync(CancellationToken cancellationToken)
            => SendAsync<CapabilitiesResponse>(WorkspaceRouteId.GetCapabilities, null, cancellationToken);

        public async Task<ApiResult<GameBranchesResponse>> GetGameBranchesAsync(long gameId, CancellationToken cancellationToken)
        {
            Sent sent = await SendRawAsync(WorkspaceRouteId.GetGame, null, cancellationToken, gameId);

            // The answer carries fields the plugin has no use for, some of them sensitive; the client keeps only the
            // modelled keys and drops the rest unread.
            return KeyOnly<GameBranchesResponse>(WorkspaceRouteId.GetGame, sent);
        }

        public async Task<ApiResult<PushInfoResponse>> CheckPushTokenAsync(string pushToken, CancellationToken cancellationToken)
        {
            WorkspaceRoute route = WorkspaceRoutes.Get(WorkspaceRouteId.CheckPushToken);
            string token = pushToken?.Trim();
            if (!PushTokenShape.Matches(token))
            {
                return Refused<PushInfoResponse>(WorkspaceRouteId.CheckPushToken, PushTokenShape.NotATokenMessage);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return ApiResult<PushInfoResponse>.Failure(NotSent(Failure<PushInfoResponse>(WorkspaceRouteId.CheckPushToken, PluginErrorKind.Cancelled, "Cancelled.", 0).Error));
            }

            Sent sent = await SendWithBearerAsync(route, route.PathFor(), null, token, cancellationToken);
            token = null;
            if (sent.Error != null && (sent.Error.HttpStatus == 401 || sent.Error.HttpStatus == 403))
            {
                // The envelope maps 401 to "sign in again with a usr_ key"; here the bearer was the pasted push token.
                string said = string.IsNullOrEmpty(sent.Error.Message) ? string.Empty : " (" + sent.Error.Message + ")";
                return ApiResult<PushInfoResponse>.Failure(new PluginError(route.Step, PluginErrorKind.Rejected, PushTokenShape.NotAcceptedMessage + said, PushTokenShape.NotAcceptedHint) { HttpStatus = sent.Error.HttpStatus });
            }

            // The answer carries fields the plugin has no use for, some of them sensitive: only the source's id and name are kept.
            ApiResult<PushInfoResponse> info = KeyOnly<PushInfoResponse>(WorkspaceRouteId.CheckPushToken, sent);
            if (info.Ok && (info.Value.Source == null || info.Value.Source.SourceId <= 0))
            {
                return Failure<PushInfoResponse>(WorkspaceRouteId.CheckPushToken, PluginErrorKind.Envelope, "PingCore accepted the push token but did not say which CDN source it belongs to.", info.HttpStatus);
            }

            return info;
        }

        public Task<ApiResult<DeploymentSpecListResponse>> ListDeploymentSpecsAsync(long gameId, CancellationToken cancellationToken)
            => SendAsync<DeploymentSpecListResponse>(WorkspaceRouteId.ListDeploymentSpecs, null, cancellationToken, gameId);

        public Task<ApiResult<TemplateSetResponse>> GetTemplateSetAsync(long gameId, long templateSetId, CancellationToken cancellationToken)
            => SendAsync<TemplateSetResponse>(WorkspaceRouteId.GetTemplateSet, null, cancellationToken, gameId, templateSetId);

        public async Task<ApiResult<DeploymentReadResponse>> GetDeploymentAsync(long deploymentId, CancellationToken cancellationToken)
        {
            Sent sent = await SendRawAsync(WorkspaceRouteId.GetDeployment, null, cancellationToken, deploymentId);

            // The answer carries fields the plugin has no use for, some of them sensitive: only the id, name and spec are kept.
            return KeyOnly<DeploymentReadResponse>(WorkspaceRouteId.GetDeployment, sent);
        }

        /// <summary>An empty JSON object body for the POST routes that take none.</summary>
        private static readonly object EmptyBody = new JObject();

        private async Task<ApiResult<T>> SendAsync<T>(WorkspaceRouteId id, object body, CancellationToken cancellationToken, params long[] ids)
            where T : class
        {
            Sent sent = await SendRawAsync(id, body, cancellationToken, ids);
            if (sent.Error != null)
            {
                return ApiResult<T>.Failure(sent.Error);
            }

            T value;
            try
            {
                value = sent.Data.Type == JTokenType.Null ? null : sent.Data.ToObject<T>(Serializer);
            }
            catch (JsonException e)
            {
                return Failure<T>(id, PluginErrorKind.Envelope, $"The answer's data did not have the expected shape ({e.GetType().Name}).", sent.Status);
            }

            if (value == null)
            {
                return Failure<T>(id, PluginErrorKind.Envelope, "The answer carried no data.", sent.Status);
            }

            return ApiResult<T>.Success(value, sent.Message, sent.Status);
        }


        // A read whose answer carries more than the plugin may keep: the modelled keys are taken, everything else is never
        // materialised, and the raw data is dropped at once.
        private static ApiResult<T> KeyOnly<T>(WorkspaceRouteId id, Sent sent)
            where T : class
        {
            if (sent.Error != null)
            {
                return ApiResult<T>.Failure(sent.Error);
            }

            T value;
            try
            {
                value = sent.Data == null || sent.Data.Type == JTokenType.Null ? null : sent.Data.ToObject<T>(Serializer);
            }
            catch (JsonException e)
            {
                return Failure<T>(id, PluginErrorKind.Envelope, $"The answer's data did not have the expected shape ({e.GetType().Name}).", sent.Status);
            }
            finally
            {
                sent.Data = null;
            }

            return value == null
                ? Failure<T>(id, PluginErrorKind.Envelope, "The answer carried no data.", sent.Status)
                : ApiResult<T>.Success(value, sent.Message, sent.Status);
        }

        /// <summary>The classified answer of one call: its <c>data</c> and message, or its error.</summary>
        private sealed class Sent
        {
            public JToken Data;
            public string Message;
            public int Status;
            public PluginError Error;
        }

        private async Task<Sent> SendRawAsync(WorkspaceRouteId id, object body, CancellationToken cancellationToken, params long[] ids)
        {
            WorkspaceRoute route = WorkspaceRoutes.Get(id);
            if (route.Caller != WorkspaceRouteCaller.Plugin)
            {
                throw new InvalidOperationException($"{route.Template} is not sent with the brand member's key ({route.Caller}).");
            }

            string path;
            try
            {
                path = route.PathFor(ids);
            }
            catch (ArgumentException e)
            {
                return new Sent { Error = Refused<ApiAck>(id, e.Message).Error };
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new Sent { Error = NotSent(Failure<ApiAck>(id, PluginErrorKind.Cancelled, "Cancelled.", 0).Error) };
            }

            string key;
            try
            {
                key = keyStore.Read(CredentialTargets.UserKey(Endpoint.Host))?.Secret;
            }
            catch (CredentialStoreException e)
            {
                return new Sent { Error = NotSent(Failure<ApiAck>(id, PluginErrorKind.NotSignedIn, "The credential store could not be read: " + Redactor.PatternsOnly.Redact(e.Message), 0).Error) };
            }

            if (string.IsNullOrEmpty(key))
            {
                return new Sent { Error = new PluginError(route.Step, PluginErrorKind.NotSignedIn, "Not signed in.", "Sign in on " + PingCoreMenu.SignInText + ".") { NotSent = true } };
            }

            return await SendWithBearerAsync(route, path, body, key, cancellationToken);
        }

        // Sends one request with the bearer (the brand member's key, or for CheckPushToken only a pasted
        // push token) in the Authorization header and nowhere else, and classifies the answer. The bearer is a known
        // secret of the redactor, so no message carries it.
        private async Task<Sent> SendWithBearerAsync(WorkspaceRoute route, string path, object body, string bearer, CancellationToken cancellationToken)
        {
            WorkspaceRouteId id = route.Id;
            var redactor = new Redactor(new[] { bearer });
            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + bearer,
                ["Accept"] = "application/json",
            };
            string json = body == null ? null : JsonConvert.SerializeObject(body, PingCoreJson.Settings);
            var request = new PingCoreHttpRequest(route.Method, Endpoint.UrlFor(path), headers, json);
            bearer = null;

            PingCoreHttpResponse response;
            try
            {
                response = await transport.SendAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new Sent { Error = Failure<ApiAck>(id, PluginErrorKind.Cancelled, "Cancelled.", 0).Error };
            }
            catch (Exception e)
            {
                // The type and the route template only: an exception message can quote the URL.
                string detail = e is PingCoreTransportException ? redactor.Redact(e.Message) : e.GetType().Name;
                return new Sent { Error = new PluginError(route.Step, PluginErrorKind.Transport, $"{route.Method} {route.Template}: no answer from the workspace ({detail}).", "Check the network; the plugin calls " + Endpoint.ApiBase + " only.") };
            }

            ApiEnvelope.Outcome outcome = ApiEnvelope.Classify(response, route, redactor, clock());
            return outcome.Ok
                ? new Sent { Data = outcome.Data, Message = outcome.Message, Status = response.Status }
                : new Sent { Error = outcome.Error, Status = response.Status };
        }

        private static ApiResult<SecretReceipt> Store(WorkspaceRouteId id, ICredentialStore store, string target, string secret, string userName, int status, string failure)
        {
            try
            {
                store.Write(target, secret, userName);
                return null;
            }
            catch (CredentialStoreException e)
            {
                return Failure<SecretReceipt>(id, PluginErrorKind.Refused, failure + " (" + Redactor.PatternsOnly.Redact(e.Message) + ")", status);
            }
        }

        // Refused before anything was sent.
        private static ApiResult<T> Refused<T>(WorkspaceRouteId id, string message)
            where T : class
            => ApiResult<T>.Failure(NotSent(Failure<T>(id, PluginErrorKind.Refused, message, 0).Error));

        private static PluginError NotSent(PluginError error)
        {
            error.NotSent = true;
            return error;
        }

        private static ApiResult<T> Failure<T>(WorkspaceRouteId id, PluginErrorKind kind, string message, int status)
            where T : class
        {
            return ApiResult<T>.Failure(new PluginError(WorkspaceRoutes.Get(id).Step, kind, message, null) { HttpStatus = status });
        }
    }
}
