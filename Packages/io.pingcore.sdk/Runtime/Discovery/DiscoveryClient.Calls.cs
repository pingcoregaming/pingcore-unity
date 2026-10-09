using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Discovery.Client.Wire;

namespace PingCore.Discovery.Client
{
    /// <summary>The server list, locations, latency, reservations and quick join.</summary>
    public sealed partial class DiscoveryClient
    {
        /// <summary>How long <see cref="GetLocationsAsync"/> reuses its answer.</summary>
        public static readonly TimeSpan LocationsCacheTime = TimeSpan.FromMinutes(5);

        /// <summary>How long <see cref="MeasureLatencyAsync"/> reuses a successful measurement.</summary>
        public static readonly TimeSpan LatencyCacheTime = TimeSpan.FromMinutes(10);

        private readonly object cacheSync = new object();
        private IReadOnlyList<Location> cachedLocations;
        private DateTimeOffset cachedLocationsAt;
        private LatencyResult cachedLatency;
        private DateTimeOffset cachedLatencyAt;

        private string AppPath => "/v1/apps/" + DiscoveryCaller.Segment(AppPublicId);

        /// <summary>One page of the public server list (no player token needed). <see cref="ServerPage.Next"/> pages on.</summary>
        public async Task<DiscoveryResult<ServerPage>> ListServersAsync(ServerListQuery query, CancellationToken cancellationToken)
        {
            ServerListQuery q = query ?? new ServerListQuery();
            string queryString = q.ToQueryString();
            DiscoveryRequest request = DiscoveryRequest.Create("GET", AppPath + "/servers" + (queryString.Length > 0 ? "?" + queryString : string.Empty), "GET /v1/apps/{publicId}/servers");
            DiscoveryResult<ServerListResponse> answer = await SendPublicAsync<ServerListResponse>(request, "servers.list", cancellationToken);
            if (!answer.IsOk)
            {
                return DiscoveryResult<ServerPage>.From(answer);
            }

            ServerListResponse body = answer.Value;
            int consumed = body.Offset + body.Returned;
            ServerListQuery next = body.Returned > 0 && consumed < body.TotalServers ? q.Clone().Page(Math.Max(1, Math.Min(ServerListQuery.MaxLimit, body.Limit)), consumed) : null;
            var page = new ServerPage(body.Servers ?? new List<PublicServer>(), body.TotalServers, body.Returned, body.Limit, body.Offset, next);
            return new DiscoveryResult<ServerPage>(DiscoveryOutcome.Ok, answer.Status, null, null, null, null, answer.RateLimit, page);
        }

        /// <summary>The platform's locations (no player token needed), reused for <see cref="LocationsCacheTime"/>.</summary>
        public async Task<DiscoveryResult<IReadOnlyList<Location>>> GetLocationsAsync(CancellationToken cancellationToken)
        {
            DateTimeOffset now = scheduler.UtcNow;
            lock (cacheSync)
            {
                if (cachedLocations != null && now - cachedLocationsAt < LocationsCacheTime)
                {
                    return DiscoveryResult<IReadOnlyList<Location>>.Success(cachedLocations, 0, null);
                }
            }

            DiscoveryRequest request = DiscoveryRequest.Create("GET", "/v1/locations", "GET /v1/locations");
            DiscoveryResult<LocationsResponse> answer = await SendPublicAsync<LocationsResponse>(request, "locations", cancellationToken);
            if (!answer.IsOk)
            {
                return DiscoveryResult<IReadOnlyList<Location>>.From(answer);
            }

            IReadOnlyList<Location> locations = (IReadOnlyList<Location>)answer.Value.Locations ?? Array.Empty<Location>();
            lock (cacheSync)
            {
                cachedLocations = locations;
                cachedLocationsAt = scheduler.UtcNow;
            }

            return new DiscoveryResult<IReadOnlyList<Location>>(DiscoveryOutcome.Ok, answer.Status, null, null, null, null, answer.RateLimit, locations);
        }

