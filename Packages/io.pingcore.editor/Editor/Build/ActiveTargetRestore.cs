using System;
using System.IO;
using System.Text;
using PingCore.Editor.BuildGuard;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEngine;

namespace PingCore.Editor.Build
{
    /// <summary>The Editor's active build target as one value.</summary>
    public sealed class ActiveTarget
    {
        public ActiveTarget(BuildTargetGroup group, BuildTarget target, StandaloneBuildSubtarget subtarget, BuildProfile profile)
        {
            Group = group;
            Target = target;
            Subtarget = subtarget;
            Profile = profile;
        }

        public BuildTargetGroup Group { get; }

        public BuildTarget Target { get; }

        /// <summary>Meaningful for a standalone target only.</summary>
        public StandaloneBuildSubtarget Subtarget { get; }

        /// <summary>The active Build Profile asset, or null for the platform's own settings.</summary>
        public BuildProfile Profile { get; }

        /// <summary>The Editor's target now.</summary>
        public static ActiveTarget Capture()
        {
            BuildTarget active = EditorUserBuildSettings.activeBuildTarget;
            return new ActiveTarget(BuildPipeline.GetBuildTargetGroup(active), active, EditorUserBuildSettings.standaloneBuildSubtarget, ActiveProfileOrNull());
        }

        /// <summary>The active Build Profile asset, or null when the platform's own settings are active (or there is no profile context yet).</summary>
        public static BuildProfile ActiveProfileOrNull()
        {
            try
            {
                return BuildProfile.GetActiveBuildProfile();
            }
            catch (Exception e) when (e is InvalidOperationException || e is NullReferenceException)
            {
                // No profile context yet (a fresh Library); the platform settings are active.
                return null;
            }
        }

        /// <summary><c>StandaloneWindows64 (Player)</c>, plus the profile's name when one is active.</summary>
        public string Describe()
        {
            string text = Target.ToString();
            if (Group == BuildTargetGroup.Standalone)
            {
                text += " (" + Subtarget + ")";
            }

            return Profile == null ? text : text + ", build profile " + Profile.name;
        }
    }

    /// <summary>
    /// Puts the Editor back on the build target and build profile it had before a server build, Ship's in-Editor build
    /// and the command line's alike. <c>BuildPipeline.BuildPlayer</c> for Linux64 / Server from an Editor on another
    /// target leaves the Editor on Linux64 / Server (Unity 6000.4.10f1), and
    /// <c>BuildPlayer(BuildPlayerWithProfileOptions)</c> also makes the profile it built the active build profile, which
    /// Unity keeps across restarts. Left alone, the next domain reload compiles the Editor with
    /// <c>UNITY_SERVER</c>, every <c>!UNITY_SERVER</c> assembly drops out of it, and a client scene plays with missing
    /// scripts and no UI. <see cref="TargetRestorePlan"/> decides and <see cref="TargetRestoreSteps"/> applies; the
    /// settings read back at once, and the scripts recompile with a domain reload a few seconds after an in-Editor build
    /// returns (a <c>-executeMethod</c> run never ticks, so after the command line they recompile at the next launch).
    /// After an in-Editor build a marker in <c>Library/PingCore/</c> lets <see cref="ActiveTargetRestoreCheck"/> report
    /// the outcome after that reload.
    /// </summary>
    public sealed class ActiveTargetRestore
    {
        /// <summary>The marker, relative to the project.</summary>
        public const string MarkerRelativePath = "Library/PingCore/active-target-restore.txt";

        private ActiveTargetRestore(bool changed, bool switched, string before, string after, string message, bool reloadFollows = false)
        {
            Changed = changed;
            Switched = switched;
            ReloadFollows = reloadFollows;
            Before = before;
            After = after;
            Message = message;
        }

        /// <summary>The build moved the Editor's active target or build profile.</summary>
        public bool Changed { get; }

        /// <summary>The Editor was put back (its profile, its target or both).</summary>
        public bool Switched { get; }

        /// <summary>An in-Editor build switched the target back: the scripts recompile and the domain reloads shortly, and the marker is written.</summary>
        public bool ReloadFollows { get; }

        public string Before { get; }

        /// <summary>Where the build left the Editor.</summary>
        public string After { get; }

        /// <summary>One sentence for the log and the Ship section, or null when nothing changed.</summary>
        public string Message { get; }

        /// <summary>
        /// Puts the Editor back when the build moved it. Never throws; a failure is logged and reported.
        /// <paramref name="inProcess"/> (Ship's build) also leaves the marker the check after the domain reload reads, when
        /// the target is switched (only a target switch brings that reload).
        /// </summary>
        public static ActiveTargetRestore Run(ActiveTarget before, string logPrefix, bool inProcess = true)
        {
            return Run(before, logPrefix, inProcess, ActiveTarget.Capture, new EditorActiveTargetSettings(),
                expected => WriteMarker(BuildGuardContext.ProjectRoot, expected));
        }

