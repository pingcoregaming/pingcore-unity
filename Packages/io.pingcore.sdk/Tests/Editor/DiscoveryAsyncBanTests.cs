using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace PingCore.Sdk.Tests.Editor
{
    /// <summary>
    /// <c>PingCore.Discovery.Client</c> follows Core's async rules although the Core ban scan does
    /// not cover it: results arrive on the Unity main thread, so no <c>ConfigureAwait(</c> call, and no
    /// <c>Task.Run(</c> to leave it. The latency probe's completing-thread timestamp is a
    /// <c>ContinueWith</c>, not either of these. This scans <c>Runtime/Discovery/**/*.cs</c>, comments
    /// included (a comment naming the construct without a call is not a hit).
    /// The one allowance: the Editor-only hook (<c>Infrastructure/InfrastructureEditorHook.cs</c>, compiled only with
    /// <c>UNITY_EDITOR</c>) starts the Editor plugin's answerer with exactly one <c>Task.Run(</c>, so a blocking answerer
    /// cannot hold Play's main thread; its result still comes back on the caller's context.
    /// </summary>
    public sealed class DiscoveryAsyncBanTests
    {
        private static readonly (string Name, Regex Pattern)[] Bans =
        {
            ("ConfigureAwait(", new Regex(@"\bConfigureAwait\s*\(")),
            ("Task.Run(", new Regex(@"\bTask\s*\.\s*Run\s*\(")),
        };

        // Relative path (forward slashes) and ban -> the exact number of hits allowed there.
        private static readonly Dictionary<(string File, string Ban), int> Allowed = new Dictionary<(string, string), int>
        {
            [("Runtime/Discovery/Infrastructure/InfrastructureEditorHook.cs", "Task.Run(")] = 1,
        };

        private static string DiscoveryRoot => Path.Combine(RepoPaths.PackageRoot, "Runtime", "Discovery");

        [Test]
        public void DiscoveryClientSourceNeitherConfiguresAwaitsNorRunsOnThePool()
        {
            List<string> files = Directory.GetFiles(DiscoveryRoot, "*.cs", SearchOption.AllDirectories).ToList();
            Assert.That(files.Select(Path.GetFileName), Does.Contain("LatencyProbe.cs").And.Contain("ClientWebSocketEchoTransport.cs").And.Contain("DiscoveryClient.cs"), "the scan root is wrong");

            var hits = new List<string>();
            var counted = new Dictionary<(string, string), int>();
            foreach (string file in files)
            {
                string relative = RepoPaths.Relative(RepoPaths.PackageRoot, file);
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (string ban in Scan(lines[i]))
                    {
                        if (Allowed.ContainsKey((relative, ban)))
                        {
                            counted[(relative, ban)] = (counted.TryGetValue((relative, ban), out int n) ? n : 0) + 1;
                            continue;
                        }

                        hits.Add($"{relative}:{i + 1}: {ban}");
                    }
                }
            }

            Assert.That(hits, Is.Empty, "Banned in PingCore.Discovery.Client:\n" + string.Join("\n", hits));
            foreach (KeyValuePair<(string File, string Ban), int> allowance in Allowed)
            {
                Assert.That(counted.TryGetValue(allowance.Key, out int seen) ? seen : 0, Is.EqualTo(allowance.Value),
                    $"{allowance.Key.File} holds exactly {allowance.Value} allowed {allowance.Key.Ban} (a second one, or none, means the allowance is stale)");
            }
        }

        [Test]
        public void TheOneAllowedTaskRunIsInTheEditorOnlyHookAndStartsOnlyTheAnswerer()
        {
            string[] hook = File.ReadAllLines(Path.Combine(DiscoveryRoot, "Infrastructure", "InfrastructureEditorHook.cs"));
            Assert.That(hook.First(l => l.Trim().Length > 0).Trim(), Is.EqualTo("#if UNITY_EDITOR"), "the file compiles only in the Editor");
            string call = hook.Single(l => Bans[1].Pattern.IsMatch(l)).Trim();
            Assert.That(call, Does.StartWith("Task<string> asked = Task.Run(() => answerer(question, token)"), "the pool runs the answerer and nothing else");
        }

        [Test]
        public void TheScannerFlagsEachBanAndLeavesNearMissesAlone()
        {
            // Synthetic hits: each must be flagged, or the scan above could pass vacuously.
            Assert.That(Scan("await receive.ConfigureAwait(false);"), Does.Contain("ConfigureAwait("));
            Assert.That(Scan("await socket.ReceiveAsync(segment, token) . ConfigureAwait (false);"), Does.Contain("ConfigureAwait("));
            Assert.That(Scan("long stamp = await Task.Run(() => Stopwatch.GetTimestamp());"), Does.Contain("Task.Run("));
            Assert.That(Scan("_ = System.Threading.Tasks.Task . Run (Loop);"), Does.Contain("Task.Run("));

            Assert.That(Scan("/// no timer and no <c>ConfigureAwait</c>. TaskRunner.Start(); task.RunSynchronously(); Task.Running = true;"), Is.Empty, "near misses are not flagged");
        }

        private static IEnumerable<string> Scan(string line)
        {
            return Bans.Where(b => b.Pattern.IsMatch(line)).Select(b => b.Name);
        }
    }
}
