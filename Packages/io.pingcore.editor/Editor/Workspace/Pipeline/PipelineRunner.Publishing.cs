using System;
using System.Collections.Generic;
using System.IO;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Redaction;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>How a call to the runner ended.</summary>
    public sealed class PipelineOutcome
    {
        public PipelineOutcome(PipelineRunStatus status, string failure, string message, bool refused)
        {
            Status = status;
            Failure = failure;
            Message = message;
            Refused = refused;
        }

        public PipelineRunStatus Status { get; }

        /// <summary>A <see cref="DeployFailure"/> code when it failed, else null.</summary>
        public string Failure { get; }

        /// <summary>One redacted sentence for the developer.</summary>
        public string Message { get; }

        /// <summary>True when the call was refused without doing anything (already running, nothing to continue, an unfinished release).</summary>
        public bool Refused { get; }
    }

    /// <summary>The step rows, the redacted log, the saved state and the outcome of each call.</summary>
    public sealed partial class PipelineRunner
    {
        private PipelineOutcome Stopped(DeployActionKind? interrupted)
        {
            // A stop never fails the run: the state stays resumable. Only an interrupted release request keeps its in-flight
            // mark, so a continue reports its unknown outcome instead of sending it again.
            DeployState s = State.Clone();
            if (interrupted != DeployActionKind.StartRelease)
            {
                s.InFlight = null;
            }

            s.Phase = DeployPhase.Running;
            State = s;
            Save(State);
            Log($"deploy {State.RunId}: stopped by you" + (State.ReleaseUnfinished ? $"; release {State.ReleaseId} carries on, resume watching it later" : string.Empty));
            PublishSteps(true);
            SetStatus(PipelineRunStatus.Cancelled);
            return new PipelineOutcome(PipelineRunStatus.Cancelled, DeployFailure.Cancelled, "Stopped.", false);
        }

        private PipelineOutcome Finish(PipelineRunStatus status, string failure, string message)
        {
            SetStatus(status);
            return new PipelineOutcome(status, failure, message, false);
        }

        private PipelineOutcome Busy()
        {
            return new PipelineOutcome(RunStatus, null, $"The deploy is already running ({runningStep ?? "between steps"}); wait for it or stop it first.", true);
        }

        private void Save(DeployState s)
        {
            try
            {
                DeployStateFile.Save(services.ProjectRoot, s);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
            {
                Log($"the deploy state could not be saved ({e.GetType().Name}); a reload now would lose this run's place");
            }
        }

        private void Log(string line)
        {
            if (!string.IsNullOrEmpty(line))
            {
                LogLine?.Invoke(Redactor.PatternsOnly.Redact(line));
            }
        }

        private void SetStatus(PipelineRunStatus status)
        {
            if (RunStatus != status)
            {
                RunStatus = status;
                RunStatusChanged?.Invoke(status);
            }
        }

        private void PublishSteps(bool stopped, bool quiet = false)
        {
            DateTime now = services.UtcNow();
            foreach ((PipelineStep step, PipelineStepStatus status, string detail) in PipelineSteps.Derive(State, lastAction, lastStep, stopped))
            {
                steps.TryGetValue(step, out PipelineStepState old);
                DateTime? started = old?.StartedUtc;
                DateTime? finished = old?.FinishedUtc;
                if (status == PipelineStepStatus.Running && (started == null || finished != null))
                {
                    started = old?.Status == PipelineStepStatus.Running ? started : now;
                    finished = null;
                }
                else if ((status == PipelineStepStatus.Succeeded || status == PipelineStepStatus.Failed || status == PipelineStepStatus.Cancelled) && finished == null)
                {
                    finished = started == null ? (DateTime?)null : now;
                }
                else if (status == PipelineStepStatus.Pending || status == PipelineStepStatus.Skipped)
                {
                    started = null;
                    finished = null;
                }

                PluginError error = status == PipelineStepStatus.Failed || status == PipelineStepStatus.NeedsInput
                    ? new PluginError(step.ToString(), status == PipelineStepStatus.NeedsInput ? PluginErrorKind.NotSignedIn : PluginErrorKind.Refused, detail, null) { Reason = State?.Failure }
                    : null;
                var row = new PipelineStepState(step, status, Redactor.PatternsOnly.Redact(detail), started, finished, error);
                bool changed = old == null || old.Status != row.Status || old.Detail != row.Detail || old.StartedUtc != row.StartedUtc || old.FinishedUtc != row.FinishedUtc;
                steps[step] = row;
                if (changed && !quiet)
                {
                    StepChanged?.Invoke(row);
                }
            }
        }

        private static PipelineRunStatus StatusOf(DeployState s)
        {
            if (s == null)
            {
                return PipelineRunStatus.Idle;
            }

            switch (s.Phase)
            {
                case DeployPhase.Done:
                    return PipelineRunStatus.Succeeded;
                case DeployPhase.Failed:
                    return s.Failure == DeployFailure.Cancelled ? PipelineRunStatus.Cancelled : PipelineRunStatus.Failed;
                case DeployPhase.NeedsInput:
                    return PipelineRunStatus.NeedsInput;
                default:
                    return PipelineRunStatus.CanResume;
            }
        }

        private static DeployRequest Frozen(DeployRequest request, DeployState s)
        {
            return new DeployRequest
            {
                Build = s.WantBuild,
                Push = s.WantPush,
                Release = s.WantRelease,
                BuildVersion = s.BuildVersion,
                FleetId = s.FleetId,
                CdnSourceId = s.CdnSourceId,
                ForceRelease = s.ForceRelease,
                PushFolder = s.BuildFolder ?? request.PushFolder,
                BuildProfile = request.BuildProfile,
                BuildExecutable = request.BuildExecutable,
                Startup = s.Startup ?? request.Startup,
                StartupCheckSkipped = s.StartupCheckSkipped ?? request.StartupCheckSkipped,
            };
        }

        private static string Describe(DeployRequest r)
        {
            var parts = new List<string>();
            if (r.Build)
            {
                parts.Add($"build ({r.BuildProfile})");
            }

            if (r.Push)
            {
                parts.Add($"push (CDN source {r.CdnSourceId})");
            }

            if (r.Release)
            {
                parts.Add($"release onto fleet {r.FleetId}" + (r.ForceRelease ? " (forced)" : string.Empty));
            }

            return string.Join(", ", parts) + $" at {r.BuildVersion}";
        }

        private static string DoneMessage(DeployState s)
        {
            if (s.WantRelease)
            {
                return $"Release {s.ReleaseId} of {s.Snapshot} onto fleet {s.FleetId} completed.";
            }

            return s.WantPush ? $"Pushed snapshot {s.Snapshot}." : $"Built {s.BuildFolder}.";
        }
    }
}
