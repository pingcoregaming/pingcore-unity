using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>The release calls: build targets, start, poll, acknowledge, and the window's Cancel and Acknowledge.</summary>
    public sealed partial class PipelineRunner
    {
        /// <summary>Cancels the run's release (a pending one cancels outright). The watch then sees it end.</summary>
        public async Task<PluginError> CancelReleaseAsync(CancellationToken cancellationToken)
        {
            if (State == null || State.ReleaseId <= 0)
            {
                return new PluginError("release", PluginErrorKind.Refused, "There is no release of this deploy to cancel.", null);
            }

            ApiResult<ReleaseChangedResponse> result = await services.Api.CancelReleaseAsync(State.FleetId, State.ReleaseId, cancellationToken);
            Log(result.Ok ? $"release {State.ReleaseId}: cancel requested ({result.Value?.Release?.State ?? "?"})" : $"release {State.ReleaseId}: cancel refused: {result.Error}");
            return result.Ok ? null : result.Error;
        }

        /// <summary>Acknowledges the run's failed release so it no longer holds scale-down. Refused while running (the watch acknowledges on its own).</summary>
        public async Task<PluginError> AcknowledgeReleaseAsync(CancellationToken cancellationToken)
        {
            if (IsRunning)
            {
                return new PluginError("release", PluginErrorKind.Refused, Busy().Message, null);
            }

            if (State == null || State.ReleaseId <= 0 || State.ReleaseState != "failed")
            {
                return new PluginError("release", PluginErrorKind.Refused, "There is no failed release of this deploy to acknowledge.", null);
            }

            ApiResult<ReleaseChangedResponse> result = await services.Api.AcknowledgeReleaseAsync(State.FleetId, State.ReleaseId, cancellationToken);
            if (!result.Ok)
            {
                Log($"release {State.ReleaseId}: acknowledge refused: {result.Error}");
                return result.Error;
            }

            DeployState s = State.Clone();
            s.Acknowledged = true;
            s.FailureMessage = $"Release {s.ReleaseId} failed{(string.IsNullOrEmpty(s.ReleaseFailedReason) ? string.Empty : " (" + s.ReleaseFailedReason + ")")}. It was acknowledged, so it no longer holds scale-down.";
            State = s;
            Save(State);
            Log($"release {State.ReleaseId}: acknowledged");
            PublishSteps(false);
            return null;
        }

        private async Task<StepResult> ExecuteReleaseStepAsync(DeployAction action, DateTime now, CancellationToken ct)
        {
            switch (action.Kind)
            {
                case DeployActionKind.ReadBuildTargets:
                {
                    ApiResult<BuildTargetsResponse> r = await services.Api.ListBuildTargetsAsync(State.FleetId, ct);
                    return r.Ok ? new StepResult(action.Kind, null) { BuildTargets = r.Value } : StepResult.Failure(action.Kind, r.Error, now);
                }

                case DeployActionKind.StartRelease:
                {
                    Log($"release: POST fleets/{State.FleetId}/releases targetBuildVersion {State.Snapshot}");
                    ApiResult<ReleaseCreatedResponse> r = await services.Api.CreateReleaseAsync(State.FleetId, new ReleaseCreateRequest { TargetBuildVersion = State.Snapshot }, ct);
                    return r.Ok ? new StepResult(action.Kind, null) { CreatedRelease = r.Value?.Release } : StepResult.Failure(action.Kind, r.Error, now);
                }

                case DeployActionKind.PollRelease:
                {
                    ApiResult<ReleaseDetailResponse> r = await services.Api.GetReleaseAsync(State.FleetId, State.ReleaseId, ct);
                    if (r.Ok && r.Value?.Release != null)
                    {
                        ReleaseProgress = new ReleaseProgressView(State.FleetId, State.ReleaseId, r.Value.Release.TargetBuildVersion ?? State.Snapshot, r.Value.Release.State, r.Value.Release.Blocked, ReleaseWatcher.Progress(r.Value));
                        ReleaseProgressChanged?.Invoke(ReleaseProgress);
                    }

                    return r.Ok ? new StepResult(action.Kind, null) { ReleaseDetail = r.Value } : StepResult.Failure(action.Kind, r.Error, now);
                }

                case DeployActionKind.Acknowledge:
                {
                    ApiResult<ReleaseChangedResponse> r = await services.Api.AcknowledgeReleaseAsync(State.FleetId, State.ReleaseId, ct);
                    return r.Ok ? new StepResult(action.Kind, null) : StepResult.Failure(action.Kind, r.Error, now);
                }

                default:
                    throw new InvalidOperationException("not a release action: " + action.Kind);
            }
        }
    }
}
