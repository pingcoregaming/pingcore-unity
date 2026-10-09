using System;
using System.Collections.Generic;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>What the planner tells the runner to do next.</summary>
    public enum DeployActionKind
    {
        /// <summary>Run the in-process server build through the build profile.</summary>
        Build,

        /// <summary>No push token is stored for the CDN source: stop and ask the developer to confirm issuing one.</summary>
        IssuePushTokenNeeded,

        /// <summary>The push: the startup command check, <c>pingctl version</c>, then <c>pingctl push</c>.</summary>
        Push,

        /// <summary><c>GET fleets/{id}/build-targets</c>, after <see cref="DeployAction.After"/>.</summary>
        ReadBuildTargets,

        /// <summary><c>POST fleets/{id}/releases</c>, after <see cref="DeployAction.After"/> (only the one <c>server_unreachable</c> retry waits).</summary>
        StartRelease,

        /// <summary><c>GET fleets/{id}/releases/{rid}</c>, after <see cref="DeployAction.After"/>.</summary>
        PollRelease,

        /// <summary><c>POST .../acknowledge</c> for a failed release.</summary>
        Acknowledge,

        /// <summary>Everything asked for is done.</summary>
        Done,

        /// <summary>Stopped; <see cref="DeployAction.Reason"/> says why.</summary>
        Failed,
    }

    /// <summary>The planner's answer. Pure value.</summary>
    public sealed class DeployAction
    {
        private DeployAction(DeployActionKind kind, TimeSpan after, string reason, string message)
        {
            Kind = kind;
            After = after;
            Reason = reason;
            Message = message;
        }

        public DeployActionKind Kind { get; }

        /// <summary>How long to wait before acting (polls and the one retry); zero otherwise.</summary>
        public TimeSpan After { get; }

        /// <summary>For <see cref="DeployActionKind.Failed"/>: a <see cref="DeployFailure"/> code.</summary>
        public string Reason { get; }

        /// <summary>A sentence for the developer (failures and waits), or null.</summary>
        public string Message { get; }

        public static DeployAction Of(DeployActionKind kind, TimeSpan after = default) => new DeployAction(kind, after, null, null);

        public static DeployAction Wait(DeployActionKind kind, TimeSpan after, string message) => new DeployAction(kind, after, null, message);

        public static DeployAction Failed(string reason, string message) => new DeployAction(DeployActionKind.Failed, TimeSpan.Zero, reason, message);

        public override string ToString()
        {
            string text = Kind.ToString();
            if (After > TimeSpan.Zero)
            {
                text += $"(after {After.TotalSeconds:0} s)";
            }

            return Reason == null ? text : text + $"({Reason})";
        }
    }

    /// <summary>The failure codes of <see cref="DeployActionKind.Failed"/>.</summary>
    public static class DeployFailure
    {
        public const string InvalidRequest = "invalid_request";
        public const string BuildFailed = "build_failed";
        public const string NoBuild = "no_build";

        /// <summary>The build has no file of the name the game's startup command launches.</summary>
        public const string StartupExecutableMissing = "startup_executable_missing";
        public const string PushFailed = "push_failed";
        public const string SnapshotUnknown = "snapshot_unknown";
        public const string TargetsFailed = "build_targets_failed";
        public const string SnapshotNotListed = "snapshot_not_listed";
        public const string DataSourceMismatch = "data_source_mismatch";
        public const string AlreadyPinned = "already_pinned";
        public const string ReleaseRefused = "release_refused";
        public const string ReleaseOutcomeUnknown = "release_outcome_unknown";
        public const string ReleaseFailed = "release_failed";
        public const string ReleaseEnded = "release_ended";
        public const string PollFailed = "poll_failed";
        public const string Cancelled = "cancelled";

        // The release endpoint's refusal reasons, passed through as they are.
        public const string ReleaseInProgress = "release_in_progress";
        public const string SnapshotNotFound = "snapshot_not_found";
        public const string CdnUnreadable = "cdn_unreadable";
        public const string TooManyPins = "too_many_pins";
        public const string ServerUnreachable = "server_unreachable";
        public const string SupervisorTooOld = "supervisor_too_old";
    }

    /// <summary>
    /// What happened when the runner carried out an action; the planner folds it into the state.
    /// Only the fields of the action's kind are read.
    /// </summary>
    public sealed class StepResult
    {
        public StepResult(DeployActionKind kind, PluginError error)
        {
            Kind = kind;
            Error = error;
        }

        /// <summary>The action this answers.</summary>
        public DeployActionKind Kind { get; }

        /// <summary>Null on success.</summary>
        public PluginError Error { get; }

        public bool Ok => Error == null;

        /// <summary>Build: the project-relative output folder.</summary>
        public string BuildFolder { get; set; }

        /// <summary>Push: the snapshot pingctl printed (null when it printed none).</summary>
        public string Snapshot { get; set; }

        /// <summary>Push: true for a new snapshot, false for "version stays", null when unknown.</summary>
        public bool? SnapshotChanged { get; set; }

        /// <summary>Read build targets: the answer.</summary>
        public BuildTargetsResponse BuildTargets { get; set; }

        /// <summary>Start release: the created release.</summary>
        public ReleaseView CreatedRelease { get; set; }

        /// <summary>Poll: the release and its per-location rows.</summary>
        public ReleaseDetailResponse ReleaseDetail { get; set; }

        /// <summary>When the step ended.</summary>
        public DateTime AtUtc { get; set; }

        public static StepResult Success(DeployActionKind kind, DateTime atUtc) => new StepResult(kind, null) { AtUtc = atUtc };

        public static StepResult Failure(DeployActionKind kind, PluginError error, DateTime atUtc) => new StepResult(kind, error) { AtUtc = atUtc };
    }

    /// <summary>The planner's whole answer: the next action and the state to persist before it runs.</summary>
    public sealed class DeployPlan
    {
        public DeployPlan(DeployAction action, DeployState state)
        {
            Action = action;
            State = state;
        }

        public DeployAction Action { get; }

        public DeployState State { get; }

        /// <summary>Lines worth showing in the log pane for this decision (no secrets: versions, ids, states).</summary>
        public List<string> Notes { get; } = new List<string>();
    }
}
