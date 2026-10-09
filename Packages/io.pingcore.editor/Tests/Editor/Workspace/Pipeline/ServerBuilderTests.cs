using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using PingCore.Editor.Build;
using PingCore.Editor.BuildGuard;
using PingCore.Editor.Cli;
using PingCore.Editor.Workspace.Pipeline;
using UnityEditor;

namespace PingCore.Editor.Workspace.Tests.Pipeline
{
    /// <summary>
    /// <c>ServerBuilder</c> is the body of <c>Cli.BuildServer</c>: the scene form's arguments give the same
    /// player options the command line always built, and the Editor's own builds go through a build profile and
    /// are always plain. Also the in-process refusals and the push folder check (putting the Editor back is <c>TargetRestoreTests</c>).
    /// </summary>
    public sealed class ServerBuilderTests
    {
        /// <summary>The player options exactly as <c>Cli.BuildServer.Build</c> wrote them before the extraction.</summary>
        private static BuildPlayerOptions FormerCliOptions(BuildServerArgs args)
        {
            return new BuildPlayerOptions
            {
                scenes = args.Scenes.ToArray(),
                locationPathName = args.OutputFolder + "/" + args.Product + BuildServerArgs.ExecutableExtension,
                target = BuildTarget.StandaloneLinux64,
                targetGroup = BuildTargetGroup.Standalone,
                subtarget = (int)StandaloneBuildSubtarget.Server,
                options = BuildOptions.None,
                extraScriptingDefines = args.ExtraDefines,
            };
        }

        private static void AssertSame(BuildPlayerOptions actual, BuildPlayerOptions expected)
        {
            Assert.That(actual.scenes, Is.EqualTo(expected.scenes));
            Assert.That(actual.locationPathName, Is.EqualTo(expected.locationPathName));
            Assert.That(actual.target, Is.EqualTo(expected.target));
            Assert.That(actual.targetGroup, Is.EqualTo(expected.targetGroup));
            Assert.That(actual.subtarget, Is.EqualTo(expected.subtarget));
            Assert.That(actual.options, Is.EqualTo(expected.options));
            Assert.That(actual.extraScriptingDefines, Is.EqualTo(expected.extraScriptingDefines));
            Assert.That(actual.assetBundleManifestPath, Is.EqualTo(expected.assetBundleManifestPath));
        }

        // TestCaseData, not string[][]: NUnit would spread a string[] item into several arguments.
        private static readonly TestCaseData[] CommandLines =
        {
            new TestCaseData((object)new[] { "-pingcoreVersion", "2026.10.07-abc" }).SetName("TheCommandLineGetsTheSamePlayerOptionsAsBeforeTheExtraction_PlainDefaults"),
            new TestCaseData((object)new[] { "-pingcoreVersion", "v1", "-pingcoreDefine", "STUDIO_DEBUG", "-pingcoreOutputRoot", "Builds/Instrumented/Server" }).SetName("TheCommandLineGetsTheSamePlayerOptionsAsBeforeTheExtraction_DefinesAndOutputRoot"),
            new TestCaseData((object)new[] { "-pingcoreVersion", "v1", "-pingcoreScene", "Assets/A.unity", "-pingcoreScene", "Assets/B.unity", "-pingcoreProduct", "Srv" }).SetName("TheCommandLineGetsTheSamePlayerOptionsAsBeforeTheExtraction_ScenesAndProduct"),
        };

        [TestCaseSource(nameof(CommandLines))]
        public void TheCommandLineGetsTheSamePlayerOptionsAsBeforeTheExtraction(string[] argv)
        {
            BuildServerArgs args = BuildServerArgs.Parse(argv);
            Assert.That(args.IsValid, Is.True);
            AssertSame(ServerBuilder.PlayerOptions(ServerBuildOptions.FromArgs(args)), FormerCliOptions(args));
            Assert.That(ServerBuildOptions.FromArgs(args).InProcess, Is.False, "the CLI never refuses Play mode or switches the target back");
        }

        [Test]
        public void ExtraDefinesAndAnOutputRootReachThePlayerOptions()
        {
            BuildServerArgs args = BuildServerArgs.Parse(new[] { "-pingcoreVersion", "v1", "-pingcoreDefine", "STUDIO_DEBUG", "-pingcoreDefine", "MORE", "-pingcoreOutputRoot", "Builds/Instrumented/Server" });
            BuildPlayerOptions options = ServerBuilder.PlayerOptions(ServerBuildOptions.FromArgs(args));
            Assert.That(options.extraScriptingDefines, Is.EqualTo(new[] { "STUDIO_DEBUG", "MORE" }));
            Assert.That(options.locationPathName, Is.EqualTo("Builds/Instrumented/Server/v1/BeaconRushServer.x86_64"));
        }

