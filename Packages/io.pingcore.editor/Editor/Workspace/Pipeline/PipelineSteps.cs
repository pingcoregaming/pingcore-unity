using System;
using System.Collections.Generic;
using System.Linq;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>
    /// The step list the Ship section shows, derived from the persisted state and the next action,
    /// pure. Deriving rather than tracking means a resumed run (after a reload or a restart) shows
    /// the same rows as the run that was interrupted.
    /// </summary>
    public static class PipelineSteps
    {
        /// <summary>Every step, in order.</summary>
        public static readonly IReadOnlyList<PipelineStep> All = (PipelineStep[])Enum.GetValues(typeof(PipelineStep));

        private static readonly Dictionary<string, PipelineStep> FailureStep = new Dictionary<string, PipelineStep>(StringComparer.Ordinal)
        {
            [DeployFailure.BuildFailed] = PipelineStep.Build,
            [DeployFailure.NoBuild] = PipelineStep.Push,
            [DeployFailure.StartupExecutableMissing] = PipelineStep.Push,
            [DeployFailure.PushFailed] = PipelineStep.Push,
            [DeployFailure.SnapshotUnknown] = PipelineStep.Push,
            [DeployFailure.TargetsFailed] = PipelineStep.BuildTargets,
            [DeployFailure.SnapshotNotListed] = PipelineStep.BuildTargets,
            [DeployFailure.DataSourceMismatch] = PipelineStep.BuildTargets,
            [DeployFailure.AlreadyPinned] = PipelineStep.Release,
            [DeployFailure.ReleaseRefused] = PipelineStep.Release,
            [DeployFailure.ReleaseInProgress] = PipelineStep.Release,
            [DeployFailure.SnapshotNotFound] = PipelineStep.Release,
            [DeployFailure.CdnUnreadable] = PipelineStep.Release,
            [DeployFailure.TooManyPins] = PipelineStep.Release,
            [DeployFailure.ServerUnreachable] = PipelineStep.Release,
            [DeployFailure.SupervisorTooOld] = PipelineStep.Release,
            [DeployFailure.ReleaseEnded] = PipelineStep.Watch,
            [DeployFailure.PollFailed] = PipelineStep.Watch,
        };

        /// <summary>The step an action belongs to.</summary>
        public static PipelineStep StepOf(DeployActionKind kind)
        {
            switch (kind)
            {
                case DeployActionKind.Build:
                    return PipelineStep.Build;
                case DeployActionKind.IssuePushTokenNeeded:
                    return PipelineStep.PushToken;
                case DeployActionKind.Push:
                    return PipelineStep.Push;
                case DeployActionKind.ReadBuildTargets:
                    return PipelineStep.BuildTargets;
                case DeployActionKind.StartRelease:
                    return PipelineStep.Release;
                case DeployActionKind.Acknowledge:
                    return PipelineStep.Acknowledge;
                default:
                    return PipelineStep.Watch;
            }
        }

        /// <summary>
        /// The status and detail of every step. <paramref name="next"/> is the planner's latest action
        /// (null before the first plan); <paramref name="lastStep"/> is the step that ran last, which a
        /// failure or a stop is charged to when its code names no step.
        /// </summary>
        public static IReadOnlyList<(PipelineStep Step, PipelineStepStatus Status, string Detail)> Derive(DeployState s, DeployAction next, PipelineStep? lastStep, bool stopped)
        {
            var rows = new List<(PipelineStep, PipelineStepStatus, string)>();
            if (s == null)
            {
                return All.Select(step => (step, PipelineStepStatus.Pending, string.Empty)).ToList();
            }

            DeployActionKind? nextKind = next?.Kind;
            PipelineStep? failedAt = null;
            if (s.Phase == DeployPhase.Failed)
            {
                if (s.Failure == DeployFailure.ReleaseFailed)
                {
                    failedAt = s.Acknowledged ? PipelineStep.Watch : PipelineStep.Acknowledge;
                }
                else if (s.Failure == DeployFailure.ReleaseOutcomeUnknown)
                {
                    failedAt = PipelineStep.Release;
                }
                else if (s.Failure != null && FailureStep.TryGetValue(s.Failure, out PipelineStep mapped))
                {
                    failedAt = mapped;
                }
                else
                {
                    failedAt = lastStep ?? FirstWanted(s);
                }
            }

            PipelineStep? running = nextKind != null && DeployPlanner.IsExecutable(nextKind.Value) && s.Phase == DeployPhase.Running ? StepOf(nextKind.Value) : (PipelineStep?)null;
            foreach (PipelineStep step in All)
            {
                bool wanted = Wanted(s, step);
                PipelineStepStatus status;
                string detail = string.Empty;
                if (!wanted)
                {
                    status = PipelineStepStatus.Skipped;
                }
                else if (failedAt == step)
                {
                    status = s.Failure == DeployFailure.Cancelled ? PipelineStepStatus.Cancelled : PipelineStepStatus.Failed;
                    detail = s.FailureMessage ?? s.Failure ?? string.Empty;
                }
                else if (Done(s, step))
                {
                    status = PipelineStepStatus.Succeeded;
                    detail = DoneDetail(s, step);
                }
                else if (step == PipelineStep.PushToken && nextKind == DeployActionKind.IssuePushTokenNeeded)
                {
                    status = PipelineStepStatus.NeedsInput;
                    detail = next.Message ?? string.Empty;
                }
                else if (running == step)
                {
                    status = stopped ? PipelineStepStatus.Cancelled : PipelineStepStatus.Running;
                    detail = RunningDetail(s, step, next);
                }
                else
                {
                    status = PipelineStepStatus.Pending;
                }

                rows.Add((step, status, detail));
            }

            return rows;
        }

        private static PipelineStep FirstWanted(DeployState s)
        {
            return All.FirstOrDefault(step => Wanted(s, step));
        }

        private static bool Wanted(DeployState s, PipelineStep step)
        {
            switch (step)
            {
                case PipelineStep.Build:
                    return s.WantBuild;
                case PipelineStep.PushToken:
                    return s.WantPush;
                case PipelineStep.Push:
                    return s.WantPush;
                case PipelineStep.Acknowledge:
                    return s.WantRelease && (s.ReleaseState == "failed" || s.Acknowledged);
                default:
                    return s.WantRelease;
            }
        }

        private static bool Done(DeployState s, PipelineStep step)
        {
            switch (step)
            {
                case PipelineStep.Build:
                    return s.Built;
                case PipelineStep.PushToken:
                case PipelineStep.Push:
                    return s.Pushed;
                case PipelineStep.BuildTargets:
                    return s.TargetsConfirmed;
                case PipelineStep.Release:
                    return s.ReleaseId > 0;
                case PipelineStep.Watch:
                    return s.ReleaseState == "completed";
                default:
                    return s.Acknowledged;
            }
        }

        private static string DoneDetail(DeployState s, PipelineStep step)
        {
            switch (step)
            {
                case PipelineStep.Build:
                    return s.BuildFolder ?? string.Empty;
                case PipelineStep.Push:
                    return s.Snapshot == null ? string.Empty : $"snapshot {s.Snapshot}" + (s.SnapshotChanged == false ? " (unchanged)" : s.SnapshotChanged == true ? " (new)" : string.Empty);
                case PipelineStep.BuildTargets:
                    return $"lists {s.Snapshot}";
                case PipelineStep.Release:
                    return $"release {s.ReleaseId} -> {s.Snapshot}";
                case PipelineStep.Watch:
                    return ReleaseWatcher.Describe(s.ReleaseId, s.ReleaseState, null, s.Locations);
                case PipelineStep.Acknowledge:
                    return $"release {s.ReleaseId} acknowledged";
                default:
                    return string.Empty;
            }
        }

        private static string RunningDetail(DeployState s, PipelineStep step, DeployAction next)
        {
            switch (step)
            {
                case PipelineStep.BuildTargets:
                    return next?.Message ?? $"reading build targets of fleet {s.FleetId}";
                case PipelineStep.Release:
                    return next?.Message ?? $"releasing {s.Snapshot} onto fleet {s.FleetId}";
                case PipelineStep.Watch:
                    return ReleaseWatcher.Describe(s.ReleaseId, s.ReleaseState, s.ReleaseBlocked, s.Locations);
                default:
                    return next?.Message ?? string.Empty;
            }
        }
    }
}
