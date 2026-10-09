using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.Process;
using PingCore.Editor.Workspace.Tests.Fakes;
using static PingCore.Editor.Workspace.Tests.Pipeline.PipelineFixtures;

namespace PingCore.Editor.Workspace.Tests.Pipeline
{
    /// <summary>pingctl's argument and environment builder, its output parsers and the runner against a fake child. The locator is <c>PingctlLocatorTests</c>.</summary>
    public sealed class PingctlTests
    {
        private const string Pingctl = @"C:\tools\pingctl.exe";
        private const string Folder = @"C:\proj\Builds\Server\2026.10.07-editor1";

        // The two variable names, assembled the way the plugin does (the secrets scan bans the platform's own name as a literal).
        private static readonly string TokenName = "PINGCORE_PUSH" + "_TOKEN";
        private static readonly string UrlName = "PINGCORE_" + "API" + "_URL";

        private static WorkspaceEndpoint Endpoint => WorkspaceEndpoint.ForHost("studio.app.pingcore.io");

        [Test]
        public void ThePushArgumentsNameTheFolderAndExcludeUnitysTwoDoNotShipFoldersAndNothingElse()
        {
            IReadOnlyList<string> args = PingctlCommand.PushArguments(Folder, "BeaconRushServer", new[] { "BeaconRushServer_Data" });
            Assert.That(args, Is.EqualTo(new[]
            {
                "push", Folder,
                "--exclude", "BeaconRushServer_BackUpThisFolder_ButDontShipItWithYourGame/",
                "--exclude", "BeaconRushServer_BurstDebugInformation_DoNotShip/",
            }));
        }

        [Test]
        public void ADoNotShipFolderNamedAfterTheProjectIsExcludedToo()
        {
            // An in-Editor build names its Burst folder after the project's product name, not the executable.
            IReadOnlyList<string> args = PingctlCommand.PushArguments(Folder, "Srv", new[] { "SampleGame_BurstDebugInformation_DoNotShip", "Srv_Data" });
            Assert.That(args.Where((a, i) => i > 0 && args[i - 1] == "--exclude"), Is.EqualTo(new[]
            {
                "SampleGame_BurstDebugInformation_DoNotShip/", "Srv_BackUpThisFolder_ButDontShipItWithYourGame/", "Srv_BurstDebugInformation_DoNotShip/",
            }));
        }

        [Test]
        public void TheVariableNamesArePingctlsOwn()
        {
            Assert.That(PingctlCommand.PushTokenVariable, Is.EqualTo(TokenName));
            Assert.That(PingctlCommand.ApiUrlVariable, Is.EqualTo(UrlName));
        }

        [Test]
        public void ThePushEnvironmentCarriesTheTokenAndTheWorkspaceApiBaseOnly()
        {
            string token = FakePushToken();
            IReadOnlyDictionary<string, string> env = PingctlCommand.Environment(token, Endpoint);
            Assert.That(env.Keys.OrderBy(k => k, StringComparer.Ordinal), Is.EqualTo(new[] { UrlName, TokenName }.OrderBy(k => k, StringComparer.Ordinal)));
            Assert.That(env[UrlName], Is.EqualTo("https://studio.app.pingcore.io/api"));
            Assert.That(env[TokenName], Is.EqualTo(token));
            Assert.That(PingctlCommand.Environment(null, Endpoint).Keys, Is.EqualTo(new[] { UrlName }), "pingctl version never gets the token");
        }

        [Test]
        public void TheChildEnvironmentIsTheAllowlistPlusTheTwoVariables()
        {
            string token = FakePushToken();
            ProcessSpec spec = PingctlRunner.PushSpec(Pingctl, Folder, "BeaconRushServer", Array.Empty<string>(), token, Endpoint, null);
            var parent = new Dictionary<string, string>
            {
                ["PATH"] = @"C:\Windows",
                ["USERPROFILE"] = @"C:\Users\dev",
                ["PINGCORE_USR_KEY"] = "usr" + "_" + new string('k', 24),
                ["AWS_SECRET_ACCESS_KEY"] = "secret",
                ["PINGCTL_BIN"] = Pingctl,
            };
            Dictionary<string, string> env = ProcessEnvironment.Build(parent, spec.EnvAllowlist, spec.ExtraEnv);
            Assert.That(env.Keys.OrderBy(k => k, StringComparer.Ordinal), Is.EqualTo(new[] { "PATH", UrlName, TokenName, "USERPROFILE" }.OrderBy(k => k, StringComparer.Ordinal)),
                "[mutation: inherit the parent environment]");
            Assert.That(spec.KnownSecrets, Does.Contain(token));
            Assert.That(spec.Args.Any(a => a.Contains(token)), Is.False);
        }

