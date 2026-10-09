using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.UI.Common;

namespace PingCore.Editor.Workspace.UI.Ship
{
    /// <summary>
    /// The pipeline as the Ship section drives it: the events of <see cref="PipelineRunner"/>
    /// (<see cref="IPipelineEvents"/>) and its commands. <see cref="PipelineRunnerCommands"/> is
    /// the real one; <see cref="UnavailableDeployCommands"/> answers every command with the reason
    /// the pipeline cannot run yet (not signed in, a malformed settings file).
    /// </summary>
    public interface IDeployCommands
    {
        /// <summary>True when commands can run (signed in).</summary>
        bool Available { get; }

        /// <summary>Why <see cref="Available"/> is false, or null.</summary>
        string UnavailableReason { get; }

        /// <summary>The pipeline's events; raised on the main thread, every text already redacted.</summary>
        IPipelineEvents Events { get; }

        /// <summary>The saved run the window can offer to continue, or null.</summary>
        ResumeOffer ResumeOffer { get; }

        /// <summary>The saved state of the current or last run, or null (the last push's snapshot is what Release releases).</summary>
        DeployState State { get; }

        /// <summary>The push token choices, or null when unavailable.</summary>
        PushTokenFlow PushTokens { get; }

        /// <summary>The signed-in client, or null.</summary>
        IPingCoreApi Api { get; }

        /// <summary>What would refuse <paramref name="request"/> (a push) before a token is issued; null when it can go ahead.</summary>
        string CheckPush(DeployRequest request);

        /// <summary>Starts a new run.</summary>
        Task<PipelineOutcome> RunAsync(DeployRequest request, CancellationToken cancellationToken);

        /// <summary>Continues the saved run.</summary>
        Task<PipelineOutcome> ContinueAsync(DeployRequest request, CancellationToken cancellationToken);

        /// <summary>Stops the running step; the saved state stays resumable.</summary>
        void Stop();

        /// <summary>Forgets the saved run. False while running.</summary>
        bool Discard();

        /// <summary>Cancels the run's release. Null on success.</summary>
        Task<PluginError> CancelReleaseAsync(CancellationToken cancellationToken);

        /// <summary>Acknowledges the run's failed release. Null on success.</summary>
        Task<PluginError> AcknowledgeReleaseAsync(CancellationToken cancellationToken);
    }

    /// <summary>The real commands: a <see cref="PipelineRunner"/> for the signed-in workspace.</summary>
    public sealed class PipelineRunnerCommands : IDeployCommands
    {
        private readonly PipelineRunner runner;

        public PipelineRunnerCommands(PipelineRunner runner, IPingCoreApi api)
        {
            this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
            Api = api ?? throw new ArgumentNullException(nameof(api));
        }

        public bool Available => true;

        public string UnavailableReason => null;

        public IPipelineEvents Events => runner;

        public ResumeOffer ResumeOffer => runner.ResumeOffer;

        public DeployState State => runner.State;

        public PushTokenFlow PushTokens => runner.PushTokens;

        public IPingCoreApi Api { get; }

        public string CheckPush(DeployRequest request) => runner.CheckPush(request);

        public Task<PipelineOutcome> RunAsync(DeployRequest request, CancellationToken cancellationToken) => runner.RunAsync(request, cancellationToken);

        public Task<PipelineOutcome> ContinueAsync(DeployRequest request, CancellationToken cancellationToken) => runner.ContinueAsync(request, cancellationToken);

        public void Stop() => runner.Stop();

        public bool Discard() => runner.Discard();

        public Task<PluginError> CancelReleaseAsync(CancellationToken cancellationToken) => runner.CancelReleaseAsync(cancellationToken);

        public Task<PluginError> AcknowledgeReleaseAsync(CancellationToken cancellationToken) => runner.AcknowledgeReleaseAsync(cancellationToken);
    }

    /// <summary>Commands that cannot run yet: every one answers with <see cref="UnavailableReason"/> and calls nothing.</summary>
    public sealed class UnavailableDeployCommands : IDeployCommands, IPipelineEvents
    {
        public UnavailableDeployCommands(string reason)
        {
            UnavailableReason = string.IsNullOrEmpty(reason) ? "Sign in first: " + PingCoreMenu.SignInText + "." : reason;
        }

#pragma warning disable CS0067 // never raised: nothing runs
        public event Action<PipelineStepState> StepChanged;

        public event Action<PipelineRunStatus> RunStatusChanged;

        public event Action<string> LogLine;

        public event Action<ReleaseProgressView> ReleaseProgressChanged;
#pragma warning restore CS0067

        public bool Available => false;

        public string UnavailableReason { get; }

        public IPipelineEvents Events => this;

        public IReadOnlyList<PipelineStepState> Steps { get; } = Array.Empty<PipelineStepState>();

        public PipelineRunStatus RunStatus => PipelineRunStatus.Idle;

        public ReleaseProgressView ReleaseProgress => null;

        public ResumeOffer ResumeOffer => null;

        public DeployState State => null;

        public PushTokenFlow PushTokens => null;

        public IPingCoreApi Api => null;

        public string CheckPush(DeployRequest request) => UnavailableReason;

        public Task<PipelineOutcome> RunAsync(DeployRequest request, CancellationToken cancellationToken) => Task.FromResult(Refused());

        public Task<PipelineOutcome> ContinueAsync(DeployRequest request, CancellationToken cancellationToken) => Task.FromResult(Refused());

        public void Stop()
        {
        }

        public bool Discard() => false;

        public Task<PluginError> CancelReleaseAsync(CancellationToken cancellationToken)
            => Task.FromResult(new PluginError("release", PluginErrorKind.NotSignedIn, UnavailableReason, null));

        public Task<PluginError> AcknowledgeReleaseAsync(CancellationToken cancellationToken)
            => Task.FromResult(new PluginError("release", PluginErrorKind.NotSignedIn, UnavailableReason, null));

        private PipelineOutcome Refused() => new PipelineOutcome(PipelineRunStatus.Idle, null, UnavailableReason, true);
    }

    /// <summary>
    /// Where the Ship section gets its commands: <see cref="Factory"/> when set (tests, another
    /// host), else a <see cref="PipelineRunner"/> for the signed-in workspace of the open project,
    /// else <see cref="UnavailableDeployCommands"/> with the reason.
    /// </summary>
    public static class DeployCommandsRegistry
    {
        /// <summary>Overrides how commands are built; null for the default.</summary>
        public static Func<IDeployCommands> Factory { get; set; }

        /// <summary>Commands for the open project.</summary>
        public static IDeployCommands Create()
        {
            if (Factory != null)
            {
                return Factory();
            }

            IPingCoreApi api = WorkspaceContext.SignedInApi(out string problem);
            if (api == null)
            {
                return new UnavailableDeployCommands(problem);
            }

            // The pingctl path is read at each use, so a changed "Your own pingctl" needs no new commands; a new sign-in
            // (another host or key store) does, and ShipSection.EnsureCommands rebuilds them for it.
            try
            {
                var runner = new PipelineRunner(PipelineServices.ForEditor(WorkspaceContext.ProjectRoot, api, WorkspaceContext.PushTokenStore(),
                    () => WorkspaceContext.LoadUser(out _).PingctlPath));
                return new PipelineRunnerCommands(runner, api);
            }
            catch (ArgumentException e)
            {
                return new UnavailableDeployCommands("The deploy pipeline could not start: " + e.Message);
            }
        }
    }
}
