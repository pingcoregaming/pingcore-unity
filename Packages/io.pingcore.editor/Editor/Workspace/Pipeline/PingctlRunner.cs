using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Process;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>What a push through pingctl produced.</summary>
    public sealed class PingctlPushResult
    {
        public PingctlPushResult(string snapshot, bool? changed, Version pingctlVersion, PluginError error)
        {
            Snapshot = snapshot;
            Changed = changed;
            PingctlVersion = pingctlVersion;
            Error = error;
        }

        /// <summary>The snapshot pingctl printed, or null.</summary>
        public string Snapshot { get; }

        /// <summary>True for a new snapshot, false for an unchanged one, null when no snapshot line was printed.</summary>
        public bool? Changed { get; }

        /// <summary>What <c>pingctl version</c> reported, or null.</summary>
        public Version PingctlVersion { get; }

        /// <summary>Null on success.</summary>
        public PluginError Error { get; }

        public bool Ok => Error == null;
    }

    /// <summary>
    /// The push: runs <c>pingctl version</c> (refusing anything older than
    /// <see cref="PingctlCommand.MinimumVersion"/>), then <c>pingctl push</c> on the server build
    /// folder. The child gets the default environment allowlist plus the push token and the
    /// workspace's API base, never an argument holding a token; every output line is redacted
    /// (the runner masks the token it was handed) before it reaches <paramref name="onLine"/>.
    /// </summary>
    public sealed class PingctlRunner
    {
        /// <summary>How long a push may run.</summary>
        public static readonly TimeSpan PushTimeout = TimeSpan.FromMinutes(30);

        private readonly IProcessRunner runner;
        private readonly string pingctlPath;

        public PingctlRunner(IProcessRunner runner, string pingctlPath)
        {
            this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
            this.pingctlPath = pingctlPath;
        }

        /// <summary>The spec of <c>pingctl version</c>: no token in its environment. Pure.</summary>
        public static ProcessSpec VersionSpec(string pingctlPath, WorkspaceEndpoint endpoint, Action<ProcessLine> onLine)
        {
            return new ProcessSpec
            {
                FileName = pingctlPath,
                Args = PingctlCommand.VersionArguments(),
                EnvAllowlist = ProcessEnvironment.DefaultAllowlist,
                ExtraEnv = PingctlCommand.Environment(null, endpoint),
                Timeout = TimeSpan.FromMinutes(1),
                OnLine = onLine,
                Step = "push",
            };
        }

        /// <summary>The spec of <c>pingctl push</c>. Pure; <paramref name="subfolderNames"/> are the build folder's direct subfolders.</summary>
        public static ProcessSpec PushSpec(string pingctlPath, string folder, string product, IEnumerable<string> subfolderNames, string pushToken, WorkspaceEndpoint endpoint, Action<ProcessLine> onLine)
        {
            return new ProcessSpec
            {
                FileName = pingctlPath,
                Args = PingctlCommand.PushArguments(folder, product, subfolderNames),
                EnvAllowlist = ProcessEnvironment.DefaultAllowlist,
                ExtraEnv = PingctlCommand.Environment(pushToken, endpoint),
                KnownSecrets = new[] { pushToken },
                Timeout = PushTimeout,
                OnLine = onLine,
                Step = "push",
            };
        }

        /// <summary>Runs <c>pingctl version</c>; the version, or an error naming what is wrong.</summary>
        public async Task<(Version Version, PluginError Error)> CheckVersionAsync(WorkspaceEndpoint endpoint, Action<ProcessLine> onLine, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(pingctlPath))
            {
                return (null, new PluginError("push", PluginErrorKind.ToolMissing, "pingctl was not found.", PingctlLocator.GetPingctlHint));
            }

            ProcessResult result = await runner.RunAsync(VersionSpec(pingctlPath, endpoint, onLine), cancellationToken);
            if (!result.Ok)
            {
                return (null, result.Error ?? new PluginError("push", PluginErrorKind.ChildFailed, "pingctl version failed.", null));
            }

            Version version = PingctlCommand.ParseVersion(result.Lines.Select(l => l.Text));
            if (version == null)
            {
                return (null, new PluginError("push", PluginErrorKind.ToolMissing, "pingctl version printed no version; is that pingctl?", PingctlLocator.GetPingctlHint));
            }

            if (version < PingctlCommand.MinimumVersion)
            {
                return (version, new PluginError("push", PluginErrorKind.ToolMissing, $"pingctl {version} is older than {PingctlCommand.MinimumVersion}, the oldest this plugin drives.", PingctlLocator.GetPingctlHint));
            }

            return (version, null);
        }

        /// <summary>
        /// <c>pingctl version</c>, then <c>pingctl push &lt;folder&gt;</c>. <paramref name="folder"/>
        /// is the absolute build folder; <paramref name="pushToken"/> goes to the child's
        /// environment only and is masked in its output.
        /// </summary>
        public async Task<PingctlPushResult> PushAsync(string folder, string product, string pushToken, WorkspaceEndpoint endpoint, Action<ProcessLine> onLine, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(pushToken))
            {
                return new PingctlPushResult(null, null, null, new PluginError("push", PluginErrorKind.NotSignedIn, "No push token is stored for this CDN source.", "Press Push again: it issues one after you confirm."));
            }

            (Version version, PluginError versionError) = await CheckVersionAsync(endpoint, onLine, cancellationToken);
            if (versionError != null)
            {
                return new PingctlPushResult(null, null, version, versionError);
            }

            onLine?.Invoke(new ProcessLine(ProcessStream.StdOut, $"pingctl {version}"));
            IEnumerable<string> subfolders = Directory.Exists(folder)
                ? Directory.GetDirectories(folder).Select(Path.GetFileName)
                : Enumerable.Empty<string>();
            ProcessSpec spec = PushSpec(pingctlPath, folder, product, subfolders, pushToken, endpoint, onLine);
            string problem = PingctlCommand.ArgumentProblem(spec.Args, pushToken);
            if (problem != null)
            {
                return new PingctlPushResult(null, null, version, new PluginError("push", PluginErrorKind.Refused, problem, null));
            }

            ProcessResult result = await runner.RunAsync(spec, cancellationToken);
            if (!result.Ok)
            {
                return new PingctlPushResult(null, null, version, result.Error ?? new PluginError("push", PluginErrorKind.ChildFailed, "pingctl push failed.", null));
            }

            (string snapshot, bool? changed) = PingctlCommand.ParseSnapshot(result.Lines.Where(l => l.Stream == ProcessStream.StdOut).Select(l => l.Text));
            return new PingctlPushResult(snapshot, changed, version, null);
        }
    }
}
