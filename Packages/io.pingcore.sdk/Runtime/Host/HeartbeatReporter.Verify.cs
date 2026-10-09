using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Discovery.Host.Wire;

namespace PingCore.Discovery.Host
{
    /// <summary>Reservation verify for the heartbeat tier. The hosted path never calls it.</summary>
    public sealed partial class HeartbeatReporter
    {
        private const int PlayerIdMaxLength = 128;

        /// <summary>
        /// Asks Discovery whether reservation <paramref name="reservationId"/> admits player
        /// <paramref name="playerId"/> on this game server:
        /// <c>GET /v1/reservations/verify/{reservationId}?serverId=&lt;own&gt;&amp;playerId=&lt;player&gt;</c>.
        /// It always sends both: the own serverId comes from the first accepted heartbeat, so before
        /// one the answer is <see cref="VerifyVerdict.Unavailable"/> with nothing sent; a null, empty
        /// or over-long (more than 128 characters) player id is <see cref="VerifyVerdict.Invalid"/>
        /// with nothing sent, because it names nobody. The verdict mapping is <c>VerifyVerdictMapper</c>:
        /// 429, 503 and transport failures are <see cref="VerifyVerdict.Unavailable"/>, never valid.
        /// With an open app's shipped token Discovery also takes the seat for an unnamed hold; the same
        /// player is admitted again on a re-verify, so a retried join does not use a second seat.
        /// </summary>
        public async Task<VerifyResult> VerifyReservationAsync(string reservationId, string playerId, CancellationToken cancellationToken)
        {
            string own;
            DiscoveryCaller discovery;
            lock (gate)
            {
                own = serverId;
                discovery = caller;
            }

            if (own == null || discovery == null)
            {
                return new VerifyResult(VerifyVerdict.Unavailable, false, null,
                    DiscoveryCallResult.Refused(DiscoveryReason.Unknown, "no heartbeat was accepted yet, so this game server has no serverId to verify for"));
            }

            if (string.IsNullOrEmpty(reservationId))
            {
                return new VerifyResult(VerifyVerdict.Invalid, false, null, DiscoveryCallResult.Refused(DiscoveryReason.Unknown, "no reservation id"));
            }

            if (string.IsNullOrEmpty(playerId) || playerId.Length > PlayerIdMaxLength)
            {
                return new VerifyResult(VerifyVerdict.Invalid, false, null, DiscoveryCallResult.Refused(DiscoveryReason.Unknown, "the player id must be 1 to 128 characters"));
            }

            string path = "/v1/reservations/verify/" + DiscoveryCaller.Segment(reservationId)
                + "?serverId=" + DiscoveryCaller.Segment(own)
                + "&playerId=" + DiscoveryCaller.Segment(playerId);
            DiscoveryRequest request = DiscoveryRequest.Create("GET", path, "GET /v1/reservations/verify/{reservationId}").WithBearer(token);
            DiscoveryResult<VerifyReservationResponse> result = await discovery.SendAsync<VerifyReservationResponse>(request, cancellationToken);
            VerifyResult verdict = VerifyVerdictMapper.Map(result, result.Value, own, playerId);
            WarnOnAnswerForm(verdict);
            return verdict;
        }

        /// <summary>Warns once when the answer's form contradicts <see cref="HeartbeatReporterOptions.TokenShipsInGame"/>.</summary>
        private void WarnOnAnswerForm(VerifyResult verdict)
        {
            if (verdict.Response == null)
            {
                return;
            }

            bool contradicts = tokenShipsInGame ? verdict.Detailed : !verdict.Detailed && verdict.Response.Valid;
            lock (gate)
            {
                if (!contradicts || verifyFormWarned)
                {
                    return;
                }

                verifyFormWarned = true;
            }

            Write(HeartbeatLogLevel.Warning, "verify", tokenShipsInGame
                ? "TokenShipsInGame is set but Discovery answered in detail, so the token is not an open app's shipped heartbeat token"
                : "Discovery answered with the verdict only, so the token is an open app's shipped heartbeat token; set TokenShipsInGame", verdict.Call == null ? 0 : verdict.Call.Status);
        }
    }
}
