using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.UI.Ship;

namespace PingCore.Editor.Workspace.Tests.UI
{
    /// <summary>
    /// The file Ship's Build row writes, as a table over the startup command read Push uses: one process name, several
    /// with and without a pick (and a pick the game no longer launches), none, and one the plugin cannot check. Never the
    /// branch's default deployment spec, and never the product name while the game names a file.
    /// </summary>
    public sealed class BuildExecutableChoiceTests
    {
        private const string Product = "Sample Game";

        private static StartupCandidate Candidate(string processName, params string[] sources)
            => new StartupCandidate(StartupCommand.Parse(processName, out _), sources);

        private static StartupCheck Several(bool anyOf) => StartupCheck.Found(new[]
        {
            Candidate("./BeaconRushServer.x86_64", "template set Linux"),
            Candidate("./BeaconRushBeta.x86_64", "template set Beta"),
        }, anyOf, null);

        [Test]
        public void OneProcessNameNamesTheBuildAfterTheFileItLaunches()
        {
            BuildExecutableChoice choice = BuildExecutableChoice.Decide(StartupCheck.Found(StartupCommand.Parse("./BeaconRushServer.x86_64 -batchmode", out _)), null, Product);
            Assert.That(choice.Executable, Is.EqualTo("BeaconRushServer.x86_64"), "[mutation: fall back to the product name] the game servers start ./BeaconRushServer.x86_64");
            Assert.That(choice.NeedsPick, Is.False);
            Assert.That(choice.Choices, Is.Empty, "no picker for one file");
            Assert.That(choice.Line, Is.EqualTo("The build writes BeaconRushServer.x86_64, the file your game launches (./BeaconRushServer.x86_64)."));
        }

        [TestCase(true, " Push accepts a build holding any one of them.", TestName = "Server executable: a pick among the template sets' files")]
        [TestCase(false, " Push needs every one of them in the build.", TestName = "Server executable: a pick among the deployments' files")]
        public void SeveralProcessNamesUseThePickedOne(bool anyOf, string push)
        {
            BuildExecutableChoice choice = BuildExecutableChoice.Decide(Several(anyOf), "./BeaconRushBeta.x86_64", Product);
            Assert.That(choice.Executable, Is.EqualTo("BeaconRushBeta.x86_64"));
            Assert.That(choice.Choices.Select(c => c.Label()), Is.EqualTo(new[] { "./BeaconRushServer.x86_64 (template set Linux)", "./BeaconRushBeta.x86_64 (template set Beta)" }), "the picker lists each file and who launches it");
            Assert.That(choice.Line, Does.StartWith("The build writes BeaconRushBeta.x86_64, the file you picked").And.EndWith(push));

            Assert.That(BuildExecutableChoice.Decide(Several(anyOf), "BeaconRushBeta.x86_64 -batchmode", Product).Executable, Is.EqualTo("BeaconRushBeta.x86_64"), "a pick matches by the file it launches");
        }

        [Test]
        public void APickedFileWithASpaceIsKeptQuotedAndReadBackAsTheSameFile()
        {
            StartupCheck check = StartupCheck.Found(new[] { Candidate("\"./My Game.x86_64\" -batchmode", "template set A"), Candidate("./Other.x86_64", "template set B") }, true, null);
            string kept = BuildExecutableChoice.ProcessNameFor(check.Candidates[0].Executable);
            Assert.That(kept, Is.EqualTo("\"./My Game.x86_64\""), "[mutation: keep the unquoted written form, which reads back as ./My]");
            Assert.That(BuildExecutableChoice.PickedPath(kept), Is.EqualTo("My Game.x86_64"));
            Assert.That(BuildExecutableChoice.Decide(check, kept, Product).NeedsPick, Is.False, "the pick is found again");
            Assert.That(BuildExecutableChoice.ProcessNameFor(check.Candidates[1].Executable), Is.EqualTo("./Other.x86_64"), "a name without a space is kept as written");
        }

        [Test]
        public void SeveralProcessNamesWithNoPickWaitForOne()
        {
            BuildExecutableChoice choice = BuildExecutableChoice.Decide(Several(true), null, Product);
            Assert.That(choice.NeedsPick, Is.True, "[mutation: build the first file]");
            Assert.That(choice.Executable, Is.Null);
            Assert.That(choice.Choices.Count, Is.EqualTo(2));
            Assert.That(choice.Line, Is.EqualTo("Your game launches different files (./BeaconRushServer.x86_64 (template set Linux), ./BeaconRushBeta.x86_64 (template set Beta)). Pick the one this build writes under Server executable, then press Build."));

            BuildExecutableChoice stale = BuildExecutableChoice.Decide(Several(false), "./Gone.x86_64", Product);
            Assert.That(stale.NeedsPick, Is.True, "a pick the game no longer launches is no pick");
            Assert.That(stale.Line, Does.Contain("The file you picked, ./Gone.x86_64, is no longer one of them."));
        }

        public static IEnumerable<TestCaseData> NoStartupCommand()
        {
            yield return new TestCaseData(null, "No startup command found for this game; the executable is named SampleGame.x86_64. Make sure your game's startup command launches that file.")
                .SetName("Server executable: not read falls back to the product name");
            yield return new TestCaseData(StartupCheck.Skip("no deployment of the fleet names a process to launch."),
                    "No startup command found for this game; the executable is named SampleGame.x86_64. Make sure your game's startup command launches that file. The startup-command check was skipped: no deployment of the fleet names a process to launch.")
                .SetName("Server executable: a skipped read falls back to the product name and says why");
            yield return new TestCaseData(StartupCheck.Refused("Deployment eu-1: Your game launches /opt/Game, a path outside the build."),
                    "The build writes SampleGame.x86_64 (the project's product name): Deployment eu-1: Your game launches /opt/Game, a path outside the build.")
                .SetName("Server executable: a startup command the plugin cannot check falls back and says why");
        }

        [TestCaseSource(nameof(NoStartupCommand))]
        public void NoUsableStartupCommandFallsBackToTheProductNameAndSaysSoPlainly(StartupCheck check, string line)
        {
            BuildExecutableChoice choice = BuildExecutableChoice.Decide(check, "./Ignored.x86_64", Product);
            Assert.That(choice.Executable, Is.EqualTo("SampleGame.x86_64"), "the product name made safe");
            Assert.That(choice.NeedsPick, Is.False);
            Assert.That(choice.Line, Is.EqualTo(line));
        }

        [Test]
        public void AFileThatIsNotAPlainPathInsideABuildFallsBackToTheProductName()
        {
            BuildExecutableChoice choice = BuildExecutableChoice.Decide(StartupCheck.Found(StartupCommand.Parse("\"./My Game.x86_64\" -batchmode", out _)), null, Product);
            Assert.That(choice.Executable, Is.EqualTo("SampleGame.x86_64"));
            Assert.That(choice.Line, Does.Contain("./My Game.x86_64 is not a plain path inside a build").And.EndWith("Make sure your game's startup command launches SampleGame.x86_64."));
        }
    }
}