        /// <summary><see cref="Run(ActiveTarget, string, bool)"/> over its seams: the capture after the build, the settings it writes and the marker writer.</summary>
        internal static ActiveTargetRestore Run(ActiveTarget before, string logPrefix, bool inProcess, Func<ActiveTarget> capture, IActiveTargetSettings settings, Action<string> writeMarker)
        {
            logPrefix = logPrefix ?? string.Empty;
            ActiveTarget after;
            try
            {
                after = capture();
            }
            catch (Exception e)
            {
                Debug.LogError(logPrefix + "could not read the Editor's build target after the build (" + e.GetType().Name + ")");
                return new ActiveTargetRestore(true, false, before?.Describe(), null, "The Editor's build target could not be read after the build; check File > Build Profiles.");
            }

            if (before == null || after == null)
            {
                return new ActiveTargetRestore(false, false, before?.Describe(), after?.Describe(), null);
            }

            TargetRestorePlan plan = TargetRestorePlan.For(before.Group, before.Target, before.Subtarget, ProfileKey(before.Profile),
                after.Group, after.Target, after.Subtarget, ProfileKey(after.Profile));
            if (!plan.Changed)
            {
                return new ActiveTargetRestore(false, false, before.Describe(), after.Describe(), null);
            }

            bool reload = inProcess && plan.SwitchTarget;
            string message = "The build left the Editor on " + after.Describe() + "; putting it back on " + before.Describe() + "."
                + (reload ? " Its scripts recompile in a moment." : string.Empty);
            Debug.Log(logPrefix + message);
            string failed = TargetRestoreSteps.Apply(plan, before, settings);
            if (failed != null)
            {
                string sentence = "The Editor could not be put back on " + before.Describe() + ": " + failed + " failed; switch it in File > Build Profiles.";
                Debug.LogError(logPrefix + sentence);
                return new ActiveTargetRestore(true, false, before.Describe(), after.Describe(), sentence);
            }

            if (reload)
            {
                try
                {
                    writeMarker?.Invoke(before.Describe());
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    Debug.LogWarning(logPrefix + "could not write the target restore marker (" + e.GetType().Name + ")");
                }
            }

            return new ActiveTargetRestore(true, true, before.Describe(), after.Describe(), message, reload);
        }

        /// <summary>A profile's key for the plan, null for none (<see cref="ProfileKey(string, int)"/>).</summary>
        internal static string ProfileKey(BuildProfile profile) => profile == null ? null : ProfileKey(AssetDatabase.GetAssetPath(profile), profile.GetInstanceID());

        /// <summary>Its asset path (the same asset reloaded is the same profile), else <c>#</c> and its instance id. Pure.</summary>
        internal static string ProfileKey(string assetPath, int instanceId) => string.IsNullOrEmpty(assetPath) ? "#" + instanceId : assetPath;

        private static void WriteMarker(string projectRoot, string expected)
        {
            string path = Path.Combine(projectRoot, MarkerRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, expected + "\n", new UTF8Encoding(false));
        }
    }

    /// <summary>
    /// After the domain reload that follows a switch back, reports whether the Editor is where the
    /// build found it, and removes the marker. The Ship section reads <see cref="LastMessage"/>.
    /// </summary>
    [InitializeOnLoad]
    public static class ActiveTargetRestoreCheck
    {
        static ActiveTargetRestoreCheck()
        {
            EditorApplication.delayCall += Check;
        }

        /// <summary>What the last check found, or null when there was nothing to check.</summary>
        public static string LastMessage { get; private set; }

        /// <summary>True while a switch back has not been confirmed by a domain reload yet.</summary>
        public static bool Pending => File.Exists(Path.Combine(BuildGuardContext.ProjectRoot, ActiveTargetRestore.MarkerRelativePath));

        private static void Check()
        {
            string path = Path.Combine(BuildGuardContext.ProjectRoot, ActiveTargetRestore.MarkerRelativePath);
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                string expected = File.ReadAllText(path, Encoding.UTF8).Trim();
                string now = ActiveTarget.Capture().Describe();
                File.Delete(path);
                LastMessage = now == expected
                    ? "The Editor is back on " + now + " and its scripts were recompiled."
                    : "The Editor reads " + now + ", not " + expected + " as before the server build; switch it in File > Build Profiles.";
                if (now == expected)
                {
                    Debug.Log(ServerBuilder.EditorLogPrefix + LastMessage);
                }
                else
                {
                    Debug.LogWarning(ServerBuilder.EditorLogPrefix + LastMessage);
                }
            }
            catch (IOException e)
            {
                Debug.LogWarning(ServerBuilder.EditorLogPrefix + "could not read the target restore marker (" + e.GetType().Name + ")");
            }
        }
    }
}
