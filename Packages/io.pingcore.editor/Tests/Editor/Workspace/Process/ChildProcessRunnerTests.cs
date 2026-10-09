using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Process;
using PingCore.Editor.Workspace.Redaction;
using PingCore.Editor.Workspace.Tests.Fakes;

namespace PingCore.Editor.Workspace.Tests.Process
{
    /// <summary>
    /// The child process runner: a token-shaped argument is refused before anything starts, the
    /// child's environment is the allowlist plus the extras and nothing else, every output line is
    /// redacted, and the quoting round-trips. The real runs use <c>cmd.exe</c> on Windows.
    /// </summary>
    public sealed class ChildProcessRunnerTests
    {
        private static string FakeToken() => "cdnpush_" + new string('k', 24) + "Zq9";

        [Test]
        public async Task ATokenShapedArgumentIsRefusedAndTheChildNeverStarts()
        {
            var runner = new ChildProcessRunner(() => new Dictionary<string, string>());
            ProcessResult result = await runner.RunAsync(new ProcessSpec
            {
                FileName = Path.Combine(Path.GetTempPath(), "no-such-tool-" + Guid.NewGuid().ToString("N") + ".exe"),
                Args = new[] { "push", "--token", FakeToken() },
                Step = "push",
            }, CancellationToken.None);

            Assert.That(result.Error.Kind, Is.EqualTo(PluginErrorKind.Refused), "refused, not ToolMissing: the start was never attempted");
            Assert.That(result.Error.Message, Does.Contain("Argument 3").And.Not.Contain(FakeToken()));
            Assert.That(ChildProcessRunner.RefusedArgumentIndex(new[] { "push", "build/", "--exclude", "x_DoNotShip/" }), Is.EqualTo(-1));
        }

        [Test]
        public void TheEnvironmentIsTheAllowlistPlusTheExtrasOnly()
        {
            var parent = new Dictionary<string, string>
            {
                ["Path"] = @"C:\Windows",
                ["SystemRoot"] = @"C:\Windows",
                ["PINGCORE_WORKSPACE_KEY"] = "usr_" + new string('p', 20),
                ["AWS_SECRET_ACCESS_KEY"] = "nope",
            };
            var extra = new Dictionary<string, string> { ["PINGCORE_PUSH_TOKEN"] = FakeToken(), ["PINGCORE_WORKSPACE"] = "https://studio.app.pingcore.io/api" };
            Dictionary<string, string> env = ProcessEnvironment.Build(parent, ProcessEnvironment.DefaultAllowlist, extra);

            Assert.That(env.Keys, Is.EquivalentTo(new[] { "Path", "SystemRoot", "PINGCORE_PUSH_TOKEN", "PINGCORE_WORKSPACE" }));
            Assert.That(env["PINGCORE_PUSH_TOKEN"], Is.EqualTo(FakeToken()));
            Assert.That(ProcessEnvironment.Build(parent, new[] { "PATH" }, null).Keys, Is.EquivalentTo(new[] { "Path" }), "names match without case");
        }

        [Test]
        public async Task ARealChildSeesOnlyTheAllowlistAndItsOutputIsRedacted()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Ignore("Runs cmd.exe; Windows only.");
            }

            string token = FakeToken();
            string leaked = "usr_" + new string('L', 20);
            string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var parent = new Dictionary<string, string>
            {
                ["SystemRoot"] = systemRoot,
                ["ComSpec"] = Path.Combine(systemRoot, "System32", "cmd.exe"),
                ["PINGCORE_PARENT_ONLY"] = leaked,
            };
            var lines = new List<ProcessLine>();
            var runner = new ChildProcessRunner(() => parent);
            ProcessResult result = await runner.RunAsync(new ProcessSpec
            {
                FileName = Path.Combine(systemRoot, "System32", "cmd.exe"),
                Args = new[] { "/d", "/c", "set" },
                EnvAllowlist = new[] { "SYSTEMROOT", "COMSPEC" },
                ExtraEnv = new Dictionary<string, string> { ["PINGCORE_PUSH_TOKEN"] = token },
                OnLine = lines.Add,
                Timeout = TimeSpan.FromSeconds(30),
                Step = "env-probe",
            }, CancellationToken.None);

