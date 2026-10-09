using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Credentials;
using PingCore.Editor.Workspace.Redaction;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>The in-process build through the build profile, and the push (the checks, then pingctl with the push token in its environment).</summary>
    public sealed partial class PipelineRunner
    {
        /// <summary>
        /// What would refuse a push of a chosen folder before anything is issued or run: a run already going or a release
        /// that may still be moving, the request itself, the folder check (the file the startup command launches, the
        /// build guard's verdict, newer files) and a usable pingctl.
        /// Null when the push can go ahead. The Ship section asks this before it offers to issue a push token, so a push
        /// that would be refused never replaces another holder's token. The same checks run again in the step itself.
        /// </summary>
        public string CheckPush(DeployRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (IsRunning)
            {
                return Busy().Message;
            }

            string problem = UnfinishedReleaseRefusal() ?? request.Problem();
            if (problem != null || !request.Push)
            {
                return problem;
            }

            if (!request.Build)
            {
                string folder = Path.GetFullPath(Path.Combine(services.ProjectRoot, request.PushFolder));
                BuildFolderVerdict verdict = services.CheckBuildFolder?.Invoke(services.ProjectRoot, folder, request.Startup) ?? BuildFolderVerdict.Pass;
                if (!verdict.Ok)
                {
                    return string.Join(" ", verdict.Problems);
                }
            }

            PingctlLocation location = services.LocatePingctl?.Invoke() ?? new PingctlLocation(null, null, "pingctl was not found.");
            return location.Found ? null : location.Problem;
        }

        private StepResult Build(DeployRequest request, DateTime now)
        {
            Log($"build: {request.BuildProfile} -> {DeployPlanner.BuildFolderFor(State.BuildVersion)}/{request.BuildExecutable}");
            ServerBuildOutcome outcome;
            try
            {
                outcome = services.Builder.Build(State.BuildVersion, request.BuildProfile, request.BuildExecutable);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                return StepResult.Failure(DeployActionKind.Build, new PluginError("build", PluginErrorKind.ChildFailed, $"The build threw {e.GetType().Name}; see the Console.", null), now);
            }

            if (outcome.RestoreMessage != null)
            {
                Log(outcome.RestoreMessage);
            }

            if (!outcome.Passed)
            {
                return StepResult.Failure(DeployActionKind.Build, new PluginError("build", PluginErrorKind.ChildFailed, Redactor.PatternsOnly.Redact(outcome.Problem ?? "The build failed; see the Console."), null), now);
            }

            return new StepResult(DeployActionKind.Build, null) { BuildFolder = outcome.OutputFolder };
        }

        private async Task<StepResult> PushAsync(DeployRequest request, DateTime now, CancellationToken ct)
        {
            if (!request.HasStartup && string.IsNullOrWhiteSpace(request.StartupCheckSkipped))
            {
                // The folder check skips the file check for a null executable; only a recorded reason may make it null.
                return StepResult.Failure(DeployActionKind.Push, new PluginError("push", PluginErrorKind.Refused, "The game's startup command is not known, so the build cannot be checked against it. " + StartupCommand.FixInPanel, null) { Reason = DeployFailure.StartupExecutableMissing }, now);
            }

            string folder = AbsoluteBuildFolder();
            BuildFolderVerdict verdict = services.CheckBuildFolder?.Invoke(services.ProjectRoot, folder, request.Startup) ?? BuildFolderVerdict.Pass;
            if (!verdict.Ok)
            {
                return StepResult.Failure(DeployActionKind.Push, new PluginError("push", PluginErrorKind.Refused, string.Join(" ", verdict.Problems), null) { Reason = verdict.Reason }, now);
            }

            PushFolderSummary summary = PushFolderSummary.Read(folder, request.ExcludeProduct);
            Log($"push: {summary.Describe()}");

            PingctlLocation location = services.LocatePingctl?.Invoke() ?? new PingctlLocation(null, null, "pingctl was not found.");
            if (!location.Found)
            {
                return StepResult.Failure(DeployActionKind.Push, new PluginError("push", PluginErrorKind.ToolMissing, location.Problem, PingctlLocator.GetPingctlHint), now);
            }

            string token;
            try
            {
                token = PushTokens.ReadForChild(State.CdnSourceId);
            }
            catch (CredentialStoreException e)
            {
                return StepResult.Failure(DeployActionKind.Push, new PluginError("push", PluginErrorKind.NotSignedIn, "The push token could not be read: " + Redactor.PatternsOnly.Redact(e.Message), null), now);
            }

            // The bundled binary runs as a private copy, checked against the manifest through a handle held until the
            // push is over, so the bytes checked are the bytes that run (PingctlRunCopy).
            PingctlPushResult pushed;
            using (PingctlRunCopy run = (services.PreparePingctl ?? (l => PingctlRunCopy.Prepare(services.ProjectRoot, l)))(location))
            {
                if (run == null || !run.Ok)
                {
                    return StepResult.Failure(DeployActionKind.Push, new PluginError("push", PluginErrorKind.ToolMissing, run?.Problem ?? "pingctl could not be prepared to run.", PingctlLocator.GetPingctlHint), now);
                }

                PluginError notRunnable = await EnsureRunnableAsync(run, ct);
                if (notRunnable != null)
                {
                    return StepResult.Failure(DeployActionKind.Push, notRunnable, now);
                }

                Log($"push: pingctl ({location.Source}{(run.Bundled ? ", checked run copy" : string.Empty)}) push to CDN source {State.CdnSourceId}");
                using (services.ReloadLock.Acquire())
                {
                    pushed = await new PingctlRunner(services.Processes, run.Path)
                        .PushAsync(folder, request.ExcludeProduct, token, services.Api.Endpoint, line => Log("  pingctl: " + line), ct);
                }
            }

            if (!pushed.Ok)
            {
                return StepResult.Failure(DeployActionKind.Push, pushed.Error, now);
            }

            return new StepResult(DeployActionKind.Push, null) { Snapshot = pushed.Snapshot, SnapshotChanged = pushed.Changed };
        }

        // A package unpacked on macOS or Linux can lose the binary's execute bit, and a copy need not keep it, so the
        // checked run copy of the bundled pingctl gets u+x before it runs; on Windows there is nothing to do.
        private async Task<PluginError> EnsureRunnableAsync(PingctlRunCopy run, CancellationToken ct)
        {
            if (Path.DirectorySeparatorChar == '\\' || !run.Bundled)
            {
                return null;
            }

            Process.ProcessResult chmod = await services.Processes.RunAsync(new Process.ProcessSpec
            {
                FileName = "/bin/chmod",
                Args = new[] { "u+x", run.Path },
                Timeout = TimeSpan.FromSeconds(10),
                Step = "push",
            }, ct);
            return chmod.Ok ? null : new PluginError("push", PluginErrorKind.ToolMissing, "The bundled pingctl could not be made executable (chmod failed).", PingctlLocator.GetPingctlHint);
        }

        private string AbsoluteBuildFolder() => Path.GetFullPath(Path.Combine(services.ProjectRoot, State.BuildFolder ?? DeployPlanner.BuildFolderFor(State.BuildVersion)));
    }
}