        /// <summary>
        /// The locations, then the latency probe on each beacon. A successful result is reused for
        /// <see cref="LatencyCacheTime"/>. A WebGL player measures through the browser's WebSocket.
        /// </summary>
        public async Task<LatencyResult> MeasureLatencyAsync(LatencyProbeOptions probeOptions, CancellationToken cancellationToken)
        {
            if (!LatencyProbe.IsSupported)
            {
                return new LatencyResult(DiscoveryOutcome.Unsupported, null, null, "the latency probe is not supported on this platform");
            }

            DateTimeOffset now = scheduler.UtcNow;
            lock (cacheSync)
            {
                if (cachedLatency != null && now - cachedLatencyAt < LatencyCacheTime)
                {
                    return cachedLatency;
                }
            }

            DiscoveryResult<IReadOnlyList<Location>> locations = await GetLocationsAsync(cancellationToken);
            if (!locations.IsOk)
            {
                return new LatencyResult(locations.Outcome, null, null, "locations: " + locations);
            }

            using (CancellationTokenSource linked = Link(cancellationToken))
            {
                LatencyResult result = await latencyProbe.MeasureAsync(locations.Value, probeOptions, linked.Token);
                if (result.IsOk)
                {
                    lock (cacheSync)
                    {
                        cachedLatency = result;
                        cachedLatencyAt = scheduler.UtcNow;
                    }
                }

                Write(result.IsOk ? DiscoveryLogLevel.Info : DiscoveryLogLevel.Warning, "latency", result.Outcome + ", " + result.Medians.Count + " of " + result.Detail.Count + " locations measured");
                return result;
            }
        }

        /// <summary>
        /// Holds seats on a chosen game server. The SDK mints <see cref="ReserveOptions.ReservationId"/>
        /// when absent and reuses it on its own retry, so a retry after a lost answer replays instead
        /// of holding twice. With one seat and no player ids it names the token's own player id. A 401
        /// re-issue under another player id is resent only when no earlier attempt may have reached
        /// Discovery; otherwise it answers Unauthorized with <see cref="DiscoveryCallResult.IdentityChanged"/>.
        /// </summary>
        public async Task<DiscoveryResult<ReservationResponse>> ReserveAsync(string serverId, ReserveOptions reserveOptions, CancellationToken cancellationToken)
        {
            ReserveOptions o = reserveOptions ?? new ReserveOptions();
            DiscoveryCallResult refused = RequestChecks.CheckReserve(serverId, o);
            if (refused != null)
            {
                return DiscoveryResult<ReservationResponse>.From(refused);
            }

            string reservationId = o.ReservationId ?? SecureIds.NewId128();
            string path = AppPath + "/servers/" + DiscoveryCaller.Segment(serverId) + "/reservations";
            return await SendAuthorisedAsync<ReservationResponse>(
                token => DiscoveryRequest.Json("POST", path, new ReserveRequest
                {
                    ReservationId = reservationId,
                    Seats = o.Seats,
                    PlayerIds = SeatIds(o.Seats, o.PlayerIds, token),
                    TtlSeconds = o.TtlSeconds,
                    Context = o.Context,
                }, "POST /v1/apps/{publicId}/servers/{serverId}/reservations"),
                "reservations.create",
                true,
                OwnerBinding.OnceAnAttemptMayHaveLanded,
                cancellationToken);
        }

