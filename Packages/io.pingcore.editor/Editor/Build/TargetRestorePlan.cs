using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Profile;

namespace PingCore.Editor.Build
{
    /// <summary>
    /// What putting the Editor back after a server build takes, pure: which of the three settings the build moved. The
    /// order is <see cref="TargetRestoreSteps.Apply"/>'s: the active build profile first, then the subtarget, then the
    /// target. The profile goes first because <c>BuildPipeline.BuildPlayer(BuildPlayerWithProfileOptions)</c> makes the
    /// profile it built the ACTIVE build profile and Unity keeps it across restarts, and a target switch made while a
    /// profile is active is undone at the next start, when Unity applies the profile again (both on Unity
    /// 6000.4.10f1).
    /// </summary>
    public sealed class TargetRestorePlan
    {
        private TargetRestorePlan(bool restoreProfile, bool restoreSubtarget, bool switchTarget)
        {
            RestoreProfile = restoreProfile;
            RestoreSubtarget = restoreSubtarget;
            SwitchTarget = switchTarget;
        }

        /// <summary>The build moved anything: the profile, the target or (standalone only) the subtarget.</summary>
        public bool Changed => RestoreProfile || SwitchTarget;

        /// <summary>The active build profile differs: put the one from before back (none means the platform's own settings).</summary>
        public bool RestoreProfile { get; }

        /// <summary>Set the standalone subtarget back before the switch (a standalone target only).</summary>
        public bool RestoreSubtarget { get; }

        /// <summary>The target, or a standalone target's subtarget, differs: switch the Editor back.</summary>
        public bool SwitchTarget { get; }

        /// <summary>
        /// The plan for a build that found the Editor on the first four values and left it on the last four. A profile is
        /// named by a key (its asset path), null for none. Pure.
        /// </summary>
        public static TargetRestorePlan For(BuildTargetGroup groupBefore, BuildTarget targetBefore, StandaloneBuildSubtarget subBefore, string profileBefore,
            BuildTargetGroup groupAfter, BuildTarget targetAfter, StandaloneBuildSubtarget subAfter, string profileAfter)
        {
            bool profile = !string.Equals(profileBefore, profileAfter, StringComparison.Ordinal);
            bool target = groupBefore != groupAfter || targetBefore != targetAfter || (groupBefore == BuildTargetGroup.Standalone && subBefore != subAfter);
            return new TargetRestorePlan(profile, target && groupBefore == BuildTargetGroup.Standalone, target);
        }

        public override string ToString() => $"profile {RestoreProfile}, subtarget {RestoreSubtarget}, target {SwitchTarget}";
    }

    /// <summary>The Editor settings a switch back writes; <see cref="TargetRestoreSteps"/> and <see cref="ActiveTargetRestore"/> take a fake in tests.</summary>
    public interface IActiveTargetSettings
    {
        /// <summary>Makes <paramref name="profile"/> the active build profile; null makes the platform's own settings active again.</summary>
        void SetActiveProfile(BuildProfile profile);

        void SetStandaloneSubtarget(StandaloneBuildSubtarget subtarget);

        /// <summary><c>EditorUserBuildSettings.SwitchActiveBuildTarget</c>: false when the Editor refused.</summary>
        bool SwitchTarget(BuildTargetGroup group, BuildTarget target);
    }

    /// <summary>The real Editor settings.</summary>
    public sealed class EditorActiveTargetSettings : IActiveTargetSettings
    {
        // Null is accepted: it deactivates the profile and leaves the target as it is (Unity 6000.4.10f1),
        // which is why the target switch follows it.
        public void SetActiveProfile(BuildProfile profile) => BuildProfile.SetActiveBuildProfile(profile);

        public void SetStandaloneSubtarget(StandaloneBuildSubtarget subtarget) => EditorUserBuildSettings.standaloneBuildSubtarget = subtarget;

        public bool SwitchTarget(BuildTargetGroup group, BuildTarget target) => EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);
    }

    /// <summary>
    /// Carries a <see cref="TargetRestorePlan"/> out, in its order. Every step runs even when an earlier one failed, so a
    /// profile that cannot be put back still leaves the target switched for this session, and every failure is named.
    /// </summary>
    public static class TargetRestoreSteps
    {
        /// <summary>Applies <paramref name="plan"/> towards <paramref name="before"/>; null when every step it took worked, else what failed. Never throws.</summary>
        public static string Apply(TargetRestorePlan plan, ActiveTarget before, IActiveTargetSettings settings)
        {
            if (plan == null || before == null || settings == null || !plan.Changed)
            {
                return null;
            }

            var failures = new List<string>();
            if (plan.RestoreProfile)
            {
                Step(failures, "the active build profile", () => settings.SetActiveProfile(before.Profile));
            }

            if (plan.RestoreSubtarget)
            {
                Step(failures, "the subtarget", () => settings.SetStandaloneSubtarget(before.Subtarget));
            }

            if (plan.SwitchTarget)
            {
                Step(failures, "the build target", () =>
                {
                    if (!settings.SwitchTarget(before.Group, before.Target))
                    {
                        throw new InvalidOperationException("the Editor refused the switch");
                    }
                });
            }

            return failures.Count == 0 ? null : string.Join("; ", failures);
        }

        private static void Step(List<string> failures, string what, Action step)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                failures.Add(what + " (" + e.GetType().Name + ")");
            }
        }
    }
}
