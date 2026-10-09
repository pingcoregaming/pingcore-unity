using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Redaction;
using Diagnostics = System.Diagnostics;

namespace PingCore.Editor.Workspace.Process
{
    /// <summary>
    /// The real <see cref="IProcessRunner"/> on <see cref="Diagnostics.Process"/>. Before starting
    /// it refuses any argument that looks like a credential (arguments are readable by every user
    /// of the machine). The child's environment is cleared and rebuilt from
    /// <see cref="ProcessSpec.EnvAllowlist"/> plus <see cref="ProcessSpec.ExtraEnv"/> only. Every
    /// output line passes the redactor (the known secrets of the spec plus the patterns) before it
    /// is kept or handed to <see cref="ProcessSpec.OnLine"/>, which runs on the synchronization
    /// context of the caller (the Unity main thread). A timeout or a cancellation kills the child.
    /// (Mono has no process-tree kill; a tool that forks must exit with its parent.) Nothing here
    /// blocks the caller's thread: after the child exits, its output is drained asynchronously for
    /// at most <see cref="DefaultOutputDrainTimeout"/>, because a process the child started can hold
    /// the output pipe open long after the child is gone, and a blocking wait there would freeze the
    /// Editor while assembly reloads are locked.
    /// </summary>
    public sealed class ChildProcessRunner : IProcessRunner
    {
        /// <summary>How long the output may stay open after the child exited before the run gives up on it.</summary>
        public static readonly TimeSpan DefaultOutputDrainTimeout = TimeSpan.FromSeconds(5);

        private readonly Func<IReadOnlyDictionary<string, string>> parentEnvironment;
        private readonly TimeSpan outputDrainTimeout;

        /// <param name="parentEnvironment">The parent's variables; the process's own when null (tests inject one).</param>
        /// <param name="outputDrainTimeout">The output drain bound after exit; <see cref="DefaultOutputDrainTimeout"/> when null.</param>
        public ChildProcessRunner(Func<IReadOnlyDictionary<string, string>> parentEnvironment = null, TimeSpan? outputDrainTimeout = null)
        {
            this.parentEnvironment = parentEnvironment ?? CurrentEnvironment;
            this.outputDrainTimeout = outputDrainTimeout ?? DefaultOutputDrainTimeout;
        }

