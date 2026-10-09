using System;
using System.Collections.Generic;
using PingCore.Editor.Workspace.Api;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>The steps a Ship row runs, in order.</summary>
    public enum PipelineStep
    {
        /// <summary>The in-process Linux Dedicated Server build through the build profile (<c>ServerBuilder</c>).</summary>
        Build,

        /// <summary>A push token for the CDN source is stored; waits for the developer to issue one when none is.</summary>
        PushToken,

        /// <summary>The startup command check, then <c>pingctl push</c> to the fleet's CDN source.</summary>
        Push,

        /// <summary><c>GET fleets/{id}/build-targets</c> lists the new build (polled for publish lag).</summary>
        BuildTargets,

        /// <summary><c>POST fleets/{id}/releases</c>. Never retried automatically, except once on <c>server_unreachable</c>.</summary>
        Release,

        /// <summary>Polling the release every 5 s with per-location progress.</summary>
        Watch,

        /// <summary>Acknowledging a failed release so it no longer holds scale-down.</summary>
        Acknowledge,
    }

    /// <summary>Where one step stands.</summary>
    public enum PipelineStepStatus
    {
        /// <summary>Will run in this pipeline, has not started.</summary>
        Pending,

        /// <summary>Running now.</summary>
        Running,

        /// <summary>Stopped until the developer acts (for example: provide a push token).</summary>
        NeedsInput,

        Succeeded,

        Failed,

        /// <summary>Not part of this pipeline (for example Build when only Release was asked for).</summary>
        Skipped,

        /// <summary>Stopped by the developer.</summary>
        Cancelled,
    }

    /// <summary>The whole pipeline's state, for the window's header and buttons.</summary>
    public enum PipelineRunStatus
    {
        /// <summary>Nothing running and nothing to resume.</summary>
        Idle,

        Running,

        /// <summary>Stopped at a step that needs the developer (<see cref="PipelineStepStatus.NeedsInput"/>).</summary>
        NeedsInput,

        /// <summary>A release from an earlier session is still unfinished; <see cref="PipelineRunner.ResumeOffer"/> says which.</summary>
        CanResume,

        Succeeded,

        Failed,

        Cancelled,
    }

    /// <summary>One row of the step list. Immutable; every change publishes a new one.</summary>
    public sealed class PipelineStepState
    {
        public PipelineStepState(PipelineStep step, PipelineStepStatus status, string detail, DateTime? startedUtc, DateTime? finishedUtc, PluginError error)
        {
            Step = step;
            Status = status;
            Detail = detail ?? string.Empty;
            StartedUtc = startedUtc;
            FinishedUtc = finishedUtc;
            Error = error;
        }

        public PipelineStep Step { get; }

        public PipelineStepStatus Status { get; }

        /// <summary>One redacted line: what the step is doing or did (<c>snapshot 2026.10.07-editor1 (new)</c>).</summary>
        public string Detail { get; }

        public DateTime? StartedUtc { get; }

        public DateTime? FinishedUtc { get; }

        /// <summary>Why it failed or needs input; null otherwise. Already redacted.</summary>
        public PluginError Error { get; }

        /// <summary>Elapsed time at <paramref name="nowUtc"/> (frozen once finished); zero before it started.</summary>
        public TimeSpan Elapsed(DateTime nowUtc)
        {
            if (StartedUtc == null)
            {
                return TimeSpan.Zero;
            }

            DateTime end = FinishedUtc ?? nowUtc;
            return end > StartedUtc.Value ? end - StartedUtc.Value : TimeSpan.Zero;
        }

        public PipelineStepState With(PipelineStepStatus status, string detail, DateTime? startedUtc, DateTime? finishedUtc, PluginError error)
            => new PipelineStepState(Step, status, detail, startedUtc, finishedUtc, error);

        public override string ToString() => $"{Step}: {Status}{(Detail.Length > 0 ? " - " + Detail : string.Empty)}";
    }

    /// <summary>A release's progress at one member deployment (location), from the release detail.</summary>
    public sealed class ReleaseLocationProgress
    {
        public long BrandDeploymentId { get; set; }

        /// <summary><c>pending</c>, <c>surging</c>, <c>rolling</c>, <c>completing</c>, <c>done</c> or the handler's other words.</summary>
        public string Phase { get; set; }

        public int OldCount { get; set; }

        public int NewCount { get; set; }

        public int RetiringCount { get; set; }

        /// <summary>Why this location is not moving, or null.</summary>
        public string Blocked { get; set; }

        public bool Parked { get; set; }

        /// <summary>One line, for example <c>deployment 21: rolling, 1 old, 1 new, 0 retiring</c>.</summary>
        public override string ToString()
        {
            string line = $"deployment {BrandDeploymentId}: {Phase ?? "unknown"}, {OldCount} old, {NewCount} new, {RetiringCount} retiring";
            if (Parked)
            {
                line += ", parked";
            }

            return string.IsNullOrEmpty(Blocked) ? line : line + " (blocked: " + Blocked + ")";
        }
    }

    /// <summary>The release the pipeline is watching.</summary>
    public sealed class ReleaseProgressView
    {
        public ReleaseProgressView(long fleetId, long releaseId, string targetBuildVersion, string state, string blocked, IReadOnlyList<ReleaseLocationProgress> locations)
        {
            FleetId = fleetId;
            ReleaseId = releaseId;
            TargetBuildVersion = targetBuildVersion;
            State = state;
            Blocked = blocked;
            Locations = locations ?? Array.Empty<ReleaseLocationProgress>();
        }

        public long FleetId { get; }

        public long ReleaseId { get; }

        public string TargetBuildVersion { get; }

        /// <summary>The release's <c>state</c>.</summary>
        public string State { get; }

        /// <summary>Why a pending release has not started, or null.</summary>
        public string Blocked { get; }

        public IReadOnlyList<ReleaseLocationProgress> Locations { get; }
    }

    /// <summary>
    /// What the Ship section binds to. Every event fires on the Unity main thread (the runner is
    /// driven from it and its awaits return there). Every text is already redacted.
    /// </summary>
    public interface IPipelineEvents
    {
        /// <summary>A step row changed.</summary>
        event Action<PipelineStepState> StepChanged;

        /// <summary>The pipeline as a whole changed state.</summary>
        event Action<PipelineRunStatus> RunStatusChanged;

        /// <summary>A line for the log pane (child output, decisions, API errors), redacted.</summary>
        event Action<string> LogLine;

        /// <summary>A release poll answered.</summary>
        event Action<ReleaseProgressView> ReleaseProgressChanged;

        /// <summary>The current row of every step, in <see cref="PipelineStep"/> order.</summary>
        IReadOnlyList<PipelineStepState> Steps { get; }

        PipelineRunStatus RunStatus { get; }

        /// <summary>The last release progress seen, or null.</summary>
        ReleaseProgressView ReleaseProgress { get; }
    }
}
