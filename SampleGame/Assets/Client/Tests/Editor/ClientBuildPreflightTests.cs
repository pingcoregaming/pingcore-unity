using System.Collections.Generic;
using BeaconRush.Editor;
using NUnit.Framework;
using PingCore.Editor.Cli;
using UnityEditor;

namespace BeaconRush.Client.Tests
{
    /// <summary>
    /// <see cref="ClientBuild"/>'s checks before it builds: the editor must be on StandaloneWindows64 with the Player
    /// subtarget and compiled without <c>UNITY_SERVER</c>, and the client scene must have no missing scripts.
    /// </summary>
    public sealed class ClientBuildPreflightTests
    {
        [Test]
        public void TheWindowsPlayerTargetCompiledForTheClientPasses()
        {
            Assert.That(ClientBuildPreflight.TargetProblem(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, false), Is.Null);
        }

        [Test]
        public void TheServerSubtargetLeftByAServerBuildFailsAndNamesTheRelaunchFlags()
        {
            string problem = ClientBuildPreflight.TargetProblem(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, true);
            Assert.That(problem, Does.StartWith("WRONG BUILD TARGET: the editor's scripts are compiled with UNITY_SERVER"));
            Assert.That(problem, Does.Contain("BeaconRush.Client (!UNITY_SERVER) is not compiled in this editor"));
            Assert.That(problem, Does.Contain("-buildTarget Win64 -standaloneBuildSubtarget Player"));
        }

        [Test]
        public void ScriptsCompiledForTheServerFailEvenWhenTheSettingsAlreadySayPlayer()
        {
            // A switch made inside -executeMethod changes the settings, not the scripts this editor has loaded.
            Assert.That(ClientBuildPreflight.TargetProblem(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, true), Does.Contain("compiled with UNITY_SERVER"));
        }

        [TestCase(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Server, "StandaloneWindows64 with the Server subtarget")]
        [TestCase(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Player, "StandaloneLinux64 with the Player subtarget")]
        [TestCase(BuildTarget.StandaloneOSX, StandaloneBuildSubtarget.Player, "StandaloneOSX with the Player subtarget")]
        public void AnyOtherActiveTargetFailsAndNamesIt(BuildTarget target, StandaloneBuildSubtarget subtarget, string named)
        {
            string problem = ClientBuildPreflight.TargetProblem(target, subtarget, false);
            Assert.That(problem, Does.StartWith("WRONG BUILD TARGET: the editor's active build target is " + named + ", not StandaloneWindows64 with Player"));
            Assert.That(problem, Does.EndWith("Relaunch the editor with -buildTarget Win64 -standaloneBuildSubtarget Player (a switch inside -executeMethod takes effect only after the method returns)"));
        }

        [Test]
        public void MissingScriptsFailNamingTheSceneAndEveryObject()
        {
            var findings = new List<MissingScriptFinding>
            {
                new MissingScriptFinding(ClientBuildArgs.ScenePath, "BeaconRushClient", 3),
            };

            Assert.That(ClientBuildPreflight.MissingScriptProblem(ClientBuildArgs.ScenePath, findings), Does.StartWith(
                "MISSING SCRIPTS in Assets/Client/Scenes/Client.unity or a prefab it uses (Assets/Client/Scenes/Client.unity: 3 missing scripts on BeaconRushClient)"));
            Assert.That(ClientBuildPreflight.MissingScriptProblem(ClientBuildArgs.ScenePath, new List<MissingScriptFinding>()), Is.Null);
        }

        [Test]
        public void TheClientSceneAndItsPrefabsHaveNoMissingScriptsInThePlayerCompiledEditor()
        {
            // This assembly compiles only without UNITY_SERVER, so the editor running it is always the one ClientBuild needs.
            // For the same reason an assertion on ClientBuildPreflight.EditorCompiledForServer here could never fail, so
            // there is none: the target table tests above cover the server-compiled case through TargetProblem.
            IReadOnlyList<MissingScriptFinding> findings = MissingScriptScan.ScanScenes(new[] { ClientBuildArgs.ScenePath });
            Assert.That(findings, Is.Empty, MissingScriptScan.Describe(findings));
        }
    }
}