        /// <summary>The index of the first argument that looks like a credential, or -1. Pure.</summary>
        public static int RefusedArgumentIndex(IReadOnlyList<string> args)
        {
            if (args == null)
            {
                return -1;
            }

            for (int i = 0; i < args.Count; i++)
            {
                if (Redactor.LooksLikeCredentialArgument(args[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            if (spec == null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            string step = spec.Step ?? "child";
            string tool = string.IsNullOrEmpty(spec.FileName) ? "the tool" : Path.GetFileName(spec.FileName);
            IReadOnlyList<string> args = spec.Args ?? Array.Empty<string>();
            int refused = RefusedArgumentIndex(args);
            if (refused >= 0)
            {
                return new ProcessResult(null, false, false, null, new PluginError(step, PluginErrorKind.Refused,
                    $"Argument {refused + 1} of {tool} looks like a credential; credentials go to a child through its environment or standard input, never its command line.", null));
            }

            if (string.IsNullOrEmpty(spec.FileName))
            {
                return new ProcessResult(null, false, false, null, new PluginError(step, PluginErrorKind.ToolMissing, "No executable was given.", null));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new ProcessResult(null, false, true, null, new PluginError(step, PluginErrorKind.Cancelled, "Cancelled.", null));
            }

            var known = new List<string>(spec.KnownSecrets ?? Array.Empty<string>());
            if (spec.ExtraEnv != null)
            {
                known.AddRange(spec.ExtraEnv.Values);
            }

            if (spec.StandardInput != null)
            {
                // Each line on its own too: the child echoes lines, not the whole block.
                known.Add(spec.StandardInput);
                known.AddRange(spec.StandardInput.Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries));
            }

            var redactor = new Redactor(known);
            var info = new Diagnostics.ProcessStartInfo
            {
                FileName = spec.FileName,
                Arguments = CommandLine.Join(args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = spec.StandardInput != null,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = spec.WorkingDir ?? string.Empty,
            };
            info.EnvironmentVariables.Clear();
            foreach (KeyValuePair<string, string> pair in ProcessEnvironment.Build(parentEnvironment(), spec.EnvAllowlist, spec.ExtraEnv))
            {
                info.EnvironmentVariables[pair.Key] = pair.Value;
            }

            SynchronizationContext context = SynchronizationContext.Current;
            var lines = new List<ProcessLine>();
            var gate = new object();
            var stdoutEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stderrEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool returned = false;
            void OnData(ProcessStream stream, string data)
            {
                if (data == null)
                {
                    // A null line is the end of that stream.
                    (stream == ProcessStream.StdOut ? stdoutEnded : stderrEnded).TrySetResult(true);
                    return;
                }

                if (Volatile.Read(ref returned))
                {
                    // Late output of a run that already answered (a grandchild that kept the pipe) is dropped.
                    return;
                }

                var line = new ProcessLine(stream, redactor.Redact(data));
                lock (gate)
                {
                    if (lines.Count < ProcessEnvironment.MaxKeptLines)
                    {
                        lines.Add(line);
                    }
                }

                Action<ProcessLine> callback = spec.OnLine;
                if (callback == null)
                {
                    return;
                }

                if (context != null)
                {
                    context.Post(_ => callback(line), null);
                }
                else
                {
                    callback(line);
                }
            }

            using (var process = new Diagnostics.Process { StartInfo = info, EnableRaisingEvents = true })
            {
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.OutputDataReceived += (_, e) => OnData(ProcessStream.StdOut, e.Data);
                process.ErrorDataReceived += (_, e) => OnData(ProcessStream.StdErr, e.Data);
                process.Exited += (_, __) => exited.TrySetResult(true);

                try
                {
                    if (!process.Start())
                    {
                        return new ProcessResult(null, false, false, null, new PluginError(step, PluginErrorKind.ChildFailed, $"{tool} did not start.", null));
                    }
                }
                catch (Win32Exception e)
                {
                    // 2 and 3: the file or path was not found.
                    PluginErrorKind kind = e.NativeErrorCode == 2 || e.NativeErrorCode == 3 ? PluginErrorKind.ToolMissing : PluginErrorKind.ChildFailed;
                    return new ProcessResult(null, false, false, null, new PluginError(step, kind, $"{tool} could not be started (Windows error {e.NativeErrorCode}).", kind == PluginErrorKind.ToolMissing ? $"Set your own {tool} under Window > PingCore, Ship." : null));
                }
                catch (Exception e) when (e is InvalidOperationException || e is FileNotFoundException)
                {
                    return new ProcessResult(null, false, false, null, new PluginError(step, PluginErrorKind.ToolMissing, $"{tool} could not be started ({e.GetType().Name}).", null));
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (spec.StandardInput != null)
                {
                    try
                    {
                        await process.StandardInput.WriteAsync(spec.StandardInput);
                        process.StandardInput.Close();
                    }
                    catch (IOException)
                    {
                        // The child closed its input early; its exit code says the rest.
                    }
                }

                Task delay = Task.Delay(spec.Timeout, cancellationToken);
                Task finished = await Task.WhenAny(exited.Task, delay);
                if (finished != exited.Task)
                {
                    Kill(process);
                    Volatile.Write(ref returned, true);
                    bool cancelled = cancellationToken.IsCancellationRequested;
                    return new ProcessResult(null, !cancelled, cancelled, Snapshot(lines, gate), new PluginError(step,
                        cancelled ? PluginErrorKind.Cancelled : PluginErrorKind.ChildFailed,
                        cancelled ? $"{tool} was stopped." : $"{tool} did not finish within {spec.Timeout.TotalMinutes:0.#} minutes and was stopped.", null));
                }

                // Exited fires before the redirected streams drain. Wait for both to end, asynchronously and bounded:
                // a process the child started may hold them open (never process.WaitForExit() here, which blocks the
                // caller's thread until every holder is gone).
                Task drained = Task.WhenAll(stdoutEnded.Task, stderrEnded.Task);
                bool complete = await Task.WhenAny(drained, Task.Delay(outputDrainTimeout)) == drained;
                Volatile.Write(ref returned, true);
                int? code = ExitCodeOf(process);
                PluginError error;
                if (!complete)
                {
                    error = new PluginError(step, PluginErrorKind.ChildFailed,
                        $"{tool} exited{(code.HasValue ? " with code " + code.Value : string.Empty)}, but its output stayed open for {outputDrainTimeout.TotalSeconds:0.#} s afterwards (a process it started still holds it), so the rest of its output was not read and the step counts as failed.",
                        "Check for a process the tool left running.") { ExitCode = code };
                }
                else
                {
                    error = code == 0 ? null : new PluginError(step, PluginErrorKind.ChildFailed, $"{tool} exited with code {(code.HasValue ? code.Value.ToString() : "unknown")}.", null) { ExitCode = code };
                }

                return new ProcessResult(code, false, false, Snapshot(lines, gate), error);
            }
        }

        private static int? ExitCodeOf(Diagnostics.Process process)
        {
            try
            {
                return process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static IReadOnlyList<ProcessLine> Snapshot(List<ProcessLine> lines, object gate)
        {
            lock (gate)
            {
                return lines.ToList();
            }
        }

        private static void Kill(Diagnostics.Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception e) when (e is InvalidOperationException || e is Win32Exception)
            {
                // Already gone.
            }
        }

        private static IReadOnlyDictionary<string, string> CurrentEnvironment()
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                env[(string)entry.Key] = (string)entry.Value;
            }

            return env;
        }
    }
}