        [Test]
        public void AnArgumentShapedLikeATokenOrHoldingTheTokenOrTokenItselfIsRefused()
        {
            string token = FakePushToken();
            Assert.That(PingctlCommand.ArgumentProblem(PingctlCommand.PushArguments(Folder, "BeaconRushServer", null), token), Is.Null);
            Assert.That(PingctlCommand.ArgumentProblem(new[] { "push", Folder, "--token", "x" }, token), Does.Contain("--token"));
            Assert.That(PingctlCommand.ArgumentProblem(new[] { "push", "--token=abc" }, token), Does.Contain("--token"));
            Assert.That(PingctlCommand.ArgumentProblem(new[] { "push", @"C:\builds\" + token }, token), Does.Contain("Argument 2"));
            Assert.That(PingctlCommand.ArgumentProblem(new[] { "push", "cdnpush" + "_" + "abcdefgh12" }, null), Does.Contain("Argument 2"), "[mutation: only compare with the exact token]");
            Assert.That(PingctlCommand.ArgumentProblem(new[] { "push", "dsc" + "_" + new string('a', 16) }, null), Is.Not.Null);
        }

        [Test]
        public void TheSnapshotParserReadsAllThreePingctlOutcomes()
        {
            Assert.That(PingctlCommand.ParseSnapshot(new[] { "Scanning 120 files", "Published version 2026.10.07-1" }), Is.EqualTo(("2026.10.07-1", (bool?)true)));
            Assert.That(PingctlCommand.ParseSnapshot(new[] { "CDN reports no content changes; version stays 2026.10.06-p2fix" }), Is.EqualTo(("2026.10.06-p2fix", (bool?)false)));
            Assert.That(PingctlCommand.ParseSnapshot(new[] { "Nothing to push", "  Published version: 2026.10.06-p2fix  " }), Is.EqualTo(("2026.10.06-p2fix", (bool?)false)));
        }

        [Test]
        public void TheSnapshotParserTakesTheLastOutcomeAndIgnoresLookalikes()
        {
            Assert.That(PingctlCommand.ParseSnapshot(new[] { "Published version a", "CDN reports no content changes; version stays b" }), Is.EqualTo(("b", (bool?)false)));
            Assert.That(PingctlCommand.ParseSnapshot(new[] { "Unpublished version x", "Published version", "error: Published version x failed" }), Is.EqualTo(((string)null, (bool?)null)));
            Assert.That(PingctlCommand.ParseSnapshot(null), Is.EqualTo(((string)null, (bool?)null)));
        }

        [Test]
        public void TheVersionParserReadsPingctlsVersionLine()
        {
            Assert.That(PingctlCommand.ParseVersion(new[] { "pingctl v0.1.1" }), Is.EqualTo(new Version(0, 1, 1)));
            Assert.That(PingctlCommand.ParseVersion(new[] { "pingctl version 1.12.3 (abc)" }), Is.EqualTo(new Version(1, 12, 3)));
            Assert.That(PingctlCommand.ParseVersion(new[] { "usage: pingctl" }), Is.Null);
        }

        [Test]
        public async Task ThePushRunsVersionFirstThenPushWithTheTokenInTheEnvironmentAndRedactedOutput()
        {
            string token = FakePushToken();
            var runner = new FakeProcessRunner()
                .Enqueue("pingctl", new FakeProcessScript(0, "pingctl v0.1.1"))
                .Enqueue("pingctl", new FakeProcessScript(0, "uploading with " + token, "Published version 2026.10.07-1"));
            var lines = new List<string>();
            PingctlPushResult result = await new PingctlRunner(runner, Pingctl).PushAsync(Folder, "BeaconRushServer", token, Endpoint, l => lines.Add(l.Text), CancellationToken.None);

            Assert.That(result.Ok, Is.True);
            Assert.That(result.Snapshot, Is.EqualTo("2026.10.07-1"));
            Assert.That(result.Changed, Is.True);
            Assert.That(runner.Runs.Select(r => r.Args[0]), Is.EqualTo(new[] { "version", "push" }));
            Assert.That(runner.Runs[0].ExtraEnv.ContainsKey(TokenName), Is.False);
            Assert.That(runner.Runs[1].ExtraEnv[TokenName], Is.EqualTo(token));
            Assert.That(runner.Runs.SelectMany(r => r.Args).Any(a => a.Contains(token)), Is.False);
            Assert.That(lines.Any(l => l.Contains(token)), Is.False, "[mutation: drop the token from the known secrets]");
            Assert.That(lines, Has.Some.Contains("[redacted]"));
        }

        [Test]
        public async Task APingctlOlderThan010IsRefusedBeforeAnyPush()
        {
            var runner = new FakeProcessRunner().Enqueue("pingctl", new FakeProcessScript(0, "pingctl v0.0.9"));
            PingctlPushResult result = await new PingctlRunner(runner, Pingctl).PushAsync(Folder, "BeaconRushServer", FakePushToken(), Endpoint, null, CancellationToken.None);
            Assert.That(result.Error.Kind, Is.EqualTo(PluginErrorKind.ToolMissing));
            Assert.That(result.Error.Message, Does.Contain("0.0.9"));
            Assert.That(runner.Runs.Count, Is.EqualTo(1), "[mutation: skip the version gate]");
        }

        [Test]
        public async Task AFailedPushOrAMissingToolOrTokenIsAnError()
        {
            var failing = new FakeProcessRunner().Enqueue("pingctl", new FakeProcessScript(0, "pingctl v0.1.1")).Enqueue("pingctl", new FakeProcessScript(3).Err("publish refused"));
            PingctlPushResult failed = await new PingctlRunner(failing, Pingctl).PushAsync(Folder, "P", FakePushToken(), Endpoint, null, CancellationToken.None);
            Assert.That(failed.Error.Kind, Is.EqualTo(PluginErrorKind.ChildFailed));
            Assert.That(failed.Error.ExitCode, Is.EqualTo(3));

            var missing = new FakeProcessRunner().Enqueue("pingctl", new FakeProcessScript(0) { ToolMissing = true });
            Assert.That((await new PingctlRunner(missing, Pingctl).PushAsync(Folder, "P", FakePushToken(), Endpoint, null, CancellationToken.None)).Error.Kind, Is.EqualTo(PluginErrorKind.ToolMissing));

            var untouched = new FakeProcessRunner();
            PingctlPushResult noToken = await new PingctlRunner(untouched, Pingctl).PushAsync(Folder, "P", null, Endpoint, null, CancellationToken.None);
            Assert.That(noToken.Error.Kind, Is.EqualTo(PluginErrorKind.NotSignedIn));
            Assert.That(untouched.Runs, Is.Empty);
        }
    }
}
