namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>A saved run the Ship section can offer to continue.</summary>
    public sealed class ResumeOffer
    {
        public ResumeOffer(string message, long fleetId, long releaseId, string buildVersion)
        {
            Message = message;
            FleetId = fleetId;
            ReleaseId = releaseId;
            BuildVersion = buildVersion;
        }

        /// <summary>The question, for example "Release 12 on fleet 1 is still running; resume watching?".</summary>
        public string Message { get; }

        public long FleetId { get; }

        /// <summary>The release being watched, or 0.</summary>
        public long ReleaseId { get; }

        public string BuildVersion { get; }
    }

    /// <summary>
    /// The resume offer. A saved run never continues by itself: the state file outlives the Editor, so a run
    /// a crash or a restart left behind waits for the developer's Continue. Each Ship row runs one step, so
    /// nothing is handed over across the domain reload that follows a build.
    /// </summary>
    public sealed partial class PipelineRunner
    {
        /// <summary>The saved run that can be continued, or null.</summary>
        public ResumeOffer ResumeOffer => IsRunning ? null : Offer(State);

        /// <summary>The saved run's offer, or null: a run that is neither done nor failed, or a release that may still be moving.</summary>
        public static ResumeOffer Offer(DeployState s)
        {
            if (s == null || s.Phase == DeployPhase.Done)
            {
                return null;
            }

            if (s.ReleaseUnfinished && (s.Phase == DeployPhase.Running || s.Failure == DeployFailure.PollFailed))
            {
                return new ResumeOffer($"Release {s.ReleaseId} on fleet {s.FleetId} ({s.Snapshot}) is still running; resume watching?", s.FleetId, s.ReleaseId, s.BuildVersion);
            }

            if (s.Phase == DeployPhase.NeedsInput)
            {
                return new ResumeOffer($"The push of {s.BuildVersion} is waiting for a push token for CDN source {s.CdnSourceId}; continue once it is issued?", s.FleetId, 0, s.BuildVersion);
            }

            if (s.Phase == DeployPhase.Running)
            {
                string where = s.InFlight != null ? $"during {PipelineSteps.StepOf(s.InFlight.Value)}" : "between steps";
                return new ResumeOffer($"The run of {s.BuildVersion} stopped {where}; continue it?", s.FleetId, s.ReleaseId, s.BuildVersion);
            }

            return null;
        }

        /// <summary>
        /// The saved state as a continue starts it. A watch that gave up on poll failures runs again (the
        /// release carried on). The build-targets lag clock starts again: a first check recorded before a long
        /// stop or an Editor restart would otherwise fail the lag limit at once.
        /// </summary>
        internal static DeployState Resumed(DeployState saved)
        {
            DeployState s = saved.Clone();
            if (s.Phase == DeployPhase.Failed)
            {
                s.Phase = DeployPhase.Running;
                s.Failure = null;
                s.FailureMessage = null;
                s.PollFailures = 0;
            }

            if (!s.TargetsConfirmed)
            {
                s.TargetsFirstCheckedUtc = null;
            }

            return s;
        }
    }
}
