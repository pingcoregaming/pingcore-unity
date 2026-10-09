using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Pipeline;

namespace PingCore.Editor.Workspace.Tests.Pipeline
{
    /// <summary>
    /// The Push check's startup command, pure: the file a command-line config's process name launches; the decision for a
    /// fleet with deployments (every member's file, named when they differ) and for one with none (any of the game's
    /// template sets' files); the skips, which never refuse; and whether a build holds what is required. Never a branch's
    /// default deployment spec.
    /// </summary>
    public sealed class StartupCommandTests
    {
        [TestCase("./BeaconRush.x86_64", "./BeaconRush.x86_64", "BeaconRush.x86_64", TestName = "a ./ prefix")]
        [TestCase("BeaconRush.x86_64", "BeaconRush.x86_64", "BeaconRush.x86_64", TestName = "a bare name")]
        [TestCase("./BeaconRush.x86_64 -batchmode -nographics -port 7777", "./BeaconRush.x86_64", "BeaconRush.x86_64", TestName = "arguments after the name")]
        [TestCase("  ./BeaconRush.x86_64\t-batchmode ", "./BeaconRush.x86_64", "BeaconRush.x86_64", TestName = "spaces and a tab around it")]
        [TestCase("\"./My Game.x86_64\" -port 1", "./My Game.x86_64", "My Game.x86_64", TestName = "a double-quoted name with a space")]
        [TestCase("'bin/Server.x86_64' -logFile -", "bin/Server.x86_64", "bin/Server.x86_64", TestName = "a single-quoted path in a subfolder")]
        [TestCase("./././Game", "./././Game", "Game", TestName = "repeated ./")]
        public void TheFirstWordIsTheFileTheBuildMustHold(string processName, string written, string relative)
        {
            StartupExecutable exe = StartupCommand.Parse(processName, out string problem);
            Assert.That(problem, Is.Null);
            Assert.That((exe.Written, exe.RelativePath), Is.EqualTo((written, relative)));
        }

        [TestCase(null, "has no startup command", TestName = "no process name")]
        [TestCase("   ", "has no startup command", TestName = "a blank process name")]
        [TestCase("\"./Game -port 1", "unbalanced quote", TestName = "an unbalanced quote")]
        [TestCase("\"\" -port 1", "empty name", TestName = "an empty quoted name")]
        [TestCase("/opt/game/Game.x86_64", "a path outside the build", TestName = "an absolute path")]
        [TestCase("../Game.x86_64", "not a file inside the build", TestName = "a path that climbs out")]
        [TestCase("bin\\Game.exe", "not a file inside the build", TestName = "a backslash")]
        [TestCase("./", "not a file inside the build", TestName = "only ./")]
        public void ACommandThePluginCannotCheckIsRefusedAndSaysWhereToFixIt(string processName, string problem)
        {
            Assert.That(StartupCommand.Parse(processName, out string actual), Is.Null);
            Assert.That(actual, Does.Contain(problem));
            Assert.That(actual, Does.EndWith(StartupCommand.FixInPanel));
        }

        [Test]
        public void ABuildWithoutTheFileIsRefusedInTheSpecsWords()
        {
            var files = new[] { "BeaconRush.x86_64", "BeaconRush_Data/level0", "UnityPlayer.so" };
            Assert.That(StartupCommand.MissingProblem("BeaconRush.x86_64", files), Is.Null);
            Assert.That(StartupCommand.MissingProblem("Other.x86_64", files), Is.EqualTo("Your game launches ./Other.x86_64; this build has no such file."));
            Assert.That(StartupCommand.MissingProblem("beaconrush.x86_64", files), Is.Not.Null, "Linux compares names case-sensitively");
            Assert.That(StartupCommand.MissingProblem("BeaconRush_Data/level0", new[] { "BeaconRush_Data\\level0" }), Is.Null, "either slash in the listing");
            Assert.That(StartupCommand.MissingProblem((string)null, files), Does.Contain("not known"));
        }

        [Test]
        public void SeveralFilesAreAllRequiredForDeploymentsAndAnyOneForTemplateSets()
        {
            var files = new[] { "A.x86_64", "A_Data/level0" };
            var all = new StartupFiles(new[] { "A.x86_64", "B.x86_64" }, false);
            Assert.That(StartupCommand.MissingProblem(all, files), Is.EqualTo("Your fleet's deployments launch ./A.x86_64, ./B.x86_64; this build has no ./B.x86_64."), "[mutation: one of the deployments' files is enough]");
            Assert.That(StartupCommand.MissingProblem(all, files.Concat(new[] { "B.x86_64" })), Is.Null);

            var any = new StartupFiles(new[] { "B.x86_64", "A.x86_64" }, true);
            Assert.That(StartupCommand.MissingProblem(any, files), Is.Null, "one of the template sets' files is enough");
            Assert.That(StartupCommand.MissingProblem(any, new[] { "C.x86_64" }), Is.EqualTo("Your game's template sets launch ./B.x86_64, ./A.x86_64; this build has none of them."), "[mutation: pass with none]");
            Assert.That(StartupCommand.MissingProblem(any, new[] { "a.x86_64" }), Is.Not.Null, "case-sensitive, as Linux");

            Assert.That(StartupCommand.MissingProblem(StartupFiles.One("A.x86_64"), files), Is.Null, "one file reads as before");
            Assert.That(StartupCommand.MissingProblem(new StartupFiles(new[] { "B.x86_64" }, true), files), Is.EqualTo("Your game launches ./B.x86_64; this build has no such file."));
        }