        [Test]
        public void TheEditorBuildsThroughABuildProfileAndNeverWithExtraDefines()
        {
            ServerBuildOptions editor = ServerBuildOptions.ForProfile("v1", "Assets/Server.asset", "Srv.x86_64");
            Assert.That(editor.IsValid, Is.True, string.Join("; ", editor.Errors));
            Assert.That(editor.InProcess, Is.True);
            Assert.That(editor.Args.Defines, Is.Empty, "[mutation: let the Editor add a define]");
            Assert.That(editor.Args.OutputRoot, Is.EqualTo("Builds/Server"));
            Assert.That(editor.Args.ExtraDefines, Is.Empty, "the profile holds the defines");
            Assert.That(editor.Args.Scenes, Is.Empty, "the profile holds the scenes");
            Assert.That(editor.OutputFolder, Is.EqualTo("Builds/Server/v1"));
            Assert.Throws<ArgumentException>(() => ServerBuilder.PlayerOptions(editor), "a profile build has no scene list of its own");
        }

        [Test]
        public void BadEditorOptionsAreRefusedWithTheCommandLinesWords()
        {
            Assert.That(ServerBuildOptions.ForProfile("../x", "Assets/S.asset", "S.x86_64").Errors.Single(),
                Is.EqualTo(BuildServerArgs.Parse(new[] { "-buildProfile", "Assets/S.asset", "-pingcoreVersion", "../x" }).Errors.Single()));
            Assert.That(ServerBuildOptions.ForProfile(null, "Assets/S.asset", "S.x86_64").IsValid, Is.False);
            Assert.That(ServerBuildOptions.ForProfile("v1", null, "S.x86_64").IsValid, Is.False);
            Assert.That(ServerBuildOptions.ForProfile("v1", "Assets/S.asset", "../S.x86_64").IsValid, Is.False);
            Assert.Throws<ArgumentException>(() => ServerBuilder.Build(ServerBuildOptions.ForProfile("../x", "Assets/S.asset", "S.x86_64")));
        }

        [TestCase(true, false, "Play mode")]
        [TestCase(true, true, "Play mode")]
        [TestCase(false, true, "compiling")]
        [TestCase(false, false, null)]
        public void AnInEditorBuildIsRefusedInPlayModeAndDuringACompile(bool playing, bool compiling, string expected)
        {
            string problem = ServerBuilder.EditorStateProblem(playing, compiling);
            if (expected == null)
            {
                Assert.That(problem, Is.Null);
            }
            else
            {
                Assert.That(problem, Does.Contain(expected));
            }
        }
    }

