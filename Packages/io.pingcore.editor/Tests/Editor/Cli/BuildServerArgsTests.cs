using NUnit.Framework;
using PingCore.Editor.Build;
using PingCore.Editor.Cli;

namespace PingCore.Editor.Tests.Cli
{
    public sealed class BuildServerArgsTests
    {
        /// <summary>What Unity itself puts on the command line around the BuildServer flags; all of it is ignored.</summary>
        private static string[] Line(params string[] pingcore)
        {
            var head = new[] { "Unity.exe", "-batchmode", "-nographics", "-quit", "-projectPath", "C:/x/SampleGame", "-executeMethod", "PingCore.Editor.Cli.BuildServer.Run" };
            var tail = new[] { "-logFile", "C:/x/build.log" };
            var all = new string[head.Length + pingcore.Length + tail.Length];
            head.CopyTo(all, 0);
            pingcore.CopyTo(all, head.Length);
            tail.CopyTo(all, head.Length + pingcore.Length);
            return all;
        }

        [Test]
        public void AVersionAloneGivesThePlainBuildWithTheDefaults()
        {
            BuildServerArgs args = BuildServerArgs.Parse(Line("-pingcoreVersion", "2026.10.02-abc1234"));
            Assert.That(args.IsValid, Is.True, string.Join("; ", args.Errors));
            Assert.That(args.Defines, Is.Empty);
            Assert.That(args.OutputRoot, Is.EqualTo("Builds/Server"));
            Assert.That(args.Scenes, Is.EqualTo(new[] { "Assets/Game/Scenes/Server.unity" }));
            Assert.That(args.Product, Is.EqualTo("BeaconRushServer"));
            Assert.That(args.OutputFolder, Is.EqualTo("Builds/Server/2026.10.02-abc1234"));
            Assert.That(ServerBuildOptions.FromArgs(args).ExecutablePath, Is.EqualTo("Builds/Server/2026.10.02-abc1234/BeaconRushServer.x86_64"));
            Assert.That(ServerBuildOptions.FromArgs(args).VersionFilePath, Is.EqualTo("Builds/Server/2026.10.02-abc1234/version.txt"));
            Assert.That(args.ExtraDefines, Is.Empty);
        }

        [Test]
        public void DefinesAndAnOutputRootChangeOnlyTheDefinesAndTheFolder()
        {
            BuildServerArgs args = BuildServerArgs.Parse(Line("-pingcoreDefine", "STUDIO_DEBUG", "-PINGCOREDEFINE", "_X1", "-pingcoreOutputRoot", "Builds/Instrumented/Server", "-pingcoreVersion", "v1"));
            Assert.That(args.IsValid, Is.True, string.Join("; ", args.Errors));
            Assert.That(args.Defines, Is.EqualTo(new[] { "STUDIO_DEBUG", "_X1" }));
            Assert.That(args.ExtraDefines, Is.EqualTo(new[] { "STUDIO_DEBUG", "_X1" }));
            Assert.That(args.OutputRoot, Is.EqualTo("Builds/Instrumented/Server"));
            Assert.That(ServerBuildOptions.FromArgs(args).ExecutablePath, Is.EqualTo("Builds/Instrumented/Server/v1/BeaconRushServer.x86_64"));
            Assert.That(args.Scenes, Is.EqualTo(new[] { "Assets/Game/Scenes/Server.unity" }));
        }

        [Test]
        public void ADefineAloneKeepsTheDefaultFolder()
        {
            BuildServerArgs args = BuildServerArgs.Parse(Line("-pingcoreDefine", "STUDIO_DEBUG", "-pingcoreVersion", "v1"));
            Assert.That(args.IsValid, Is.True, string.Join("; ", args.Errors));
            Assert.That(args.OutputFolder, Is.EqualTo("Builds/Server/v1"), "the build guard decides whether that output may carry the define");
        }

        [TestCase("STUDIO-DEBUG", TestName = "a define with a dash")]
        [TestCase("1ABC", TestName = "a define starting with a digit")]
        [TestCase("A;B", TestName = "two defines in one value")]
        public void ADefineThatIsNotASymbolIsRefused(string define)
        {
            BuildServerArgs args = BuildServerArgs.Parse(Line("-pingcoreVersion", "v1", "-pingcoreDefine", define));
            Assert.That(string.Join("; ", args.Errors), Does.Contain("must be a scripting define"));
        }

        [TestCase("Server", TestName = "an output root outside Builds")]
        [TestCase("Builds", TestName = "Builds itself")]
        [TestCase("Builds/../Assets", TestName = "an output root that climbs")]
        [TestCase("Builds\\X", TestName = "an output root with backslashes")]
        [TestCase("Builds/a b", TestName = "an output root with a space")]
        [TestCase("Builds//X", TestName = "an empty part")]
        public void AnOutputRootOutsideBuildsIsRefused(string root)
        {
            BuildServerArgs args = BuildServerArgs.Parse(Line("-pingcoreVersion", "v1", "-pingcoreOutputRoot", root));
            Assert.That(string.Join("; ", args.Errors), Does.Contain("must be a folder under Builds/"));
        }