        private static TemplateSetResponse Set(long id, string name, params TemplateConfigView[] configs) => new TemplateSetResponse { TemplateSetId = id, SetName = name, Configs = new List<TemplateConfigView>(configs) };

        private static TemplateConfigView Cli(string processName, bool active = true) => new TemplateConfigView { TemplateType = "cli", Active = active, ProcessName = processName };

        private static StartupSource Member(string name, TemplateSetResponse set, string unresolved = null) => new StartupSource { Label = "deployment " + name, TemplateSet = set, Unresolved = unresolved };

        private static StartupSource GameSet(TemplateSetResponse set) => new StartupSource { Label = "template set " + set.SetName, TemplateSet = set };

        [Test]
        public void DeploymentsThatAgreeNeedOneFileAndTheActiveCommandLineIsTheCommand()
        {
            TemplateSetResponse set = Set(2301, "Default", new TemplateConfigView { TemplateType = "file", Active = true, ProcessName = "ignored" }, Cli("./old.x86_64", active: false), Cli("./BeaconRush.x86_64 -batchmode"), Cli("BeaconRush.x86_64"));
            StartupCheck check = StartupCommand.ForDeployments(new[] { Member("eu-1", set), Member("us-1", set) });
            Assert.That((check.Problem, check.Skipped, check.Note), Is.EqualTo(((string)null, (string)null, (string)null)));
            Assert.That(check.Executable.RelativePath, Is.EqualTo("BeaconRush.x86_64"), "./x and x are the same file; a file config and an inactive one are not commands");
            Assert.That(check.Candidates.Single().Sources, Is.EqualTo(new[] { "deployment eu-1", "deployment us-1" }));
            Assert.That(check.AnyOf, Is.False);
            Assert.That(check.Files.Paths, Is.EqualTo(new[] { "BeaconRush.x86_64" }));
            Assert.That(check.Describe(), Is.Null, "one file needs no extra line");
        }

        [Test]
        public void DeploymentsThatDifferNeedEveryFileAndNameWhoLaunchesEach()
        {
            StartupCheck check = StartupCommand.ForDeployments(new[]
            {
                Member("eu-1", Set(2301, "Linux", Cli("./A.x86_64"))),
                Member("us-1", Set(2302, "Beta", Cli("./B.x86_64 -beta"))),
                Member("ap-1", Set(2301, "Linux", Cli("./A.x86_64"))),
            });
            Assert.That(check.Executable, Is.Null, "no single file");
            Assert.That(check.AnyOf, Is.False, "[mutation: any one of the deployments' files]");
            Assert.That(check.Files.Paths, Is.EqualTo(new[] { "A.x86_64", "B.x86_64" }));
            Assert.That(check.Describe(), Is.EqualTo("Your fleet's deployments launch different files: ./A.x86_64 (deployment eu-1, deployment ap-1), ./B.x86_64 (deployment us-1); the build must hold each."));
        }

        [Test]
        public void AMemberWithoutAProcessIsLeftOutAndNamedAndNoneSkipsTheCheck()
        {
            StartupCheck partial = StartupCommand.ForDeployments(new[]
            {
                Member("eu-1", Set(2301, "Linux", Cli("./A.x86_64"))),
                Member("us-1", null, "its deployment spec beta names no template set"),
            });
            Assert.That(partial.Executable.RelativePath, Is.EqualTo("A.x86_64"));
            Assert.That(partial.Note, Is.EqualTo("Not checked: deployment us-1: its deployment spec beta names no template set."));
            Assert.That(partial.Describe(), Is.EqualTo(partial.Note));

            StartupCheck none = StartupCommand.ForDeployments(new[]
            {
                Member("eu-1", Set(2301, "Linux", Cli("   "))),
                Member("us-1", null, "it no longer exists"),
            });
            Assert.That(none.Problem, Is.Null, "[mutation: refuse the push when no member names a process]");
            Assert.That(none.Candidates, Is.Empty);
            Assert.That(none.Skipped, Is.EqualTo(StartupCheck.SkippedPrefix + "no deployment of the fleet names a process to launch (deployment eu-1: template set Linux names no process to launch (no active command-line config with a process name); deployment us-1: it no longer exists)."));
        }

        [Test]
        public void AMemberCommandThePluginCannotCheckRefusesNamingTheMember()
        {
            StartupCheck outside = StartupCommand.ForDeployments(new[] { Member("eu-1", Set(2301, "Linux", Cli("./A.x86_64"))), Member("us-1", Set(2302, "Abs", Cli("/opt/game/Game.x86_64"))) });
            Assert.That(outside.Problem, Does.StartWith("Deployment us-1: ").And.Contain("a path outside the build"));
            Assert.That(outside.Skipped, Is.Null);

            StartupCheck twoFiles = StartupCommand.ForDeployments(new[] { Member("eu-1", Set(2301, "Default", Cli("./A.x86_64"), Cli("./B.x86_64"))) });
            Assert.That(twoFiles.Problem, Does.Contain("Template set Default has active command-line configs that launch different files (./A.x86_64, ./B.x86_64)"));
        }

