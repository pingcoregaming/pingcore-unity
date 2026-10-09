using System.Linq;
using NUnit.Framework;
using PingCore.Editor.Build;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEngine;

namespace PingCore.Editor.Tests.Cli
{
    /// <summary>
    /// Which Build Profiles can build a PingCore game server (Linux x86_64 on the Dedicated Server subtarget only),
    /// and the options an in-Editor profile build is given. That a real project's profile is found and builds is
    /// proven in the sample (<c>SampleGame/Assets/Client/Tests/Editor/ServerBuildProfileTests.cs</c>) and by a real
    /// profile build, not here: this package's tests must not depend on the project they run in.
    /// </summary>
    public sealed class BuildProfilesTests
    {
        [TestCase(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, true, TestName = "Linux Dedicated Server is a server profile")]
        [TestCase(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Player, false, TestName = "a Linux player is not")]
        [TestCase(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Default, false, TestName = "a Linux profile on the default subtarget is not")]
        [TestCase(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Server, false, TestName = "a Windows Dedicated Server is not")]
        [TestCase(BuildTarget.StandaloneOSX, StandaloneBuildSubtarget.Server, false, TestName = "a macOS Dedicated Server is not")]
        [TestCase(BuildTarget.LinuxHeadlessSimulation, StandaloneBuildSubtarget.Server, false, TestName = "Linux headless simulation is not")]
        [TestCase(BuildTarget.WebGL, StandaloneBuildSubtarget.Default, false, TestName = "WebGL is not")]
        public void OnlyALinuxDedicatedServerProfileIsAServerProfile(BuildTarget target, StandaloneBuildSubtarget subtarget, bool server)
        {
            Assert.That(BuildProfiles.IsLinuxDedicatedServer((int)target, (int)subtarget), Is.EqualTo(server));
            Assert.That(new BuildProfileInfo("Assets/P.asset", (int)target, (int)subtarget).IsLinuxDedicatedServer, Is.EqualTo(server));
        }

        [Test]
        public void TheListKeepsOnlyServerProfilesSortedByPath()
        {
            var profiles = new[]
            {
                new BuildProfileInfo("Assets/Z Server.asset", (int)BuildTarget.StandaloneLinux64, (int)StandaloneBuildSubtarget.Server),
                new BuildProfileInfo("Assets/Client.asset", (int)BuildTarget.StandaloneWindows64, (int)StandaloneBuildSubtarget.Player),
                new BuildProfileInfo("Assets/A Server.asset", (int)BuildTarget.StandaloneLinux64, (int)StandaloneBuildSubtarget.Server),
                new BuildProfileInfo("Assets/Linux Player.asset", (int)BuildTarget.StandaloneLinux64, (int)StandaloneBuildSubtarget.Player),
                null,
                new BuildProfileInfo(null, (int)BuildTarget.StandaloneLinux64, (int)StandaloneBuildSubtarget.Server),
            };

            Assert.That(BuildProfiles.LinuxDedicatedServer(profiles).Select(p => p.Path), Is.EqualTo(new[] { "Assets/A Server.asset", "Assets/Z Server.asset" }));
            Assert.That(BuildProfiles.LinuxDedicatedServer(null), Is.Empty);
        }