    /// <summary>
    /// A folder may be pushed only when it holds the file the game's startup command launches and the build guard passed
    /// it: a pass at postprocess for an executable inside this folder, older than none of its files.
    /// </summary>
    public sealed class BuildFolderCheckTests
    {
        private string root;
        private string folder;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "pingcore-folder-" + Guid.NewGuid().ToString("N"));
            folder = Path.Combine(root, "Builds", "Server", "v1");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Game.x86_64"), "elf");
            File.WriteAllText(Path.Combine(folder, "version.txt"), "v1\n", new UTF8Encoding(false));
            Directory.CreateDirectory(Path.Combine(folder, "Game_Data"));
            File.WriteAllText(Path.Combine(folder, "Game_Data", "level0"), "x");
            Age(folder);
            WriteVerdict(BuildGuardVerdict.Pass, "Builds/Server/v1/Game.x86_64");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        private static void Age(string dir)
        {
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-10));
                File.SetCreationTimeUtc(f, DateTime.UtcNow.AddMinutes(-10));
            }
        }

        private void WriteVerdict(string verdict, string output)
        {
            BuildGuardVerdictFile.Write(root, BuildGuardVerdict.Create(verdict, BuildGuardPostprocessor.Stage, output, null, null));
        }

        [Test]
        public void AFolderTheGuardPassedWithTheStartupExecutableMayBePushed()
        {
            Assert.That(BuildFolderCheck.Check(root, "Builds/Server/v1", StartupFiles.One("Game.x86_64")).Problems, Is.Empty);
            Assert.That(BuildFolderCheck.Check(root, folder, StartupFiles.One("Game.x86_64")).Ok, Is.True, "an absolute folder is the same folder");
        }

        [Test]
        public void ASkippedStartupCheckSkipsOnlyTheFileCheckAndStillNeedsTheGuardsVerdict()
        {
            Assert.That(BuildFolderCheck.Check(root, "Builds/Server/v1", null).Ok, Is.True, "no executable named: the guard passed this folder");
            File.Delete(BuildGuardVerdictFile.GetPath(root));
            BuildFolderVerdict noVerdict = BuildFolderCheck.Check(root, "Builds/Server/v1", null);
            Assert.That(noVerdict.Ok, Is.False, "[mutation: a skipped startup check skips the guard too]");
            Assert.That(noVerdict.Reason, Is.EqualTo(DeployFailure.NoBuild));
        }

        [Test]
        public void SeveralStartupFilesAreAllRequiredUnlessAnyOneIsEnough()
        {
            BuildFolderVerdict all = BuildFolderCheck.Check(root, "Builds/Server/v1", new StartupFiles(new[] { "Game.x86_64", "Beta.x86_64" }, false));
            Assert.That(all.Reason, Is.EqualTo(DeployFailure.StartupExecutableMissing), "the deployments' files: every one");
            Assert.That(all.Problems.Single(), Does.EndWith("this build has no ./Beta.x86_64."));

            Assert.That(BuildFolderCheck.Check(root, "Builds/Server/v1", new StartupFiles(new[] { "Beta.x86_64", "Game.x86_64" }, true)).Ok, Is.True, "the template sets' files: any one");
            Assert.That(BuildFolderCheck.Check(root, "Builds/Server/v1", new StartupFiles(new[] { "Beta.x86_64", "Other.x86_64" }, true)).Reason, Is.EqualTo(DeployFailure.StartupExecutableMissing));
        }

        [TestCase("Other.x86_64", TestName = "another executable")]
        [TestCase("game.x86_64", TestName = "the same name in another case, as Linux reads it")]
        [TestCase("bin/Game.x86_64", TestName = "the right name in a subfolder")]
        public void AFolderWithoutTheStartupExecutableIsRefusedInTheSpecsWords(string executable)
        {
            BuildFolderVerdict verdict = BuildFolderCheck.Check(root, "Builds/Server/v1", StartupFiles.One(executable));
            Assert.That(verdict.Reason, Is.EqualTo(DeployFailure.StartupExecutableMissing));
            Assert.That(verdict.Problems.Single(), Is.EqualTo($"Your game launches ./{executable}; this build has no such file."));
        }

        [Test]
        public void AMissingOrUnchosenFolderIsNoBuild()
        {
            Assert.That(BuildFolderCheck.Check(root, "Builds/Server/v2", StartupFiles.One("Game.x86_64")).Problems.Single(), Does.Contain("does not exist"));
            Assert.That(BuildFolderCheck.Check(root, " ", StartupFiles.One("Game.x86_64")).Reason, Is.EqualTo(DeployFailure.NoBuild));
        }

        [Test]
        public void AFailedVerdictOrOneForAnotherBuildIsRefused()
        {
            WriteVerdict(BuildGuardVerdict.Fail, "Builds/Server/v1/Game.x86_64");
            Assert.That(BuildFolderCheck.Check(root, "Builds/Server/v1", StartupFiles.One("Game.x86_64")).Problems, Has.Some.Contains("not pass"));
            WriteVerdict(BuildGuardVerdict.Pass, "Builds/Client/v1/Game.exe");
            Assert.That(BuildFolderCheck.Check(root, "Builds/Server/v1", StartupFiles.One("Game.x86_64")).Problems, Has.Some.Contains("another build"), "[mutation: accept any passing verdict]");
            WriteVerdict(BuildGuardVerdict.Pass, "Builds/Server/v10/Game.x86_64");
            Assert.That(BuildFolderCheck.Check(root, "Builds/Server/v1", StartupFiles.One("Game.x86_64")).Problems, Has.Some.Contains("another build"), "a sibling folder whose name starts the same is another build");
        }

        [Test]
        public void AFolderBuiltOutsideTheProjectNeedsItsOwnPassingVerdict()
        {
            string outside = Path.Combine(root, "elsewhere", "Linux");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "Game.x86_64"), "elf");
            Age(outside);
            Assert.That(BuildFolderCheck.Check(root, outside, StartupFiles.One("Game.x86_64")).Problems, Has.Some.Contains("another build"), "the last verdict is the project's own build");
            WriteVerdict(BuildGuardVerdict.Pass, Path.Combine(outside, "Game.x86_64"));
            Assert.That(BuildFolderCheck.Check(root, outside, StartupFiles.One("Game.x86_64")).Ok, Is.True, "a profile build the developer ran into a folder of their own");
        }

        [Test]
        public void AFileChangedAfterTheGuardRanIsRefused()
        {
            string late = Path.Combine(folder, "Game_Data", "late.txt");
            File.WriteAllText(late, "copied in later");
            File.SetLastWriteTimeUtc(late, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(BuildGuardVerdictFile.GetPath(root), DateTime.UtcNow.AddMinutes(-5));
            Assert.That(BuildFolderCheck.Check(root, "Builds/Server/v1", StartupFiles.One("Game.x86_64")).Problems, Has.Some.Contains("changed after"), "a copy keeps its old write time but has a new creation time");
        }
    }
}
