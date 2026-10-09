using NUnit.Framework;
using PingCore.Editor.Build;
using UnityEditor;

namespace PingCore.Editor.Tests.Build
{
    /// <summary>
    /// Whether the Editor sits on a Dedicated Server profile or subtarget (which compiles every player-only script out of
    /// it), as a pure function over the profile and subtarget values, and the two sentences it gives: Window &gt; PingCore's
    /// line and the one Play mode error.
    /// </summary>
    public sealed class ServerTargetCheckTests
    {
        private const BuildTargetGroup S = BuildTargetGroup.Standalone;
        private const int Server = (int)StandaloneBuildSubtarget.Server;
        private const int Player = (int)StandaloneBuildSubtarget.Player;

        [TestCase(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, "Beacon Rush Server", Server, true, "Beacon Rush Server", TestName = "the Linux Dedicated Server profile is active")]
        [TestCase(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Server, "Windows Server", Server, true, "Windows Server", TestName = "a Windows Dedicated Server profile counts too")]
        [TestCase(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, null, null, true, null, TestName = "the platform's own Linux Server settings, no profile")]
        [TestCase(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, null, null, false, null, TestName = "a Windows player is fine")]
        [TestCase(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, "Windows Player", Player, false, null, TestName = "a player profile is fine")]
        [TestCase(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, "Odd", Player, false, null, TestName = "a known player profile decides alone, whatever a stale subtarget says")]
        [TestCase(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, "Unknown", null, true, null, TestName = "a profile whose subtarget cannot be read falls back to the subtarget")]
        public void TheServerTargetIsTheProfileOrTheSubtarget(BuildTarget target, StandaloneBuildSubtarget subtarget, string profile, int? profileSubtarget, bool server, string named)
        {
            ServerTargetState state = ServerTargetCheck.Evaluate(S, target, subtarget, profile, profileSubtarget);
            Assert.That((state.IsServer, state.ProfileName), Is.EqualTo((server, named)));
        }

        [Test]
        public void TheSubtargetMeansNothingOffStandalone()
        {
            ServerTargetState state = ServerTargetCheck.Evaluate(BuildTargetGroup.Android, BuildTarget.Android, StandaloneBuildSubtarget.Server, null, null);
            Assert.That(state.IsServer, Is.False);
            Assert.That(ServerTargetCheck.WindowMessage(state), Is.Null);
        }

        [Test]
        public void TheWindowNamesTheProfileAndSendsTheDeveloperToBuildProfiles()
        {
            ServerTargetState profile = ServerTargetCheck.Evaluate(S, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, "Beacon Rush Server", Server);
            Assert.That(ServerTargetCheck.WindowMessage(profile), Is.EqualTo(
                "Your active build profile is Beacon Rush Server, a dedicated server profile, so client scenes cannot run in Play mode. Switch to a player profile in Build Profiles."));

            ServerTargetState platform = ServerTargetCheck.Evaluate(S, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, null, null);
            Assert.That(ServerTargetCheck.WindowMessage(platform), Is.EqualTo(
                "Your active build target is Linux Dedicated Server, so client scenes cannot run in Play mode. Switch to a player profile in Build Profiles."));

            Assert.That(ServerTargetCheck.WindowMessage(ServerTargetCheck.Evaluate(S, BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, null, null)), Is.Null);
            Assert.That(ServerTargetCheck.WindowMessage(null), Is.Null);
        }

        [Test]
        public void PlayModeLogsOneErrorOnlyOnAServerTargetWithMissingScripts()
        {
            ServerTargetState server = ServerTargetCheck.Evaluate(S, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, "Beacon Rush Server", Server);
            string message = ServerTargetCheck.PlayModeMessage(server, 12);
            Assert.That(message, Does.StartWith("[PingCore] Play mode started with 12 components in the open scenes missing their scripts, because the active build profile is Beacon Rush Server, a dedicated server profile"));
            Assert.That(message, Does.Contain("UNITY_SERVER").And.Contain("File > Build Profiles").And.Contain("Switch Platform"), "the cause and the fix");
            Assert.That(ServerTargetCheck.PlayModeMessage(server, 1), Does.Contain("with 1 component in"));

            Assert.That(ServerTargetCheck.PlayModeMessage(server, 0), Is.Null, "[mutation: log on every Play] a game server scene played on purpose is not an error");
            ServerTargetState player = ServerTargetCheck.Evaluate(S, BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, null, null);
            Assert.That(ServerTargetCheck.PlayModeMessage(player, 12), Is.Null, "missing scripts on a player target are another problem");
            Assert.That(ServerTargetCheck.PlayModeMessage(ServerTargetCheck.Evaluate(S, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, null, null), 3),
                Does.Contain("because the active build target is Linux Dedicated Server:"));
        }
    }
}