            Assert.That(result.Ok, Is.True, result.Error?.ToString());
            string all = string.Join("\n", result.Lines.Select(l => l.Text));
            Assert.That(all, Does.Contain("PINGCORE_PUSH_TOKEN="), "the extra reached the child");
            Assert.That(all, Does.Not.Contain(token), "and its value was printed by set, then redacted");
            Assert.That(all, Does.Contain("PINGCORE_PUSH_TOKEN=" + Redactor.Mask));
            Assert.That(all, Does.Not.Contain("PINGCORE_PARENT_ONLY"), "a parent variable outside the allowlist never reaches the child");
            Assert.That(all, Does.Not.Contain(leaked));
            Assert.That(all, Does.Not.Contain("USERNAME=").And.Not.Contain("Path="), "the real environment was cleared");
        }

        [Test]
        public async Task ANonZeroExitAndAMissingToolAreTyped()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Ignore("Runs cmd.exe; Windows only.");
            }

            string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var runner = new ChildProcessRunner();
            ProcessResult failed = await runner.RunAsync(new ProcessSpec
            {
                FileName = Path.Combine(systemRoot, "System32", "cmd.exe"),
                Args = new[] { "/d", "/c", "exit 3" },
                Timeout = TimeSpan.FromSeconds(30),
            }, CancellationToken.None);
            Assert.That(failed.Error.Kind, Is.EqualTo(PluginErrorKind.ChildFailed));
            Assert.That(failed.ExitCode, Is.EqualTo(3));
            Assert.That(failed.Error.ExitCode, Is.EqualTo(3));

            ProcessResult missing = await runner.RunAsync(new ProcessSpec
            {
                FileName = Path.Combine(Path.GetTempPath(), "no-such-tool-" + Guid.NewGuid().ToString("N") + ".exe"),
            }, CancellationToken.None);
            Assert.That(missing.Error.Kind, Is.EqualTo(PluginErrorKind.ToolMissing));
        }

        [Test]
        public async Task StandardInputReachesTheChildAndIsMaskedInItsEcho()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Ignore("Runs cmd.exe; Windows only.");
            }

            string secret = "registry-" + "secret-value-123";
            string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            ProcessResult echoed = await new ChildProcessRunner().RunAsync(new ProcessSpec
            {
                FileName = Path.Combine(systemRoot, "System32", "findstr.exe"),
                Args = new[] { "." },
                StandardInput = secret + "\n",
                Timeout = TimeSpan.FromSeconds(30),
            }, CancellationToken.None);

            Assert.That(echoed.Ok, Is.True, echoed.Error?.ToString());
            Assert.That(echoed.Lines.Select(l => l.Text), Is.EqualTo(new[] { Redactor.Mask }), "findstr echoed the stdin line, which the runner masked");
        }

        [Test]
        public async Task AGrandchildHoldingTheOutputCannotHoldTheRunPastTheDrainBound()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Ignore("Runs cmd.exe and ping.exe; Windows only.");
            }

            string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            string ping = Path.Combine(systemRoot, "System32", "PING.EXE");
            var parent = new Dictionary<string, string> { ["SystemRoot"] = systemRoot, ["ComSpec"] = Path.Combine(systemRoot, "System32", "cmd.exe") };
            var runner = new ChildProcessRunner(() => parent, TimeSpan.FromSeconds(1));
            var watch = System.Diagnostics.Stopwatch.StartNew();

            // cmd starts ping in the background with cmd's own stdout, then exits at once; ping keeps the pipe open for ~10 s.
            ProcessResult result = await runner.RunAsync(new ProcessSpec
            {
                FileName = Path.Combine(systemRoot, "System32", "cmd.exe"),
                Args = new[] { "/d", "/c", "start /b " + ping + " -n 11 127.0.0.1" },
                EnvAllowlist = new[] { "SYSTEMROOT", "COMSPEC" },
                Timeout = TimeSpan.FromSeconds(60),
                Step = "drain-probe",
            }, CancellationToken.None);
            watch.Stop();

            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(6)), "[mutation: process.WaitForExit() after Exited] the run waited for the grandchild");
            Assert.That(result.Ok, Is.False, "output that was cut short never counts as a clean run");
            Assert.That(result.Error.Kind, Is.EqualTo(PluginErrorKind.ChildFailed));
            Assert.That(result.Error.Message, Does.Contain("output stayed open for 1 s"));
            Assert.That(result.ExitCode, Is.EqualTo(0), "cmd itself exited 0");
        }

        [Test]
        public async Task AChildWhoseOutputEndsWithItIsNotHeldByTheDrainBound()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Ignore("Runs cmd.exe; Windows only.");
            }

            string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ProcessResult result = await new ChildProcessRunner(null, TimeSpan.FromSeconds(30)).RunAsync(new ProcessSpec
            {
                FileName = Path.Combine(systemRoot, "System32", "cmd.exe"),
                Args = new[] { "/d", "/c", "echo one& echo two" },
                Timeout = TimeSpan.FromSeconds(30),
            }, CancellationToken.None);
            watch.Stop();

            Assert.That(result.Ok, Is.True, result.Error?.ToString());
            Assert.That(result.Lines.Select(l => l.Text.Trim()), Is.EqualTo(new[] { "one", "two" }), "every line arrived before the run answered");
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)), "the drain ends when the streams end, not when the bound runs out");
        }

        [TestCase("plain", "plain")]
        [TestCase("with space", "\"with space\"")]
        [TestCase("", "\"\"")]
        [TestCase("quote\"inside", "\"quote\\\"inside\"")]
        [TestCase("C:\\dir with space\\", "\"C:\\dir with space\\\\\"")]
        [TestCase("a\\\\b", "a\\\\b")]
        public void QuotingFollowsTheWindowsArgumentRules(string arg, string quoted)
        {
            Assert.That(CommandLine.Quote(arg), Is.EqualTo(quoted));
        }

        [Test]
        public async Task TheFakeRunnerEnforcesTheSameArgvRuleAndRedactsItsScript()
        {
            var fake = new FakeProcessRunner().Enqueue("pingctl", new FakeProcessScript(0, "using token " + FakeToken(), "snapshot_20990101_000000"));
            ProcessResult refused = await fake.RunAsync(new ProcessSpec { FileName = "pingctl", Args = new[] { FakeToken() } }, CancellationToken.None);
            Assert.That(refused.Error.Kind, Is.EqualTo(PluginErrorKind.Refused));

            ProcessResult ran = await fake.RunAsync(new ProcessSpec { FileName = "C:/tools/pingctl.exe", Args = new[] { "push", "build/" }, ExtraEnv = new Dictionary<string, string> { ["PINGCORE_PUSH_TOKEN"] = FakeToken() } }, CancellationToken.None);
            Assert.That(ran.Ok, Is.True);
            Assert.That(ran.Lines.Select(l => l.Text), Is.EqualTo(new[] { "using token " + Redactor.Mask, "snapshot_20990101_000000" }));
            Assert.That(fake.Runs.Count, Is.EqualTo(2));
        }
    }
}
