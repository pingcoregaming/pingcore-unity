using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Process;
using PingCore.Editor.Workspace.Redaction;

namespace PingCore.Editor.Workspace.Tests.Fakes
{
    /// <summary>
    /// A scripted <see cref="IProcessRunner"/>. Each <see cref="RunAsync"/> records the spec and
    /// answers the next queued script for that executable (matched by file name without
    /// directory or extension, case-insensitive), else <see cref="Default"/>. A script's lines
    /// are passed through the same redaction as the real runner (known secrets from the spec's
    /// <c>ExtraEnv</c>, <c>StandardInput</c> and <c>KnownSecrets</c>) and fed to <c>OnLine</c>.
    /// It also enforces the real runner's argv rule: a token-shaped argument answers
    /// <see cref="PluginErrorKind.Refused"/> without "running".
    /// </summary>
    public sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Dictionary<string, Queue<FakeProcessScript>> scripts = new Dictionary<string, Queue<FakeProcessScript>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every spec received, in order.</summary>
        public List<ProcessSpec> Runs { get; } = new List<ProcessSpec>();

        /// <summary>The answer when nothing is queued: exit code 0, no output.</summary>
        public FakeProcessScript Default { get; set; } = new FakeProcessScript(0);

        /// <summary>Queues an answer for the next run of <paramref name="tool"/> (for example <c>pingctl</c>).</summary>
        public FakeProcessRunner Enqueue(string tool, FakeProcessScript script)
        {
            if (!scripts.TryGetValue(tool, out Queue<FakeProcessScript> queue))
            {
                scripts[tool] = queue = new Queue<FakeProcessScript>();
            }

            queue.Enqueue(script);
            return this;
        }

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            Runs.Add(spec);
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(new ProcessResult(null, false, true, null, new PluginError(spec.Step, PluginErrorKind.Cancelled, "Cancelled.", null)));
            }

            string refused = (spec.Args ?? Array.Empty<string>()).FirstOrDefault(Redactor.LooksLikeCredentialArgument);
            if (refused != null)
            {
                return Task.FromResult(new ProcessResult(null, false, false, null, new PluginError(spec.Step, PluginErrorKind.Refused, "An argument looks like a credential; credentials go through the environment or standard input, never the command line.", null)));
            }

            string tool = System.IO.Path.GetFileNameWithoutExtension(spec.FileName ?? string.Empty);
            FakeProcessScript script = scripts.TryGetValue(tool, out Queue<FakeProcessScript> queue) && queue.Count > 0 ? queue.Dequeue() : Default;
            if (script.ToolMissing)
            {
                return Task.FromResult(new ProcessResult(null, false, false, null, new PluginError(spec.Step, PluginErrorKind.ToolMissing, $"{tool} was not found.", null)));
            }

            var known = new List<string>(spec.KnownSecrets ?? Array.Empty<string>());
            known.AddRange((spec.ExtraEnv ?? new Dictionary<string, string>()).Values);
            if (spec.StandardInput != null)
            {
                known.Add(spec.StandardInput);
                known.AddRange(spec.StandardInput.Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries));
            }

            var redactor = new Redactor(known);
            var lines = new List<ProcessLine>();
            foreach ((ProcessStream stream, string text) in script.Lines)
            {
                var line = new ProcessLine(stream, redactor.Redact(text));
                lines.Add(line);
                spec.OnLine?.Invoke(line);
            }

            PluginError error = script.TimedOut || script.ExitCode != 0
                ? new PluginError(spec.Step, PluginErrorKind.ChildFailed, script.TimedOut ? $"{tool} timed out." : $"{tool} exited with code {script.ExitCode}.", null) { ExitCode = script.TimedOut ? (int?)null : script.ExitCode }
                : null;
            return Task.FromResult(new ProcessResult(script.TimedOut ? (int?)null : script.ExitCode, script.TimedOut, false, lines, error));
        }
    }

    /// <summary>What a fake child prints and how it ends.</summary>
    public sealed class FakeProcessScript
    {
        public FakeProcessScript(int exitCode, params string[] stdoutLines)
        {
            ExitCode = exitCode;
            foreach (string line in stdoutLines)
            {
                Lines.Add((ProcessStream.StdOut, line));
            }
        }

        public int ExitCode { get; }

        /// <summary>Output lines in order (raw; the fake redacts them).</summary>
        public List<(ProcessStream Stream, string Text)> Lines { get; } = new List<(ProcessStream, string)>();

        /// <summary>When true the run answers <see cref="PluginErrorKind.ToolMissing"/>.</summary>
        public bool ToolMissing { get; set; }

        /// <summary>When true the run answers a timeout.</summary>
        public bool TimedOut { get; set; }

        /// <summary>Adds a standard error line.</summary>
        public FakeProcessScript Err(string line)
        {
            Lines.Add((ProcessStream.StdErr, line));
            return this;
        }
    }
}