        [Test]
        public void AProfilesPlatformIsReadFromItsSerializedTargetAndSubtarget()
        {
            // An in-memory profile set to Linux Dedicated Server through the same serialized fields Describe reads.
            BuildProfile profile = ScriptableObject.CreateInstance<BuildProfile>();
            try
            {
                using (var serialized = new SerializedObject(profile))
                {
                    serialized.FindProperty("m_BuildTarget").intValue = (int)BuildTarget.StandaloneLinux64;
                    serialized.FindProperty("m_Subtarget").intValue = (int)StandaloneBuildSubtarget.Server;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }

                BuildProfileInfo info = BuildProfiles.Describe(profile, "Assets/Linux Server.asset");
                Assert.That(info, Is.Not.Null, "[mutation: the serialized field names changed]");
                Assert.That(info.IsLinuxDedicatedServer, Is.True);

                using (var serialized = new SerializedObject(profile))
                {
                    serialized.FindProperty("m_Subtarget").intValue = (int)StandaloneBuildSubtarget.Player;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }

                Assert.That(BuildProfiles.Describe(profile, "Assets/Linux Server.asset").IsLinuxDedicatedServer, Is.False, "a Linux player profile is not a server");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void AMissingProfileIsRefusedWithItsPath()
        {
            Assert.That(BuildProfiles.LoadServerProfile("Assets/NoSuchFolder/None.asset", out string problem), Is.Null);
            Assert.That(problem, Is.EqualTo("build profile not found: Assets/NoSuchFolder/None.asset"));
            Assert.That(BuildProfiles.LoadServerProfile("Packages/x.asset", out problem), Is.Null);
            Assert.That(problem, Does.Contain("must be a project path under Assets/"));
        }

        [Test]
        public void AnEditorProfileBuildWritesTheGivenExecutableUnderItsVersionFolder()
        {
            ServerBuildOptions options = ServerBuildOptions.ForProfile("2026.10.08-abc1234", "Assets/Server.asset", "BeaconRush.x86_64");
            Assert.That(options.IsValid, Is.True, string.Join("; ", options.Errors));
            Assert.That(options.InProcess, Is.True);
            Assert.That(options.Args.UsesBuildProfile, Is.True);
            Assert.That(options.Args.Defines, Is.Empty, "extra defines stay command-line only");
            Assert.That(options.ExecutablePath, Is.EqualTo("Builds/Server/2026.10.08-abc1234/BeaconRush.x86_64"));
            Assert.That(options.VersionFilePath, Is.EqualTo("Builds/Server/2026.10.08-abc1234/version.txt"));
            Assert.That(ServerBuildOptions.ForProfile("v1", "Assets/S.asset", "bin/Server.x86_64").ExecutablePath, Is.EqualTo("Builds/Server/v1/bin/Server.x86_64"));
        }

        [TestCase("BeaconRush.x86_64", null, TestName = "a plain name")]
        [TestCase("bin/Server.x86_64", null, TestName = "a path inside the build")]
        [TestCase("", "empty", TestName = "an empty name")]
        [TestCase("../Server.x86_64", "no . or .. part", TestName = "a path that climbs out")]
        [TestCase("./Server.x86_64", "no . or .. part", TestName = "a dot part")]
        [TestCase("bin\\Server.x86_64", "forward slashes", TestName = "a backslash")]
        [TestCase("My Game.x86_64", "letters, digits", TestName = "a space")]
        [TestCase("a/b/c/d/e.x86_64", "at most four deep", TestName = "five parts deep")]
        public void TheExecutableIsAPathInsideTheBuild(string executable, string problem)
        {
            string actual = ServerBuildOptions.ExecutableProblem(executable);
            if (problem == null)
            {
                Assert.That(actual, Is.Null);
            }
            else
            {
                Assert.That(actual, Does.Contain(problem));
                Assert.That(ServerBuildOptions.ForProfile("v1", "Assets/S.asset", executable).IsValid, Is.False);
            }
        }

        [TestCase("Beacon Rush", "BeaconRush.x86_64")]
        [TestCase("My-Game_2.0", "My-Game_2.0.x86_64")]
        [TestCase("...hidden", "hidden.x86_64")]
        [TestCase("!!!", "Server.x86_64")]
        [TestCase(null, "Server.x86_64")]
        public void TheProductNameBecomesASafeExecutableName(string product, string executable)
        {
            Assert.That(ServerBuildOptions.ExecutableFromProduct(product), Is.EqualTo(executable));
            Assert.That(ServerBuildOptions.ExecutableProblem(ServerBuildOptions.ExecutableFromProduct(product)), Is.Null);
        }
    }
}
