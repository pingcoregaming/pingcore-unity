using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>
    /// Runs the deploy pipeline from the Unity main thread: asks <see cref="DeployPlanner"/> for the
    /// next action, persists the state (<see cref="DeployStateFile"/>) before and after every step,
    /// carries the action out (the in-process build through the build profile, pingctl with assembly
    /// reloads locked for the child's whole life and released in <c>finally</c>, the workspace calls), and
    /// publishes step rows, log lines and release progress through <see cref="IPipelineEvents"/>.
    /// A second start while a step runs is answered with the reason, never queued. A stop keeps the
    /// state resumable; a domain reload or an Editor restart is resumed through <see cref="ResumeOffer"/>.
    /// Each Ship row runs one step (build, push or release); a release that was still moving when the
    /// Editor stopped is offered again. Split by concern: this file drives the loop;
    /// <c>PipelineRunner.Resume.cs</c> holds the offer, <c>PipelineRunner.Push.cs</c> the build and the push,
    /// <c>PipelineRunner.Release.cs</c> the release calls, and <c>PipelineRunner.Publishing.cs</c> the step
    /// rows, the log and the outcomes.
    /// </summary>
    public sealed partial class PipelineRunner : IPipelineEvents
    {
        private readonly PipelineServices services;
        private readonly Dictionary<PipelineStep, PipelineStepState> steps = new Dictionary<PipelineStep, PipelineStepState>();
        private CancellationTokenSource stopSource;
        private DeployAction lastAction;
        private PipelineStep? lastStep;
        private string runningStep;

        public PipelineRunner(PipelineServices services)
        {
            this.services = services ?? throw new ArgumentNullException(nameof(services));
            if (string.IsNullOrEmpty(services.ProjectRoot) || services.Api == null || services.Store == null || services.Processes == null
                || services.Builder == null || services.ReloadLock == null)
            {
                throw new ArgumentException("PipelineServices needs the project root, the API client, the credential store, the process runner, the build step and the reload lock.", nameof(services));
            }

            DeployStateLoad loaded = DeployStateFile.Load(services.ProjectRoot);
            State = loaded.State;
            LoadProblem = loaded.Problem;
            RunStatus = Offer(State) != null ? PipelineRunStatus.CanResume : StatusOf(State);
            PublishSteps(false, quiet: true);
        }

        public event Action<PipelineStepState> StepChanged;

        public event Action<PipelineRunStatus> RunStatusChanged;

        public event Action<string> LogLine;

        public event Action<ReleaseProgressView> ReleaseProgressChanged;

        public IReadOnlyList<PipelineStepState> Steps => PipelineSteps.All.Select(s => steps[s]).ToList();

        public PipelineRunStatus RunStatus { get; private set; }

        public ReleaseProgressView ReleaseProgress { get; private set; }

        /// <summary>The persisted state of the current or last run, or null.</summary>
        public DeployState State { get; private set; }

        /// <summary>Why a saved state file was set aside on load, or null.</summary>
        public string LoadProblem { get; }

        /// <summary>True while a call is driving the pipeline.</summary>
        public bool IsRunning => stopSource != null;

        /// <summary>The push token choices for the configured source (the <see cref="PipelineStep.PushToken"/> step).</summary>
        public PushTokenFlow PushTokens => new PushTokenFlow(services.Store, services.Api.Endpoint.Host);

        /// <summary>Starts a new run. Refused while one runs, or while a saved release may still be moving (resume or discard it first).</summary>
        public Task<PipelineOutcome> RunAsync(DeployRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (IsRunning)
            {
                return Task.FromResult(Busy());
            }

            string unfinished = UnfinishedReleaseRefusal();
            if (unfinished != null)
            {
                return Task.FromResult(new PipelineOutcome(RunStatus, null, unfinished, true));
            }

            string problem = request.Problem();
            if (problem != null)
            {
                return Task.FromResult(new PipelineOutcome(RunStatus, DeployFailure.InvalidRequest, problem, true));
            }

            State = DeployState.Start(request, services.UtcNow(), services.NewRunId());
            lastAction = null;
            lastStep = null;
            ReleaseProgress = null;
            Save(State);
            Log($"deploy {State.RunId}: {Describe(request)}");
            return DriveAsync(Frozen(request, State), null, cancellationToken);
        }

        // A new run never starts over a release that may still be moving: it is watched or discarded first.
        private string UnfinishedReleaseRefusal()
        {
            return State != null && State.ReleaseUnfinished && State.Phase != DeployPhase.Failed
                ? $"Release {State.ReleaseId} on fleet {State.FleetId} may still be running. Resume watching it, or discard it, before starting another."
                : null;
        }

        /// <summary>
        /// Continues the saved run (after a push token was stored, a stop, a domain reload or an
        /// Editor restart). The saved run's version, fleet, source, folder and steps win over
        /// <paramref name="request"/>, which supplies the rest (the build profile, the executables).
        /// </summary>
        public Task<PipelineOutcome> ContinueAsync(DeployRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (IsRunning)
            {
                return Task.FromResult(Busy());
            }

            if (Offer(State) == null)
            {
                return Task.FromResult(new PipelineOutcome(RunStatus, null, "There is no unfinished deploy to continue.", true));
            }

            State = Resumed(State);
            Save(State);
            Log($"deploy {State.RunId}: continuing");
            return DriveAsync(Frozen(request, State), null, cancellationToken);
        }

        /// <summary>Stops the running step: a child is killed, a wait ends. The saved state stays resumable.</summary>
        public void Stop()
        {
            stopSource?.Cancel();
        }

        /// <summary>Forgets the saved run (a finished one, or one the developer gave up on). Refused while running.</summary>
        public bool Discard()
        {
            if (IsRunning)
            {
                return false;
            }

            DeployStateFile.Delete(services.ProjectRoot);
            State = null;
            ReleaseProgress = null;
            lastAction = null;
            lastStep = null;
            SetStatus(PipelineRunStatus.Idle);
            PublishSteps(false);
            return true;
        }

        private async Task<PipelineOutcome> DriveAsync(DeployRequest request, StepResult first, CancellationToken cancellationToken)
        {
            stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken ct = stopSource.Token;
            SetStatus(PipelineRunStatus.Running);
            StepResult last = first;
            try
            {
                while (true)
                {
                    request.PushTokenStored = PushTokens.Presence(State.CdnSourceId, out _) != PushTokenPresence.None; // an unreadable store fails at the read, never issues
                    DeployPlan plan = DeployPlanner.Next(request, State, last, services.UtcNow());
                    State = plan.State;
                    Save(State);
                    foreach (string note in plan.Notes)
                    {
                        Log(note);
                    }

                    DeployAction action = plan.Action;
                    lastAction = action;
                    PublishSteps(false);
                    switch (action.Kind)
                    {
                        case DeployActionKind.Done:
                            Log($"deploy {State.RunId}: done");
                            return Finish(PipelineRunStatus.Succeeded, null, DoneMessage(State));
                        case DeployActionKind.Failed:
                            Log($"deploy {State.RunId}: stopped: {action.Reason}: {action.Message}");
                            return Finish(action.Reason == DeployFailure.Cancelled ? PipelineRunStatus.Cancelled : PipelineRunStatus.Failed, action.Reason, action.Message);
                        case DeployActionKind.IssuePushTokenNeeded:
                            Log(action.Message);
                            return Finish(PipelineRunStatus.NeedsInput, null, action.Message);
                    }

                    if (action.Message != null)
                    {
                        Log(action.Message);
                    }

                    if (action.After > TimeSpan.Zero)
                    {
                        try
                        {
                            await services.Delay(action.After, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            return Stopped(null);
                        }
                    }

                    if (ct.IsCancellationRequested)
                    {
                        return Stopped(null);
                    }

                    State = DeployPlanner.Begin(State, action, services.UtcNow());
                    Save(State);
                    lastStep = PipelineSteps.StepOf(action.Kind);
                    last = await ExecuteAsync(action, request, ct);
                    last.AtUtc = services.UtcNow();
                    if (ct.IsCancellationRequested)
                    {
                        return Stopped(action.Kind);
                    }
                }
            }
            finally
            {
                stopSource.Dispose();
                stopSource = null;
                runningStep = null;
            }
        }

        private async Task<StepResult> ExecuteAsync(DeployAction action, DeployRequest request, CancellationToken ct)
        {
            DateTime now = services.UtcNow();
            runningStep = PipelineSteps.StepOf(action.Kind).ToString();
            switch (action.Kind)
            {
                case DeployActionKind.Build:
                    return Build(request, now);
                case DeployActionKind.Push:
                    return await PushAsync(request, now, ct);
                case DeployActionKind.ReadBuildTargets:
                case DeployActionKind.StartRelease:
                case DeployActionKind.PollRelease:
                case DeployActionKind.Acknowledge:
                    return await ExecuteReleaseStepAsync(action, now, ct);
                default:
                    throw new InvalidOperationException("not an executable action: " + action.Kind);
            }
        }
    }
}