        /// <summary>
        /// Quick join: holds seats on the best visible game server that fits. The SDK mints
        /// <see cref="QuickJoinOptions.IdempotencyKey"/> when absent and reuses it on its own retry;
        /// a repeat with the same key answers the original hold with <c>replayed: true</c>. The key is
        /// scoped per player, so a 401 re-issue under another player id is resent only when no earlier
        /// attempt may have reached Discovery; otherwise it answers Unauthorized with
        /// <see cref="DiscoveryCallResult.IdentityChanged"/>.
        /// </summary>
        public async Task<DiscoveryResult<ReservationResponse>> QuickJoinAsync(QuickJoinOptions quickJoinOptions, CancellationToken cancellationToken)
        {
            QuickJoinOptions o = quickJoinOptions ?? new QuickJoinOptions();
            DiscoveryCallResult refused = RequestChecks.CheckQuickJoin(o);
            if (refused != null)
            {
                return DiscoveryResult<ReservationResponse>.From(refused);
            }

            string key = o.IdempotencyKey != null ? o.IdempotencyKey.Trim() : SecureIds.NewId128();
            Dictionary<string, int> latency = o.Latency != null && o.Latency.Count > 0 ? new Dictionary<string, int>(ToDictionary(o.Latency), StringComparer.Ordinal) : null;
            return await SendAuthorisedAsync<ReservationResponse>(
                token => DiscoveryRequest.Json("POST", AppPath + "/quick-join", new QuickJoinRequest
                {
                    IdempotencyKey = key,
                    Seats = o.Seats,
                    PlayerIds = SeatIds(o.Seats, o.PlayerIds, token),
                    Filters = o.Filters,
                    Context = o.Context,
                    Latency = latency,
                    MaxLatencyMs = o.MaxLatencyMs,
                }, "POST /v1/apps/{publicId}/quick-join"),
                "quickJoin",
                true,
                OwnerBinding.OnceAnAttemptMayHaveLanded,
                cancellationToken);
        }

        /// <summary>
        /// Reads a live reservation back; 404 when it is released, expired or not this player's. Bound to
        /// the player who reserved: after a 401 re-issue under another player id it is not resent and
        /// answers Unauthorized with <see cref="DiscoveryCallResult.IdentityChanged"/>.
        /// </summary>
        public async Task<DiscoveryResult<ReservationRecord>> GetReservationAsync(string reservationId, CancellationToken cancellationToken)
        {
            if (!SecureIds.IsValidId(reservationId))
            {
                return DiscoveryResult<ReservationRecord>.Refused(DiscoveryReason.Unknown, "a reservation id is 1 to 100 characters from [A-Za-z0-9_.:-]");
            }

            DiscoveryRequest request = DiscoveryRequest.Create("GET", AppPath + "/reservations/" + DiscoveryCaller.Segment(reservationId), "GET /v1/apps/{publicId}/reservations/{reservationId}");
            return await SendAuthorisedAsync<ReservationRecord>(_ => request, "reservations.get", true, OwnerBinding.Always, cancellationToken);
        }

        /// <summary>
        /// Releases a reservation (idempotent: releasing a gone hold answers 200 too). Bound to the player who
        /// reserved, like <see cref="GetReservationAsync"/>: an identity change answers Unauthorized with
        /// <see cref="DiscoveryCallResult.IdentityChanged"/>, and the hold then lapses at its TTL.
        /// </summary>
        public async Task<DiscoveryResult<ReleaseReservationResponse>> ReleaseReservationAsync(string reservationId, CancellationToken cancellationToken)
        {
            if (!SecureIds.IsValidId(reservationId))
            {
                return DiscoveryResult<ReleaseReservationResponse>.Refused(DiscoveryReason.Unknown, "a reservation id is 1 to 100 characters from [A-Za-z0-9_.:-]");
            }

            DiscoveryRequest request = DiscoveryRequest.Create("DELETE", AppPath + "/reservations/" + DiscoveryCaller.Segment(reservationId), "DELETE /v1/apps/{publicId}/reservations/{reservationId}");
            return await SendAuthorisedAsync<ReleaseReservationResponse>(_ => request, "reservations.release", true, OwnerBinding.Always, cancellationToken);
        }

        private static List<string> SeatIds(int seats, IReadOnlyList<string> playerIds, PlayerToken token)
        {
            if (playerIds != null)
            {
                return new List<string>(playerIds);
            }

            return seats == 1 && token != null ? new List<string> { token.PlayerId } : null;
        }

        private static IDictionary<string, int> ToDictionary(IReadOnlyDictionary<string, int> source)
        {
            var copy = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> entry in source)
            {
                copy[entry.Key] = entry.Value;
            }

            return copy;
        }
    }
}
