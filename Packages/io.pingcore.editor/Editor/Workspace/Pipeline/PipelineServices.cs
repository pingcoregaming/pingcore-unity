using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Build;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Credentials;
using PingCore.Editor.Workspace.Process;
using UnityEditor;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>How the in-process build step ended.</summary>
    public sealed class ServerBuildOutcome
    {
        public ServerBuildOutcome(bool passed, string outputFolder, string problem, string restoreMessage, bool reloadFollows)
        {
            Passed = passed;
            OutputFolder = outputFolder;
            Problem = problem;
            RestoreMessage = restoreMessage;
            ReloadFollows = reloadFollows;
        }

        public bool Passed { get; }

        /// <summary>Project-relative.</summary>
        public string OutputFolder { get; }

        public string Problem { get; }

        /// <summary>"switching the Editor back to ..." when the build moved the Editor's target, else null.</summary>
        public string RestoreMessage { get; }

        /// <summary>The Editor was switched back and will recompile with a domain reload shortly.</summary>
        public bool ReloadFollows { get; }
    }

    /// <summary>The build step's seam: the real one is <see cref="ServerBuilder"/>; tests fake it.</summary>
    public interface IServerBuildStep
    {
        /// <summary>Builds synchronously on the main thread (Unity requires it): <paramref name="buildProfile"/> writes <paramref name="executable"/> under <c>Builds/Server/&lt;version&gt;/</c>.</summary>
        ServerBuildOutcome Build(string version, string buildProfile, string executable);
    }

    /// <summary>The domain reload lock's seam: the real one is <c>EditorApplication.LockReloadAssemblies</c>.</summary>
    public interface IReloadLock
    {
        /// <summary>Locks assembly reloads until the returned handle is disposed (exactly once).</summary>
        IDisposable Acquire();
    }

    /// <summary>The in-process server build through <see cref="ServerBuilder"/> and a build profile, always plain.</summary>
    public sealed class EditorServerBuildStep : IServerBuildStep
    {
        public ServerBuildOutcome Build(string version, string buildProfile, string executable)
        {
            ServerBuildOptions options = ServerBuildOptions.ForProfile(version, buildProfile, executable);
            if (!options.IsValid)
            {
                return new ServerBuildOutcome(false, null, string.Join("; ", options.Errors), null, false);
            }

            ServerBuildResult result = ServerBuilder.Build(options);
            return new ServerBuildOutcome(result.Passed, result.OutputFolder, result.Problem,
                result.Restore != null && result.Restore.Changed ? result.Restore.Message : null,
                result.Restore != null && result.Restore.ReloadFollows);
        }
    }

    /// <summary><c>EditorApplication.LockReloadAssemblies</c> / <c>UnlockReloadAssemblies</c>, balanced.</summary>
    public sealed class EditorReloadLock : IReloadLock
    {
        public IDisposable Acquire()
        {
            EditorApplication.LockReloadAssemblies();
            return new Handle();
        }

        private sealed class Handle : IDisposable
        {
            private int released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref released, 1) == 0)
                {
                    EditorApplication.UnlockReloadAssemblies();
                }
            }
        }
    }

    /// <summary>Everything the pipeline runner talks to, injectable for tests.</summary>
    public sealed class PipelineServices
    {
        /// <summary>The Unity project root (the folder holding <c>Assets/</c>).</summary>
        public string ProjectRoot { get; set; }

        /// <summary>The signed-in workspace client.</summary>
        public IPingCoreApi Api { get; set; }

        /// <summary>Where push tokens are kept.</summary>
        public ICredentialStore Store { get; set; }

        public IProcessRunner Processes { get; set; }

        public IServerBuildStep Builder { get; set; }

        public IReloadLock ReloadLock { get; set; }

        /// <summary>The current UTC time.</summary>
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>Waits (polls, the one retry); cancellable.</summary>
        public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (t, c) => Task.Delay(t, c);

        /// <summary>Finds pingctl (your own, <c>PINGCTL_BIN</c>, or the bundled binary checked against its manifest).</summary>
        public Func<PingctlLocation> LocatePingctl { get; set; }

        /// <summary>
        /// The pingctl the push runs for a location: null (the default) is <see cref="PingctlRunCopy.Prepare"/> under
        /// <see cref="ProjectRoot"/>, a checked and held private copy of the bundled binary. Tests pass a fake.
        /// </summary>
        public Func<PingctlLocation, PingctlRunCopy> PreparePingctl { get; set; }

        /// <summary>Checks a folder may be pushed: the project root, the folder, the startup files (<see cref="BuildFolderCheck.Check"/>).</summary>
        public Func<string, string, StartupFiles, BuildFolderVerdict> CheckBuildFolder { get; set; } = BuildFolderCheck.Check;

        /// <summary>A new run id.</summary>
        public Func<string> NewRunId { get; set; } = () => Guid.NewGuid().ToString("N").Substring(0, 12);

        /// <summary>The services of the open Editor for a signed-in client and its store.</summary>
        /// <param name="pingctlPath">The developer's own pingctl, read at each locate (a changed setting applies to the next push).</param>
        public static PipelineServices ForEditor(string projectRoot, IPingCoreApi api, ICredentialStore store, Func<string> pingctlPath)
        {
            return new PipelineServices
            {
                ProjectRoot = projectRoot,
                Api = api,
                Store = store,
                Processes = new ChildProcessRunner(),
                Builder = new EditorServerBuildStep(),
                ReloadLock = new EditorReloadLock(),
                LocatePingctl = () => PingctlLocator.LocateHere(pingctlPath?.Invoke()),
            };
        }

    }
}
