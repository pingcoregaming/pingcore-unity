using System;
using System.Collections.Generic;
using System.Linq;
using PingCore.Editor.Workspace.Api.Wire;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>How a release poll reads.</summary>
    public enum ReleaseVerdict
    {
        /// <summary><c>pending</c>, <c>surging</c>, <c>rolling</c>, <c>completing</c> or a state this version does not know: keep polling.</summary>
        InProgress,

        /// <summary><c>completed</c>.</summary>
        Completed,

        /// <summary><c>failed</c> and not yet acknowledged: it holds scale-down until it is.</summary>
        FailedUnacknowledged,

        /// <summary><c>failed</c> and acknowledged.</summary>
        FailedAcknowledged,

        /// <summary><c>rolled_back</c> or <c>dismissed</c> (a cancel).</summary>
        Ended,
    }

    /// <summary>
    /// The release rules proven live against PingCore, as pure
    /// functions: poll every 5 s, read per-location progress, acknowledge a failed release, never
    /// retry a release except once on <c>server_unreachable</c>, and turn the handler's refusal
    /// reasons into sentences.
    /// </summary>
    public static class ReleaseWatcher
    {
        /// <summary>How often a running release is polled.</summary>
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

        /// <summary>The wait before the one retry of a release refused with <c>server_unreachable</c>.</summary>
        public static readonly TimeSpan UnreachableRetryDelay = TimeSpan.FromSeconds(30);

        /// <summary>Consecutive failed polls (network, 5xx) before the watch stops; the release itself keeps going and can be resumed.</summary>
        public const int MaxPollFailures = 6;

        /// <summary>True for a state after which the release never moves again.</summary>
        public static bool IsFinal(string state)
        {
            return state == "completed" || state == "failed" || state == "rolled_back" || state == "dismissed";
        }

        /// <summary>Reads one release row. Pure.</summary>
        public static ReleaseVerdict Classify(ReleaseView release)
        {
            switch (release?.State)
            {
                case "completed":
                    return ReleaseVerdict.Completed;
                case "failed":
                    return string.IsNullOrEmpty(release.AcknowledgedAt) ? ReleaseVerdict.FailedUnacknowledged : ReleaseVerdict.FailedAcknowledged;
                case "rolled_back":
                case "dismissed":
                    return ReleaseVerdict.Ended;
                default:
                    return ReleaseVerdict.InProgress;
            }
        }

        /// <summary>The per-location rows of a release detail. Pure.</summary>
        public static List<ReleaseLocationProgress> Progress(ReleaseDetailResponse detail)
        {
            return (detail?.Deployments ?? new List<ReleaseDeploymentView>())
                .Where(d => d != null)
                .Select(d => new ReleaseLocationProgress
                {
                    BrandDeploymentId = d.BrandDeploymentId,
                    Phase = d.Phase,
                    OldCount = d.OldCount,
                    NewCount = d.NewCount,
                    RetiringCount = d.RetiringCount,
                    Blocked = d.Blocked,
                    Parked = d.Parked,
                })
                .ToList();
        }

        /// <summary>One line for the step row: the state, what blocks it, and each location. Pure.</summary>
        public static string Describe(long releaseId, string state, string blocked, IEnumerable<ReleaseLocationProgress> locations)
        {
            string text = $"release {releaseId}: {state ?? "unknown"}";
            if (!string.IsNullOrEmpty(blocked))
            {
                text += $" (waiting: {blocked})";
            }

            List<ReleaseLocationProgress> rows = (locations ?? Enumerable.Empty<ReleaseLocationProgress>()).ToList();
            return rows.Count == 0 ? text : text + "; " + string.Join("; ", rows.Select(r => r.ToString()));
        }

        /// <summary>
        /// What a release refusal reason means and whether it is retried (only
        /// <c>server_unreachable</c>, once). The release endpoint's refusal reasons. Pure.
        /// </summary>
        public static (bool Retry, string Message) Refusal(string reason)
        {
            switch (reason)
            {
                case DeployFailure.ServerUnreachable:
                    return (true, "A game server did not answer the release pre-check.");
                case DeployFailure.ReleaseInProgress:
                    return (false, "The fleet already runs a release; let it finish, or cancel it, then release again.");
                case DeployFailure.SnapshotNotFound:
                    return (false, "The CDN has no snapshot of that name; push again, then release.");
                case DeployFailure.CdnUnreadable:
                    return (false, "The CDN could not be read; try the release again later.");
                case DeployFailure.TooManyPins:
                    return (false, "The CDN source already pins as many snapshots as it may; a running release must end first.");
                case DeployFailure.SupervisorTooOld:
                    return (false, "A game server of the fleet runs a supervisor too old for this release; recycle it onto a newer supervisor first.");
                default:
                    return (false, $"The workspace refused the release ({(string.IsNullOrEmpty(reason) ? "no reason given" : reason)}).");
            }
        }

        /// <summary>
        /// Does the build targets answer list <paramref name="snapshot"/>? A CDN branch counts it when
        /// <c>cdnCurrentVersion</c> is it or the targets list holds it; an image branch when the
        /// targets list holds it. Pure.
        /// </summary>
        public static bool TargetsList(BuildTargetsResponse targets, string snapshot)
        {
            if (targets == null || string.IsNullOrEmpty(snapshot))
            {
                return false;
            }

            if (targets.DataSourceType == "cdn_source" && string.Equals(targets.CdnCurrentVersion, snapshot, StringComparison.Ordinal))
            {
                return true;
            }

            return (targets.Targets ?? new List<BuildTargetView>()).Any(t => t != null && string.Equals(t.BuildVersion, snapshot, StringComparison.Ordinal));
        }
    }
}