        [Test]
        public void TheSameDefineOrTwoOutputRootsAreRefused()
        {
            Assert.That(string.Join("; ", BuildServerArgs.Parse(Line("-pingcoreVersion", "v1", "-pingcoreDefine", "A", "-pingcoreDefine", "A")).Errors), Does.Contain("given twice"));
            Assert.That(string.Join("; ", BuildServerArgs.Parse(Line("-pingcoreVersion", "v1", "-pingcoreOutputRoot", "Builds/A", "-pingcoreOutputRoot", "Builds/B")).Errors), Does.Contain("given twice"));
        }

        [Test]
        public void ScenesAndProductAreTakenInOrderAndFlagsIgnoreCase()
        {
            BuildServerArgs args = BuildServerArgs.Parse(Line(
                "-PINGCOREscene", "Assets/A.unity", "-pingcoreScene", "Packages/io.example/B.unity", "-pingcoreproduct", "MyServer", "-pingcoreVersion", "1"));
            Assert.That(args.IsValid, Is.True, string.Join("; ", args.Errors));
            Assert.That(args.Scenes, Is.EqualTo(new[] { "Assets/A.unity", "Packages/io.example/B.unity" }));
            Assert.That(ServerBuildOptions.FromArgs(args).ExecutablePath, Is.EqualTo("Builds/Server/1/MyServer.x86_64"));
        }

        [TestCase(new string[0], "-pingcoreVersion is required", TestName = "no version")]
        [TestCase(new[] { "-pingcoreVersion" }, "-pingcoreVersion needs a value", TestName = "a version flag with no value")]
        [TestCase(new[] { "-pingcoreVersion", "-pingcoreDefine" }, "needs a value", TestName = "a version flag followed by another flag")]
        [TestCase(new[] { "-pingcoreVersion", "../escape" }, "must be 1 to 64", TestName = "a version that climbs out of the folder")]
        [TestCase(new[] { "-pingcoreVersion", "a/b" }, "must be 1 to 64", TestName = "a version with a slash")]
        [TestCase(new[] { "-pingcoreVersion", ".hidden" }, "must be 1 to 64", TestName = "a version starting with a dot")]
        [TestCase(new[] { "-pingcoreVersion", "v 1" }, "must be 1 to 64", TestName = "a version with a space")]
        [TestCase(new[] { "-pingcoreVersion", "1", "-pingcoreVersion", "2" }, "given twice", TestName = "two versions")]
        [TestCase(new[] { "-pingcoreVersion", "1", "-pingcoreProduct", "My Server" }, "-pingcoreProduct must be", TestName = "a product with a space")]
        [TestCase(new[] { "-pingcoreVersion", "1", "-pingcoreProduct", "a", "-pingcoreProduct", "b" }, "given twice", TestName = "two products")]
        [TestCase(new[] { "-pingcoreVersion", "1", "-pingcoreScene", "Server.unity" }, "must be a project path", TestName = "a scene outside Assets or Packages")]
        [TestCase(new[] { "-pingcoreVersion", "1", "-pingcoreScene", "Assets/../../x.unity" }, "must be a project path", TestName = "a scene path that climbs")]
        [TestCase(new[] { "-pingcoreVersion", "1", "-pingcoreScene", "Assets\\Game\\Server.unity" }, "must be a project path", TestName = "a scene path with backslashes")]
        [TestCase(new[] { "-pingcoreVersion", "1", "-pingcoreScene", "Assets/Game/Server.prefab" }, "must be a project path", TestName = "a scene that is not a .unity file")]
        [TestCase(new[] { "-pingcoreVersion", "1", "-pingcoreScene", "Assets/A.unity", "-pingcoreScene", "Assets/A.unity" }, "given twice", TestName = "the same scene twice")]
        [TestCase(new[] { "-pingcoreVersion", "1", "-pingcoreDefines", "A" }, "unknown flag -pingcoreDefines", TestName = "a mistyped pingcore flag")]
        public void UnusableArgumentsAreReported(string[] pingcore, string expected)
        {
            BuildServerArgs args = BuildServerArgs.Parse(Line(pingcore));
            Assert.That(args.IsValid, Is.False);
            Assert.That(string.Join("; ", args.Errors), Does.Contain(expected));
        }

        [Test]
        public void EveryProblemIsReportedNotOnlyTheFirst()
        {
            BuildServerArgs args = BuildServerArgs.Parse(Line("-pingcoreProduct", "bad name", "-pingcoreScene", "nope", "-pingcoreOops"));
            Assert.That(args.Errors.Count, Is.EqualTo(4), string.Join("; ", args.Errors));
        }

