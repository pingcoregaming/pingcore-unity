using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PingCore.Editor.Build;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace PingCore.Editor.Tests.Build
{
    /// <summary>
    /// Putting the Editor back after a server build, as a pure plan plus its steps against a fake. The regression:
    /// <c>BuildPlayer(BuildPlayerWithProfileOptions)</c> left the Dedicated Server profile ACTIVE, Unity kept
    /// it across restarts, and the next Editor played the client scene with missing scripts. The profile must be put back
    /// first, because a target switch made while a profile is active is undone at the next start.
    /// </summary>
    public sealed class TargetRestoreTests
    {
        private const BuildTargetGroup S = BuildTargetGroup.Standalone;
        private const string ServerProfile = "Assets/Settings/Build Profiles/Server.asset";

        private sealed class FakeSettings : IActiveTargetSettings
        {
            public readonly List<string> Calls = new List<string>();
            public bool ThrowOnProfile;
            public bool RefuseSwitch;
            public BuildProfile ProfileSet;
            public bool ProfileWasSet;

            public void SetActiveProfile(BuildProfile profile)
            {
                Calls.Add("profile " + (profile == null ? "none" : profile.name));
                ProfileWasSet = true;
                ProfileSet = profile;
                if (ThrowOnProfile)
                {
                    throw new InvalidOperationException("planted");
                }
            }

            public void SetStandaloneSubtarget(StandaloneBuildSubtarget subtarget) => Calls.Add("subtarget " + subtarget);

            public bool SwitchTarget(BuildTargetGroup group, BuildTarget target)
            {
                Calls.Add("switch " + target);
                return !RefuseSwitch;
            }
        }

        private static ActiveTarget WindowsPlayer(BuildProfile profile = null) => new ActiveTarget(S, BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, profile);

        [Test]
        public void AProfileBuildFromAWindowsPlayerEditorPutsTheProfileBackFirstThenTheSubtargetThenTheTarget()
        {
            // Unity 6000.4.10f1: before Win64 / Player with no profile; after Linux64 / Server with the server profile active.
            TargetRestorePlan plan = TargetRestorePlan.For(S, BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, null,
                S, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, ServerProfile);
            Assert.That((plan.Changed, plan.RestoreProfile, plan.RestoreSubtarget, plan.SwitchTarget), Is.EqualTo((true, true, true, true)));

            var settings = new FakeSettings();
            Assert.That(TargetRestoreSteps.Apply(plan, WindowsPlayer(), settings), Is.Null);
            Assert.That(settings.Calls, Is.EqualTo(new[] { "profile none", "subtarget Player", "switch StandaloneWindows64" }),
                "[mutation: switch the target before the profile] a switch made while the profile is active comes back at the next start");
        }

        [Test]
        public void TheCommandLinesOwnCaseOnlyDeactivatesTheProfileTheBuildMadeActive()
        {
            // A command-line build launches on Linux64 / Server; the build then made the profile active (what the next Editor opened on).
            TargetRestorePlan plan = TargetRestorePlan.For(S, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, null,
                S, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, ServerProfile);
            Assert.That((plan.Changed, plan.RestoreProfile, plan.RestoreSubtarget, plan.SwitchTarget), Is.EqualTo((true, true, false, false)),
                "[mutation: compare only target and subtarget] the profile alone moved, and that is the change that outlives a restart");

            var settings = new FakeSettings();
            Assert.That(TargetRestoreSteps.Apply(plan, new ActiveTarget(S, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, null), settings), Is.Null);
            Assert.That(settings.Calls, Is.EqualTo(new[] { "profile none" }));
        }

        [Test]
        public void ABuildThatFoundTheServerProfileActiveLeavesItExactlyAsItWas()
        {
            TargetRestorePlan plan = TargetRestorePlan.For(S, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, ServerProfile,
                S, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, ServerProfile);
            Assert.That(plan.Changed, Is.False, "the plugin never switches a profile the developer chose");
            var settings = new FakeSettings();
            Assert.That(TargetRestoreSteps.Apply(plan, WindowsPlayer(), settings), Is.Null);
            Assert.That(settings.Calls, Is.Empty);
        }

        [Test]
        public void AnotherActiveProfileIsPutBackAsThatProfileNotAsNone()
        {
            BuildProfile player = ScriptableObject.CreateInstance<BuildProfile>();
            try
            {
                player.name = "Windows Player";
                TargetRestorePlan plan = TargetRestorePlan.For(S, BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, "Assets/Windows Player.asset",
                    S, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, ServerProfile);
                var settings = new FakeSettings();
                Assert.That(TargetRestoreSteps.Apply(plan, WindowsPlayer(player), settings), Is.Null);
                Assert.That(settings.ProfileSet, Is.SameAs(player));
                Assert.That(settings.Calls[0], Is.EqualTo("profile Windows Player"));
            }
            finally
            {
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void AFailingStepIsNamedAndTheLaterStepsStillRun()
        {
            TargetRestorePlan plan = TargetRestorePlan.For(S, BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, null,
                S, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, ServerProfile);
            var settings = new FakeSettings { ThrowOnProfile = true, RefuseSwitch = true };
            string failed = TargetRestoreSteps.Apply(plan, WindowsPlayer(), settings);
            Assert.That(failed, Is.EqualTo("the active build profile (InvalidOperationException); the build target (InvalidOperationException)"));
            Assert.That(settings.Calls, Is.EqualTo(new[] { "profile none", "subtarget Player", "switch StandaloneWindows64" }),
                "[mutation: stop at the first failure] this session still gets its target back");
            Assert.That(failed, Does.Not.Contain("planted"), "an exception's message never reaches the sentence, only its type");
        }

        [TestCase(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, true, TestName = "target and subtarget moved (Unity 6000.4.10f1)")]
        [TestCase(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Player, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, true, TestName = "only the subtarget moved")]
        [TestCase(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, false, TestName = "nothing moved")]
        public void TheTargetIsSwitchedOnlyWhenTheBuildMovedIt(BuildTarget before, StandaloneBuildSubtarget subBefore, BuildTarget after, StandaloneBuildSubtarget subAfter, bool moved)
        {
            TargetRestorePlan plan = TargetRestorePlan.For(S, before, subBefore, null, S, after, subAfter, null);
            Assert.That((plan.SwitchTarget, plan.RestoreSubtarget, plan.RestoreProfile), Is.EqualTo((moved, moved, false)));
        }

        [Test]
        public void TheSubtargetMeansNothingOffStandalone()
        {
            TargetRestorePlan plan = TargetRestorePlan.For(BuildTargetGroup.Android, BuildTarget.Android, StandaloneBuildSubtarget.Player, null,
                BuildTargetGroup.Android, BuildTarget.Android, StandaloneBuildSubtarget.Server, null);
            Assert.That(plan.Changed, Is.False);
            TargetRestorePlan back = TargetRestorePlan.For(BuildTargetGroup.Android, BuildTarget.Android, StandaloneBuildSubtarget.Player, null,
                S, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, null);
            Assert.That((back.SwitchTarget, back.RestoreSubtarget), Is.EqualTo((true, false)), "no standalone subtarget is written for an Android Editor");
        }

        private static ActiveTarget LinuxServer(BuildProfile profile = null) => new ActiveTarget(S, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, profile);

        [Test]
        public void TheCommandLinePutsTheProfileItsBuildActivatedBackWithNoMarker()
        {
            // A command-line profile build that left the Editor on the server profile: launched on Linux64 / Server, no profile.
            BuildProfile server = ScriptableObject.CreateInstance<BuildProfile>();
            try
            {
                server.name = "Beacon Rush Server";
                var settings = new FakeSettings();
                var markers = new List<string>();
                ActiveTargetRestore restore = ActiveTargetRestore.Run(LinuxServer(), "[test] ", false, () => LinuxServer(server), settings, markers.Add);
                Assert.That((restore.Changed, restore.Switched), Is.EqualTo((true, true)), "[mutation: restore only in-process] the command line puts it back too");
                Assert.That(settings.Calls, Is.EqualTo(new[] { "profile none" }));
                Assert.That(markers, Is.Empty, "no marker: a -executeMethod run never ticks, so no reload would ever read it");
                Assert.That(restore.Message, Does.Contain("build profile Beacon Rush Server").And.Not.Contain("recompile"));
                Assert.That(restore.ReloadFollows, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(server);
            }
        }

        [Test]
        public void AnInEditorBuildThatMovedTheTargetLeavesTheMarkerTheReloadReads()
        {
            var settings = new FakeSettings();
            var markers = new List<string>();
            ActiveTargetRestore restore = ActiveTargetRestore.Run(WindowsPlayer(), "[test] ", true, () => LinuxServer(), settings, markers.Add);
            Assert.That(settings.Calls, Is.EqualTo(new[] { "subtarget Player", "switch StandaloneWindows64" }));
            Assert.That(markers, Is.EqualTo(new[] { "StandaloneWindows64 (Player)" }));
            Assert.That(restore.Message, Does.EndWith("Its scripts recompile in a moment."));
            Assert.That(restore.ReloadFollows, Is.True);

            var none = new List<string>();
            ActiveTargetRestore same = ActiveTargetRestore.Run(LinuxServer(), "[test] ", true, () => LinuxServer(), new FakeSettings(), none.Add);
            Assert.That((same.Changed, same.Message, none.Count), Is.EqualTo((false, (string)null, 0)), "nothing moved: nothing written, nothing said");
        }

        [Test]
        public void TheCommandLineSwitchesATargetBackWithNoMarker()
        {
            // A script that launched on Win64 / Player: the build leaves Linux64 / Server, the settings go back, no reload reads a marker.
            var settings = new FakeSettings();
            var markers = new List<string>();
            ActiveTargetRestore restore = ActiveTargetRestore.Run(WindowsPlayer(), "[test] ", false, () => LinuxServer(), settings, markers.Add);
            Assert.That(settings.Calls, Is.EqualTo(new[] { "subtarget Player", "switch StandaloneWindows64" }));
            Assert.That((restore.Switched, restore.ReloadFollows, markers.Count), Is.EqualTo((true, false, 0)), "[mutation: the marker on every switch] only an in-Editor build reloads");
            Assert.That(restore.Message, Does.Not.Contain("recompile"));
        }

        [Test]
        public void AnInEditorProfileOnlyPutBackLeavesNoMarker()
        {
            BuildProfile server = ScriptableObject.CreateInstance<BuildProfile>();
            try
            {
                server.name = "Beacon Rush Server";
                var settings = new FakeSettings();
                var markers = new List<string>();
                ActiveTargetRestore restore = ActiveTargetRestore.Run(LinuxServer(), "[test] ", true, () => LinuxServer(server), settings, markers.Add);
                Assert.That(settings.Calls, Is.EqualTo(new[] { "profile none" }));
                Assert.That((restore.Switched, restore.ReloadFollows, markers.Count), Is.EqualTo((true, false, 0)),
                    "[mutation: the marker on every in-Editor put-back] no target switch, no reload to read it");
                Assert.That(restore.Message, Does.Not.Contain("recompile"));
            }
            finally
            {
                Object.DestroyImmediate(server);
            }
        }

        [Test]
        public void AFailedPutBackIsAnErrorNamingTheStepAndLeavesNoMarker()
        {
            var settings = new FakeSettings { RefuseSwitch = true };
            var markers = new List<string>();
            LogAssert.Expect(LogType.Error, new Regex(@"could not be put back on StandaloneWindows64 \(Player\): the build target \(InvalidOperationException\) failed; switch it in File > Build Profiles\.$"));
            ActiveTargetRestore restore = ActiveTargetRestore.Run(WindowsPlayer(), "[test] ", true, () => LinuxServer(), settings, markers.Add);
            Assert.That((restore.Changed, restore.Switched, markers.Count), Is.EqualTo((true, false, 0)));
        }

        [Test]
        public void EveryServerBuildPutsTheEditorBackBeforeItsHousekeeping()
        {
            // The regression behind a blank client scene was a restore that ran only for in-process builds.
            PackageInfo package = PackageInfo.FindForAssembly(typeof(ServerBuilder).Assembly);
            Assert.That(package, Is.Not.Null, "the PingCore.Editor package");
            string source = File.ReadAllText(Path.Combine(package.resolvedPath, "Editor", "Build", "ServerBuilder.cs"));
            Assert.That(source, Does.Contain("ActiveTarget targetBefore = ActiveTarget.Capture();"), "[mutation: capture only in-process]");
            int run = source.IndexOf("restore = ActiveTargetRestore.Run(targetBefore, logPrefix, options.InProcess);", StringComparison.Ordinal);
            int finish = source.IndexOf("restored = housekeeping.Finish(logPrefix);", StringComparison.Ordinal);
            Assert.That(run, Is.GreaterThan(0), "[mutation: if (options.InProcess) around the restore]");
            Assert.That(finish, Is.GreaterThan(run), "the housekeeping saves and checks after anything the put-back touched");
            Assert.That(source.Substring(source.LastIndexOf("finally", run, StringComparison.Ordinal), run - source.LastIndexOf("finally", run, StringComparison.Ordinal)),
                Does.Not.Contain("if (options.InProcess)"), "inside the finally, unconditionally");
        }

        [TestCase("Assets/Settings/Build Profiles/Server.asset", 42, "Assets/Settings/Build Profiles/Server.asset", TestName = "a profile asset is keyed by its path, whatever its instance")]
        [TestCase("", 42, "#42", TestName = "a profile that is no asset is keyed by its instance")]
        [TestCase(null, 7, "#7", TestName = "a profile with no path at all is keyed by its instance")]
        public void AProfileIsKeyedByItsAssetPathElseItsInstance(string path, int instanceId, string key)
        {
            Assert.That(ActiveTargetRestore.ProfileKey(path, instanceId), Is.EqualTo(key));
        }

        [Test]
        public void AProfileThatIsNoAssetIsKeyedByItsInstance()
        {
            BuildProfile loose = ScriptableObject.CreateInstance<BuildProfile>();
            try
            {
                Assert.That(ActiveTargetRestore.ProfileKey(null), Is.Null);
                Assert.That(ActiveTargetRestore.ProfileKey(loose), Is.EqualTo("#" + loose.GetInstanceID()), "a profile that is no asset falls back to its instance");
            }
            finally
            {
                Object.DestroyImmediate(loose);
            }
        }

        [Test]
        public void NothingIsAppliedWithoutAPlanOrACapture()
        {
            var settings = new FakeSettings();
            TargetRestorePlan plan = TargetRestorePlan.For(S, BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, null, S, BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, null);
            Assert.That(TargetRestoreSteps.Apply(null, WindowsPlayer(), settings), Is.Null);
            Assert.That(TargetRestoreSteps.Apply(plan, null, settings), Is.Null);
            Assert.That(settings.Calls, Is.Empty);
        }
    }
}
