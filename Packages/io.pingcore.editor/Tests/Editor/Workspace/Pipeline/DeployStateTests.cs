using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using NUnit.Framework;
using PingCore.Editor.Workspace.Pipeline;
using static PingCore.Editor.Workspace.Tests.Pipeline.PipelineFixtures;

namespace PingCore.Editor.Workspace.Tests.Pipeline
{
    /// <summary><c>UserSettings/PingCoreDeployState.json</c>: round trip, a corrupt or foreign file, and no credential.</summary>
    public sealed class DeployStateTests
    {
        private string root;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "pingcore-state-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        private static DeployState Full()
        {
            DeployState s = Start(Request());
            s.InFlight = DeployActionKind.PollRelease;
            s.Built = true;
            s.BuildFolder = "Builds/Server/" + TestVersion;
            s.Pushed = true;
            s.Snapshot = TestVersion;
            s.SnapshotChanged = true;
            s.TargetsFirstCheckedUtc = T0.AddSeconds(30);
            s.TargetsChecks = 2;
            s.TargetsConfirmed = true;
            s.DataSourceType = "cdn_source";
            s.PinnedVersion = "2026.10.06-p2fix";
            s.ReleaseAttempts = 1;
            s.ReleaseId = 12;
            s.ReleaseState = "rolling";
            s.ReleaseBlocked = "window";
            s.Locations.Add(new ReleaseLocationProgress { BrandDeploymentId = 21, Phase = "rolling", OldCount = 1, NewCount = 1, Blocked = "b", Parked = true });
            s.PollFailures = 2;
            s.Startup = new StartupFiles(new[] { "bin/Server.x86_64", "Other.x86_64" }, true);
            s.StartupCheckSkipped = "The startup-command check was skipped: no deployment of the fleet names a process to launch.";
            return s;
        }

        [Test]
        public void EveryFieldSurvivesASaveAndALoad()
        {
            DeployState s = Full();
            DeployStateFile.Save(root, s);
            DeployStateLoad loaded = DeployStateFile.Load(root);
            Assert.That(loaded.Problem, Is.Null);
            Assert.That(JsonConvert.SerializeObject(loaded.State), Is.EqualTo(JsonConvert.SerializeObject(s)));
            Assert.That(loaded.State.TargetsFirstCheckedUtc.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(loaded.State.InFlight, Is.EqualTo(DeployActionKind.PollRelease));
            Assert.That(File.ReadAllText(DeployStateFile.PathIn(root)), Does.Contain("\"inFlight\": \"PollRelease\"").And.Not.Contain("\r\n"));
        }

        [Test]
        public void TheStartupFilesSurviveASaveAndAnEarlierSingleExecutableIsReadAsOne()
        {
            DeployStateFile.Save(root, Full());
            StartupFiles back = DeployStateFile.Load(root).State.Startup;
            Assert.That(back.Paths, Is.EqualTo(new[] { "bin/Server.x86_64", "Other.x86_64" }), "every file");
            Assert.That(back.AnyOf, Is.True, "the any-one flag");
            Assert.That(File.ReadAllText(DeployStateFile.PathIn(root)), Does.Not.Contain("\"startupExecutable\""), "the earlier key is never written");

            // A run saved by the version before several startup files: its one executable is read as the only file.
            string path = DeployStateFile.PathIn(root);
            File.WriteAllText(path, "{\"format\":\"pingcore-deploy-state/1\",\"runId\":\"old1\",\"startupExecutable\":\"BeaconRush.x86_64\"}", new UTF8Encoding(false));
            StartupFiles old = DeployStateFile.Load(root).State.Startup;
            Assert.That(old.Paths, Is.EqualTo(new[] { "BeaconRush.x86_64" }), "[mutation: drop an earlier run's executable]");
            Assert.That(old.AnyOf, Is.False);
        }

        [Test]
        public void NoFileIsNoStateAndNoProblem()
        {
            DeployStateLoad loaded = DeployStateFile.Load(root);
            Assert.That(loaded.State, Is.Null);
            Assert.That(loaded.Problem, Is.Null);
        }

        [TestCase("{ this is not json")]
        [TestCase("{\"format\":\"something-else/1\"}")]
        [TestCase("{\"format\":\"pingcore-deploy-state/1\",\"releaseId\":\"eighty-three\"}")]
        [TestCase("[]")]
        public void ACorruptOrForeignFileIsSetAsideAndTheNextLoadStartsClean(string text)
        {
            string path = DeployStateFile.PathIn(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));

            DeployStateLoad loaded = DeployStateFile.Load(root);
            Assert.That(loaded.State, Is.Null, "[mutation: return a default state]");
            Assert.That(loaded.Problem, Does.Contain("PingCoreDeployState.json"));
            Assert.That(File.Exists(path), Is.False);
            Assert.That(File.ReadAllText(path + ".corrupt"), Is.EqualTo(text));

            DeployStateLoad again = DeployStateFile.Load(root);
            Assert.That((again.State, again.Problem), Is.EqualTo(((DeployState)null, (string)null)));
        }

        [Test]
        public void ACredentialShapedValueIsNeverWritten()
        {
            DeployState s = Full();
            s.FailureMessage = "pingctl said " + FakePushToken();
            Assert.Throws<InvalidOperationException>(() => DeployStateFile.Save(root, s), "[mutation: skip RefuseSecrets]");
            Assert.That(File.Exists(DeployStateFile.PathIn(root)), Is.False);
        }

        [Test]
        public void AnUnfinishedReleaseIsOneWithAnIdAndNoFinalState()
        {
            DeployState s = Full();
            Assert.That(s.ReleaseUnfinished, Is.True);
            s.ReleaseState = "completed";
            Assert.That(s.ReleaseUnfinished, Is.False);
            s.ReleaseState = "rolling";
            s.ReleaseId = 0;
            Assert.That(s.ReleaseUnfinished, Is.False);
        }
    }
}