        [Test]
        public void NoArgumentsAtAllNeedOnlyTheVersion()
        {
            BuildServerArgs args = BuildServerArgs.Parse(null);
            Assert.That(args.Errors, Is.EqualTo(new[] { "-pingcoreVersion is required (for example 2026.10.02-abc1234)" }));
        }

        [Test]
        public void ASixtyFourCharacterVersionIsTheLongestAccepted()
        {
            Assert.That(BuildServerArgs.Parse(Line("-pingcoreVersion", new string('a', 64))).IsValid, Is.True);
            Assert.That(BuildServerArgs.Parse(Line("-pingcoreVersion", new string('a', 65))).IsValid, Is.False);
        }

        [Test]
        public void ABuildProfileTakesNoSceneAndUsesTheCallersDefaultVersion()
        {
            BuildServerArgs args = BuildServerArgs.Parse(Line("-buildProfile", "Assets/Settings/Build Profiles/Linux Server.asset"), "2026.10.08-120000");
            Assert.That(args.IsValid, Is.True, string.Join("; ", args.Errors));
            Assert.That(args.UsesBuildProfile, Is.True);
            Assert.That(args.BuildProfile, Is.EqualTo("Assets/Settings/Build Profiles/Linux Server.asset"));
            Assert.That(args.Scenes, Is.Empty, "the profile holds the scenes");
            Assert.That(args.Product, Is.Null, "the project's product name names the executable");
            Assert.That(args.Version, Is.EqualTo("2026.10.08-120000"));
            Assert.That(args.OutputFolder, Is.EqualTo("Builds/Server/2026.10.08-120000"));
            Assert.That(args.ExtraDefines, Is.Empty);
            Assert.That(ServerBuildOptions.FromArgs(args, "Beacon Rush!").ExecutablePath, Is.EqualTo("Builds/Server/2026.10.08-120000/BeaconRush.x86_64"));
        }

        [Test]
        public void ABuildProfileTakesAnExplicitVersionAndProductOverTheDefaults()
        {
            BuildServerArgs args = BuildServerArgs.Parse(Line("-BUILDPROFILE", "Assets/Server.asset", "-pingcoreVersion", "v2", "-pingcoreProduct", "MyGame"), "ignored");
            Assert.That(args.IsValid, Is.True, string.Join("; ", args.Errors));
            Assert.That(args.Version, Is.EqualTo("v2"));
            Assert.That(ServerBuildOptions.FromArgs(args, "Other").ExecutablePath, Is.EqualTo("Builds/Server/v2/MyGame.x86_64"));
        }

        [TestCase(new[] { "-buildProfile", "Assets/S.asset", "-pingcoreScene", "Assets/A.unity" }, "the build profile holds the scenes", TestName = "a scene with a build profile")]
        [TestCase(new[] { "-buildProfile", "Assets/S.asset", "-pingcoreDefine", "A" }, "the build profile holds the defines", TestName = "a define with a build profile")]
        [TestCase(new[] { "-buildProfile", "Assets/S.asset", "-pingcoreOutputRoot", "Builds/X" }, "a build profile build writes under Builds/Server", TestName = "an output root with a build profile")]
        [TestCase(new[] { "-buildProfile", "Packages/x/S.asset" }, "must be a project path under Assets/", TestName = "a build profile outside Assets")]
        [TestCase(new[] { "-buildProfile", "Assets/../S.asset" }, "must be a project path under Assets/", TestName = "a build profile path that climbs")]
        [TestCase(new[] { "-buildProfile", "Assets/Server.unity" }, "must be a project path under Assets/", TestName = "a build profile that is not an asset")]
        [TestCase(new[] { "-buildProfile", "Assets/A.asset", "-buildProfile", "Assets/B.asset" }, "given twice", TestName = "two build profiles")]
        [TestCase(new[] { "-buildProfile" }, "-buildProfile needs a value", TestName = "a build profile flag with no value")]
        public void UnusableBuildProfileArgumentsAreReported(string[] flags, string expected)
        {
            BuildServerArgs args = BuildServerArgs.Parse(Line(flags), "2026.10.08-120000");
            Assert.That(args.IsValid, Is.False);
            Assert.That(string.Join("; ", args.Errors), Does.Contain(expected));
        }

        [Test]
        public void WithoutADefaultVersionABuildProfileStillNeedsOne()
        {
            BuildServerArgs args = BuildServerArgs.Parse(Line("-buildProfile", "Assets/S.asset"));
            Assert.That(string.Join("; ", args.Errors), Does.Contain("-pingcoreVersion is required"));
        }

        [Test]
        public void TheDefaultVersionIsTheUtcDateAndTime()
        {
            Assert.That(BuildServer.DefaultVersion(new System.DateTime(2026, 10, 8, 9, 5, 7, System.DateTimeKind.Utc)), Is.EqualTo("2026.10.08-090507"));
            Assert.That(BuildServerArgs.IsValidName(BuildServer.DefaultVersion(System.DateTime.UtcNow)), Is.True);
        }
    }
}
