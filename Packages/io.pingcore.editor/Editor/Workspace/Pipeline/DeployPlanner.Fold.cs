using System;
using System.Collections.Generic;
using System.Linq;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>The planner's folds: each step's result into the state, with the release rules applied.</summary>
    public static partial class DeployPlanner
    {
        /// <summary>Folds a step result into <paramref name="s"/>; answers an action when the result decides one, else null (then <see cref="Decide"/> runs).</summary>
        private static DeployAction Fold(DeployRequest request, DeployState s, StepResult r, DateTime nowUtc, List<string> notes)
        {
            switch (r.Kind)
            {
                case DeployActionKind.Build:
                    if (!r.Ok)
                    {
                        return Fail(r.Error, DeployFailure.BuildFailed);
                    }

                    s.Built = true;
                    s.BuildFolder = r.BuildFolder ?? BuildFolderFor(s.BuildVersion);
                    notes.Add($"built {s.BuildFolder}");
                    return null;

                case DeployActionKind.Push:
                    if (!r.Ok)
                    {
                        string reason = r.Error.Reason == DeployFailure.NoBuild || r.Error.Reason == DeployFailure.StartupExecutableMissing ? r.Error.Reason : DeployFailure.PushFailed;
                        return Fail(r.Error, reason);
                    }

                    if (string.IsNullOrEmpty(r.Snapshot))
                    {
                        return DeployAction.Failed(DeployFailure.SnapshotUnknown,
                            "pingctl finished but did not print the published version, so there is nothing to release. Check the push log.");
                    }

                    s.Pushed = true;
                    s.Snapshot = r.Snapshot;
                    s.SnapshotChanged = r.SnapshotChanged;
                    notes.Add($"snapshot {r.Snapshot} ({(r.SnapshotChanged == false ? "unchanged" : r.SnapshotChanged == true ? "new" : "pushed")})");
                    return null;

                case DeployActionKind.ReadBuildTargets:
                    return FoldTargets(request, s, r, nowUtc, notes);

                case DeployActionKind.StartRelease:
                    return FoldStartRelease(s, r, notes);

                case DeployActionKind.PollRelease:
                    return FoldPoll(s, r, notes);

                case DeployActionKind.Acknowledge:
                    string why = string.IsNullOrEmpty(s.ReleaseFailedReason) ? string.Empty : $" ({s.ReleaseFailedReason})";
                    if (r.Ok)
                    {
                        s.Acknowledged = true;
                        return DeployAction.Failed(DeployFailure.ReleaseFailed,
                            $"Release {s.ReleaseId} failed{why}. It was acknowledged, so it no longer holds scale-down. Fix the cause and release again.");
                    }

                    return DeployAction.Failed(DeployFailure.ReleaseFailed,
                        $"Release {s.ReleaseId} failed{why} and could not be acknowledged ({r.Error.Message}). Acknowledge it under Ship, Release; until then it holds scale-down.");

                default:
                    return null;
            }
        }

        private static DeployAction FoldTargets(DeployRequest request, DeployState s, StepResult r, DateTime nowUtc, List<string> notes)
        {
            DateTime at = r.AtUtc == default ? nowUtc : r.AtUtc;
            if (s.TargetsFirstCheckedUtc == null)
            {
                s.TargetsFirstCheckedUtc = at;
            }

            bool withinLag = at - s.TargetsFirstCheckedUtc.Value < TargetsLagLimit;
            if (!r.Ok)
            {
                if (r.Error.Kind == PluginErrorKind.RateLimited && withinLag)
                {
                    return DeployAction.Wait(DeployActionKind.ReadBuildTargets, Max(r.Error.RetryAfter, TargetsPollInterval), "The workspace asked to slow down; reading the build targets again shortly.");
                }

                return Fail(r.Error, DeployFailure.TargetsFailed);
            }

            BuildTargetsResponse targets = r.BuildTargets ?? new BuildTargetsResponse();
            s.TargetsChecks++;
            s.DataSourceType = targets.DataSourceType;
            s.PinnedVersion = targets.PinnedBuildVersion;

            string refusal = DataSourceRule.Refusal(targets.DataSourceType);
            if (refusal != null)
            {
                return DeployAction.Failed(DeployFailure.DataSourceMismatch, refusal);
            }

            if (ReleaseWatcher.TargetsList(targets, s.Snapshot))
            {
                s.TargetsConfirmed = true;
                notes.Add($"build targets list {s.Snapshot}" + (s.PinnedVersion == null ? string.Empty : $" (fleet pinned to {s.PinnedVersion})"));
                return null;
            }

            if (!withinLag)
            {
                string has = string.Join(", ", (targets.Targets ?? new List<BuildTargetView>()).Where(t => t != null).Select(t => t.BuildVersion).Take(5));
                return DeployAction.Failed(DeployFailure.SnapshotNotListed,
                    $"The build targets of fleet {s.FleetId} still do not list {s.Snapshot} after {TargetsLagLimit.TotalSeconds:0} s (they list {(has.Length == 0 ? "nothing" : has)}).");
            }

            return DeployAction.Wait(DeployActionKind.ReadBuildTargets, TargetsPollInterval, $"Waiting for fleet {s.FleetId}'s build targets to list {s.Snapshot}.");
        }

        private static DeployAction FoldStartRelease(DeployState s, StepResult r, List<string> notes)
        {
            s.ReleaseAttempts++;
            if (r.Ok)
            {
                ReleaseView created = r.CreatedRelease;
                if (created == null || created.ReleaseId <= 0)
                {
                    return DeployAction.Failed(DeployFailure.ReleaseOutcomeUnknown,
                        "The workspace accepted the release but named no release id. Check the fleet's releases before releasing again.");
                }

                s.ReleaseId = created.ReleaseId;
                s.ReleaseState = created.State;
                notes.Add($"release {s.ReleaseId} -> {s.Snapshot}: {created.State}");
                return DeployAction.Wait(DeployActionKind.PollRelease, ReleaseWatcher.PollInterval, null);
            }

            PluginError e = r.Error;
            string reason = e.Reason;
            if (reason == null && e.Kind == PluginErrorKind.Conflict)
            {
                reason = DeployFailure.ReleaseInProgress;
            }

            if (reason != null)
            {
                (bool retry, string message) = ReleaseWatcher.Refusal(reason);
                if (retry && s.ReleaseAttempts == 1)
                {
                    return DeployAction.Wait(DeployActionKind.StartRelease, ReleaseWatcher.UnreachableRetryDelay, message + $" Trying once more in {ReleaseWatcher.UnreachableRetryDelay.TotalSeconds:0} s.");
                }

                return DeployAction.Failed(reason == DeployFailure.ServerUnreachable || IsKnownRefusal(reason) ? reason : DeployFailure.ReleaseRefused, message);
            }

            if (e.Kind == PluginErrorKind.Transport || e.Kind == PluginErrorKind.Envelope || e.Kind == PluginErrorKind.Cancelled)
            {
                return DeployAction.Failed(DeployFailure.ReleaseOutcomeUnknown,
                    $"No usable answer to the release request arrived ({e.Message}); the release may or may not have started. Check the fleet's releases before releasing again.");
            }

            return Fail(e, DeployFailure.ReleaseRefused);
        }

        private static DeployAction FoldPoll(DeployState s, StepResult r, List<string> notes)
        {
            if (!r.Ok)
            {
                PluginError e = r.Error;
                switch (e.Kind)
                {
                    case PluginErrorKind.RateLimited:
                        return DeployAction.Wait(DeployActionKind.PollRelease, Max(e.RetryAfter, ReleaseWatcher.PollInterval), null);
                    case PluginErrorKind.Transport:
                    case PluginErrorKind.Envelope:
                    case PluginErrorKind.Rejected:
                        s.PollFailures++;
                        if (s.PollFailures >= ReleaseWatcher.MaxPollFailures)
                        {
                            return DeployAction.Failed(DeployFailure.PollFailed,
                                $"Release {s.ReleaseId} could not be read {s.PollFailures} times in a row ({e.Message}). The release itself carries on; resume watching later.");
                        }

                        return DeployAction.Wait(DeployActionKind.PollRelease, ReleaseWatcher.PollInterval, null);
                    default:
                        return Fail(e, DeployFailure.PollFailed);
                }
            }

            s.PollFailures = 0;
            ReleaseStatusView release = r.ReleaseDetail?.Release;
            if (release == null)
            {
                return DeployAction.Failed(DeployFailure.PollFailed, $"The answer for release {s.ReleaseId} carried no release.");
            }

            s.ReleaseState = release.State;
            s.ReleaseBlocked = release.Blocked;
            s.ReleaseFailedReason = release.FailedReason;
            s.Locations = ReleaseWatcher.Progress(r.ReleaseDetail);
            switch (ReleaseWatcher.Classify(release))
            {
                case ReleaseVerdict.Completed:
                    notes.Add($"release {s.ReleaseId} completed");
                    return DeployAction.Of(DeployActionKind.Done);
                case ReleaseVerdict.FailedUnacknowledged:
                    return DeployAction.Of(DeployActionKind.Acknowledge);
                case ReleaseVerdict.FailedAcknowledged:
                    s.Acknowledged = true;
                    return DeployAction.Failed(DeployFailure.ReleaseFailed,
                        $"Release {s.ReleaseId} failed{(string.IsNullOrEmpty(release.FailedReason) ? string.Empty : " (" + release.FailedReason + ")")}; it is already acknowledged.");
                case ReleaseVerdict.Ended:
                    return DeployAction.Failed(DeployFailure.ReleaseEnded,
                        $"Release {s.ReleaseId} ended {release.State}" + (release.State == "dismissed" ? " (cancelled)." : "."));
                default:
                    return DeployAction.Wait(DeployActionKind.PollRelease, ReleaseWatcher.PollInterval, null);
            }
        }

        private static bool IsKnownRefusal(string reason)
        {
            return reason == DeployFailure.ReleaseInProgress || reason == DeployFailure.SnapshotNotFound || reason == DeployFailure.CdnUnreadable
                || reason == DeployFailure.TooManyPins || reason == DeployFailure.SupervisorTooOld;
        }

        private static DeployAction Fail(PluginError error, string fallbackReason)
        {
            if (error != null && error.Kind == PluginErrorKind.Cancelled)
            {
                return DeployAction.Failed(DeployFailure.Cancelled, "Stopped.");
            }

            string message = error == null ? null : string.IsNullOrEmpty(error.Hint) ? error.Message : error.Message + " " + error.Hint;
            return DeployAction.Failed(fallbackReason, message);
        }

        private static TimeSpan Max(TimeSpan? a, TimeSpan b) => a.HasValue && a.Value > b ? a.Value : b;
    }
}
