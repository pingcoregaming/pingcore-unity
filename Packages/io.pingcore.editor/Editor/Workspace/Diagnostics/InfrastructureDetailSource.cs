using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Discovery.Client;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Redaction;

namespace PingCore.Editor.Workspace.Infrastructure
{
    /// <summary>
    /// Answers a game client's missing-infrastructure question from the workspace with read-only GETs on the fleet
    /// Connect picked (the routes Connect and Status already use): <c>GET fleets/{id}</c> always, and
    /// <c>GET fleets/{id}/live</c> only for <see cref="InfrastructureState.NoGameServers"/> on a fleet with deployments,
    /// then <see cref="InfrastructureDetail.Describe"/>. A failed fleet read, a missing fleet or an exception answers
    /// null (the game shows its own message alone); a failed live read only leaves the counts out (it answers 503 while
    /// the fleet's app is disabled or has no active token). The answer passes through the redactor; no key, token or
    /// error body is ever part of it. The game client's check cancels <paramref name="cancellationToken"/> after its 5 s
    /// wait, which stops the reads.
    /// </summary>
    public static class InfrastructureDetailSource
    {
        /// <summary>The detail for <paramref name="question"/>, or null. Never throws.</summary>
        public static async Task<string> AnswerAsync(IPingCoreApi api, long fleetId, InfrastructureQuestion question, CancellationToken cancellationToken)
        {
            if (api == null || fleetId <= 0 || question == null)
            {
                return null;
            }

            try
            {
                ApiResult<FleetDetailResponse> fleet = await api.GetFleetAsync(fleetId, cancellationToken);
                if (fleet == null || !fleet.Ok || fleet.Value?.Fleet == null || cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                FleetLiveResponse live = null;
                if (question.State == InfrastructureState.NoGameServers && fleet.Value.Deployments != null && fleet.Value.Deployments.Count > 0)
                {
                    ApiResult<FleetLiveResponse> read = await api.GetFleetLiveAsync(fleetId, cancellationToken);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return null;
                    }

                    live = read != null && read.Ok ? read.Value : null;
                }

                string detail = InfrastructureDetail.Describe(question.State, question.AppPublicId, fleet.Value, live);
                return detail == null ? null : Redactor.PatternsOnly.Redact(detail);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
