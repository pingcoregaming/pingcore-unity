using System;
using System.Collections.Generic;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>
    /// The deploy pipeline's decisions, pure: given the request, the persisted state and the
    /// result of the last action, it answers the next action and the state to persist. The
    /// release rules are the ones proved live against PingCore:
    /// <list type="bullet">
    /// <item>the release targets the snapshot pingctl printed, never an assumed name;</item>
    /// <item>the fleet must deliver its game files from a CDN source (<see cref="DataSourceRule"/>), and its build targets must list the snapshot (<c>cdnCurrentVersion</c>, or the targets list) before a release, polled every 5 s for up to 90 s of publish lag;</item>
    /// <item>a release onto the version the fleet is already pinned to is refused unless forced;</item>
    /// <item>a release is never retried, except once after 30 s when the pre-check found a game server unreachable; a request whose answer never arrived is reported, not resent;</item>
    /// <item>the release is polled every 5 s; a failed release is acknowledged so it no longer holds scale-down, and the pipeline stops there.</item>
    /// </list>
    /// The planner never mutates its inputs and never reads a clock: <paramref name="nowUtc"/> comes in.
    /// This file decides the next action; <c>DeployPlanner.Fold.cs</c> folds each step's result into the state.
    /// </summary>
    public static partial class DeployPlanner
    {
        /// <summary>How long build targets may lag a push before the release gives up.</summary>
        public static readonly TimeSpan TargetsLagLimit = TimeSpan.FromSeconds(90);

        /// <summary>How often the build targets are re-read while they lag.</summary>
        public static readonly TimeSpan TargetsPollInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The next action. <paramref name="lastResult"/> answers the action the previous call
        /// returned (null on the first call and on a resume after a reload or restart).
        /// </summary>
        public static DeployPlan Next(DeployRequest request, DeployState state, StepResult lastResult, DateTime nowUtc)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            DeployState s = state.Clone();
            s.UpdatedUtc = nowUtc;
            var notes = new List<string>();

            if (s.Phase == DeployPhase.Done || s.Phase == DeployPhase.Failed)
            {
                return Plan(s.Phase == DeployPhase.Done ? DeployAction.Of(DeployActionKind.Done) : DeployAction.Failed(s.Failure, s.FailureMessage), s, notes);
            }

            if (lastResult != null)
            {
                s.InFlight = null;
                DeployAction folded = Fold(request, s, lastResult, nowUtc, notes);
                if (folded != null)
                {
                    return Settle(s, folded, notes);
                }
            }
            else if (s.InFlight != null)
            {
                DeployActionKind interrupted = s.InFlight.Value;
                s.InFlight = null;
                if (interrupted == DeployActionKind.StartRelease)
                {
                    // The request may have reached the workspace. A release is never sent twice on a guess.
                    return Settle(s, DeployAction.Failed(DeployFailure.ReleaseOutcomeUnknown,
                        "The Editor stopped while the release request was being sent, so it is not known whether the release started. Check the fleet's releases before releasing again."), notes);
                }

                notes.Add($"resuming: the Editor stopped during {interrupted}; it runs again");
            }

            return Settle(s, Decide(request, s, notes), notes);
        }

        /// <summary>The state to persist just before <paramref name="action"/> starts (after any wait). Pure.</summary>
        public static DeployState Begin(DeployState state, DeployAction action, DateTime nowUtc)
        {
            DeployState s = state.Clone();
            s.UpdatedUtc = nowUtc;
            s.InFlight = IsExecutable(action.Kind) ? action.Kind : (DeployActionKind?)null;
            return s;
        }

        /// <summary>True for an action the runner carries out (everything but a stop).</summary>
        public static bool IsExecutable(DeployActionKind kind)
        {
            return kind != DeployActionKind.Done && kind != DeployActionKind.Failed && kind != DeployActionKind.IssuePushTokenNeeded;
        }

        /// <summary>The project-relative folder a plain server build of <paramref name="version"/> lands in.</summary>
        public static string BuildFolderFor(string version) => Cli.BuildServerArgs.ServerOutputRoot + "/" + version;

        private static DeployAction Decide(DeployRequest request, DeployState s, List<string> notes)
        {
            string problem = request.Problem();
            if (problem != null)
            {
                return DeployAction.Failed(DeployFailure.InvalidRequest, problem);
            }

            if (s.WantBuild && !s.Built)
            {
                return DeployAction.Of(DeployActionKind.Build);
            }

            if (s.BuildFolder == null && s.WantBuild)
            {
                s.BuildFolder = BuildFolderFor(s.BuildVersion);
            }

            if (s.WantPush && !s.Pushed)
            {
                if (string.IsNullOrEmpty(s.BuildFolder))
                {
                    return DeployAction.Failed(DeployFailure.NoBuild, "Choose the folder to push, or build first.");
                }

                return request.PushTokenStored
                    ? DeployAction.Of(DeployActionKind.Push)
                    : DeployAction.Wait(DeployActionKind.IssuePushTokenNeeded, TimeSpan.Zero,
                        $"No push token is stored for CDN source {s.CdnSourceId}. Issue one: it is kept in your credential store and never shown.");
            }

            if (!s.WantRelease)
            {
                return DeployAction.Of(DeployActionKind.Done);
            }

            if (s.ReleaseId > 0)
            {
                return DeployAction.Of(DeployActionKind.PollRelease);
            }

            if (s.Snapshot == null)
            {
                // A release-only run releases the version the developer named.
                s.Snapshot = s.BuildVersion;
            }

            if (!s.TargetsConfirmed)
            {
                return DeployAction.Of(DeployActionKind.ReadBuildTargets);
            }

            if (!s.ForceRelease && string.Equals(s.PinnedVersion, s.Snapshot, StringComparison.Ordinal))
            {
                return DeployAction.Failed(DeployFailure.AlreadyPinned,
                    $"Fleet {s.FleetId} is already pinned to {s.Snapshot}; nothing to release. Release it again only on purpose (force).");
            }

            return DeployAction.Of(DeployActionKind.StartRelease);
        }

        private static DeployPlan Settle(DeployState s, DeployAction action, List<string> notes)
        {
            switch (action.Kind)
            {
                case DeployActionKind.Done:
                    s.Phase = DeployPhase.Done;
                    break;
                case DeployActionKind.Failed:
                    s.Phase = DeployPhase.Failed;
                    s.Failure = action.Reason;
                    s.FailureMessage = action.Message;
                    break;
                case DeployActionKind.IssuePushTokenNeeded:
                    s.Phase = DeployPhase.NeedsInput;
                    break;
                default:
                    s.Phase = DeployPhase.Running;
                    break;
            }

            return Plan(action, s, notes);
        }

        private static DeployPlan Plan(DeployAction action, DeployState s, List<string> notes)
        {
            var plan = new DeployPlan(action, s);
            plan.Notes.AddRange(notes);
            return plan;
        }
    }
}
