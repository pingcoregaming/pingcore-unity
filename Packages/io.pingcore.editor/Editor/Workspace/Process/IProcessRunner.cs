using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;

namespace PingCore.Editor.Workspace.Process
{
    /// <summary>
    /// Starts a child process (<c>pingctl</c>, <c>chmod</c>) with an environment built from an
    /// allowlist, never a token on its command line, and every output line redacted before
    /// anyone sees it. Never throws for a child's failure; the result says what happened.
    /// </summary>
    public interface IProcessRunner
    {
        /// <summary>Runs <paramref name="spec"/> to completion, timeout or cancellation.</summary>
        Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken);
    }

    /// <summary>Which stream a line came from.</summary>
    public enum ProcessStream
    {
        StdOut,
        StdErr,
    }

    /// <summary>One output line, already redacted.</summary>
    public sealed class ProcessLine
    {
        public ProcessLine(ProcessStream stream, string text)
        {
            Stream = stream;
            Text = text ?? string.Empty;
        }

        public ProcessStream Stream { get; }

        /// <summary>The line with every secret masked.</summary>
        public string Text { get; }

        public override string ToString() => Stream == ProcessStream.StdErr ? "! " + Text : Text;
    }

    /// <summary>What to run.</summary>
    public sealed class ProcessSpec
    {
        /// <summary>The executable: an absolute path (preferred) or a name looked up on the child's <c>PATH</c>.</summary>
        public string FileName { get; set; }

        /// <summary>Arguments, quoted by the runner. A token-shaped argument is refused before the child starts.</summary>
        public IReadOnlyList<string> Args { get; set; } = Array.Empty<string>();

        /// <summary>Names of parent environment variables copied to the child (matched without case). Nothing else is inherited.</summary>
        public IReadOnlyList<string> EnvAllowlist { get; set; } = ProcessEnvironment.DefaultAllowlist;

        /// <summary>Extra variables for the child, for example <c>PINGCORE_PUSH_TOKEN</c>. Their values are also masked in the output.</summary>
        public IReadOnlyDictionary<string, string> ExtraEnv { get; set; } = new Dictionary<string, string>();

        /// <summary>Text written to the child's standard input, then closed (for a tool that reads a secret from it), or null. Also masked in the output.</summary>
        public string StandardInput { get; set; }

        /// <summary>The working directory, or null for the current one.</summary>
        public string WorkingDir { get; set; }

        /// <summary>How long the child may run before it is killed.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>Called for every output line, already redacted, on the caller's synchronization context when it has one.</summary>
        public Action<ProcessLine> OnLine { get; set; }

        /// <summary>More exact values to mask in the output (for example a key the step read from the store).</summary>
        public IReadOnlyList<string> KnownSecrets { get; set; } = Array.Empty<string>();

        /// <summary>The step this child serves, for errors (<c>push</c>).</summary>
        public string Step { get; set; } = "child";
    }

    /// <summary>How a child ended.</summary>
    public sealed class ProcessResult
    {
        public ProcessResult(int? exitCode, bool timedOut, bool cancelled, IReadOnlyList<ProcessLine> lines, PluginError error)
        {
            ExitCode = exitCode;
            TimedOut = timedOut;
            Cancelled = cancelled;
            Lines = lines ?? Array.Empty<ProcessLine>();
            Error = error;
        }

        /// <summary>The exit code, or null when the child did not run or was killed.</summary>
        public int? ExitCode { get; }

        public bool TimedOut { get; }

        public bool Cancelled { get; }

        /// <summary>Every output line in order (redacted), capped at <see cref="ProcessEnvironment.MaxKeptLines"/>.</summary>
        public IReadOnlyList<ProcessLine> Lines { get; }

        /// <summary>Null when the child exited 0; else why not (<see cref="PluginErrorKind.ChildFailed"/>, <see cref="PluginErrorKind.ToolMissing"/>, <see cref="PluginErrorKind.Refused"/> or <see cref="PluginErrorKind.Cancelled"/>).</summary>
        public PluginError Error { get; }

        /// <summary>True when the child exited 0.</summary>
        public bool Ok => Error == null && ExitCode == 0;
    }

    /// <summary>The child environment defaults.</summary>
    public static class ProcessEnvironment
    {
        /// <summary>
        /// The parent variables a child gets by default: what Windows and a CLI need to run and
        /// nothing else.
        /// </summary>
        public static IReadOnlyList<string> DefaultAllowlist { get; } = new[]
        {
            "PATH", "SYSTEMROOT", "COMSPEC", "TEMP", "TMP", "USERPROFILE", "HOME", "APPDATA", "LOCALAPPDATA", "PROGRAMDATA",
        };

        /// <summary>At most this many output lines are kept in a <see cref="ProcessResult"/>.</summary>
        public const int MaxKeptLines = 5000;

        /// <summary>
        /// The child's environment: the allowlisted parent variables (names matched without case)
        /// plus <paramref name="extra"/>, which wins. Pure.
        /// </summary>
        public static Dictionary<string, string> Build(IReadOnlyDictionary<string, string> parent, IReadOnlyList<string> allowlist, IReadOnlyDictionary<string, string> extra)
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var allowed = new HashSet<string>(allowlist ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (parent != null)
            {
                foreach (KeyValuePair<string, string> pair in parent)
                {
                    if (pair.Value != null && allowed.Contains(pair.Key))
                    {
                        env[pair.Key] = pair.Value;
                    }
                }
            }

            if (extra != null)
            {
                foreach (KeyValuePair<string, string> pair in extra)
                {
                    if (!string.IsNullOrEmpty(pair.Key) && pair.Value != null)
                    {
                        env[pair.Key] = pair.Value;
                    }
                }
            }

            return env;
        }
    }
}