        [Test]
        public void OneTemplateSetIsCheckedLikeOneFile()
        {
            StartupCheck check = StartupCommand.ForTemplateSets(new[] { GameSet(Set(2301, "Default", Cli("./BeaconRushServer.x86_64 -batchmode"))) });
            Assert.That(check.Executable.RelativePath, Is.EqualTo("BeaconRushServer.x86_64"));
            Assert.That(check.Candidates.Single().Sources, Is.EqualTo(new[] { "template set Default" }));
        }

        [Test]
        public void SeveralTemplateSetsPassOnAnyOneFileAndSayWhichMatched()
        {
            StartupCheck check = StartupCommand.ForTemplateSets(new[]
            {
                GameSet(Set(2301, "Linux", Cli("./A.x86_64"))),
                GameSet(Set(2302, "Beta", Cli("./B.x86_64"))),
                GameSet(Set(2303, "Empty", Cli(""))),
            });
            Assert.That(check.AnyOf, Is.True, "[mutation: every template set's file]");
            Assert.That(check.Files.Paths, Is.EqualTo(new[] { "A.x86_64", "B.x86_64" }));
            Assert.That(check.Note, Is.EqualTo("Not checked: template set Empty names no process to launch (no active command-line config with a process name)."));
            Assert.That(check.Describe(), Does.StartWith("Your game's template sets launch different files: ./A.x86_64 (template set Linux), ./B.x86_64 (template set Beta); the build must hold one of them."));
            Assert.That(check.MatchedLine(new[] { "B.x86_64", "B_Data/level0" }), Is.EqualTo("The build holds ./B.x86_64, which template set Beta launches."));
            Assert.That(check.MatchedLine(new[] { "C.x86_64" }), Is.Null);
        }

        [Test]
        public void NoTemplateSetOrNoProcessNameSkipsAndOnlyUncheckableNamesRefuse()
        {
            Assert.That(StartupCommand.ForTemplateSets(new StartupSource[0]).Skipped, Is.EqualTo(StartupCheck.SkippedPrefix + "the fleet has no deployment and the game has no template set, so the file your game launches is not known."));

            StartupCheck noProcess = StartupCommand.ForTemplateSets(new[] { GameSet(Set(2301, "Default", Cli(""))), new StartupSource { Label = "template set Old", Unresolved = "template set Old no longer exists" } });
            Assert.That(noProcess.Problem, Is.Null);
            Assert.That(noProcess.Skipped, Is.EqualTo(StartupCheck.SkippedPrefix + "the fleet has no deployment and no template set of the game names a process to launch (template set Default names no process to launch (no active command-line config with a process name); template set Old no longer exists)."));

            StartupCheck oneBad = StartupCommand.ForTemplateSets(new[] { GameSet(Set(2301, "Linux", Cli("./A.x86_64"))), GameSet(Set(2302, "Abs", Cli("/opt/Game"))) });
            Assert.That(oneBad.Executable.RelativePath, Is.EqualTo("A.x86_64"), "a set the plugin cannot check is left out when another names a file");
            Assert.That(oneBad.Note, Does.StartWith("Not checked: template set Abs's process name cannot be checked (Your game launches /opt/Game, a path outside the build").And.Not.Contain(StartupCommand.FixInPanel));

            StartupCheck allBad = StartupCommand.ForTemplateSets(new[] { GameSet(Set(2302, "Abs", Cli("/opt/Game"))) });
            Assert.That(allBad.Problem, Does.StartWith("Template set Abs: ").And.Contain("a path outside the build"));
        }

        [Test]
        public void ThePinnedChainFromDeploymentToTemplateSetWhoseProcessNameIsEmptySkipsTheCheck()
        {
            // deployments.get -> deployment-specs.list -> template-set.get: deployment 100 runs spec 5801, whose set 2301
            // answers an empty process name in the pinned fixture.
            DeploymentReadView deployment = WorkspaceFixtures.Payload<DeploymentReadResponse>("deployments.get.json").Deployment;
            DeploymentSpecView spec = WorkspaceFixtures.Payload<DeploymentSpecListResponse>("deployment-specs.list.json").Specs.Single(s => s.SpecId == deployment.DeploymentSpecId);
            TemplateSetResponse set = WorkspaceFixtures.Payload<TemplateSetResponse>("template-set.get.json");
            Assert.That(spec.TemplateSetId, Is.EqualTo(set.TemplateSetId), "the fixtures chain");
            StartupCheck check = StartupCommand.ForDeployments(new[] { Member(deployment.FriendlyName, set) });
            Assert.That(check.Skipped, Does.Contain("deployment eu-1: template set Default names no process to launch"));
        }
    }
}
