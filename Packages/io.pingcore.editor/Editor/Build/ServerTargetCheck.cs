using System;
using PingCore.Editor.Cli;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEngine;

namespace PingCore.Editor.Build
{
    /// <summary>The Editor's active target as far as Play mode cares: whether it compiles with <c>UNITY_SERVER</c>, and why.</summary>
    public sealed class ServerTargetState
    {
        public ServerTargetState(bool isServer, string profileName, string platform)
        {
            IsServer = isServer;
            ProfileName = profileName;
            Platform = platform;
        }

        /// <summary>The Editor is on a Dedicated Server profile or subtarget, so every <c>!UNITY_SERVER</c> script is compiled out of it.</summary>
        public bool IsServer { get; }

        /// <summary>The active Dedicated Server build profile's name, or null when the platform's own settings make it a server.</summary>
        public string ProfileName { get; }

        /// <summary><c>Linux</c>, <c>Windows</c>, <c>macOS</c> or the target's own name.</summary>
        public string Platform { get; }
    }

    /// <summary>
    /// Whether the Editor sits on a Dedicated Server build profile or subtarget, which compiles every script meant only for
    /// players (<c>!UNITY_SERVER</c>) out of the Editor: a client scene then plays with missing scripts and no UI. Window &gt;
    /// PingCore shows <see cref="WindowMessage"/> at the top, and <see cref="ServerTargetPlayModeGuard"/> logs
    /// <see cref="PlayModeMessage"/> when Play starts on it. Neither ever switches the target: that is the developer's choice
    /// (someone working on the game server may play its scene on purpose). The decisions are pure; <see cref="Current"/> reads the Editor.
    /// </summary>
    public static class ServerTargetCheck
    {
        /// <summary>The fix, the same in both messages.</summary>
        public const string Fix = "Switch to a player profile in Build Profiles.";

        /// <summary>
        /// The state for an Editor on <paramref name="group"/> / <paramref name="target"/> / <paramref name="subtarget"/> with
        /// the active build profile <paramref name="profileName"/> (null for none) whose serialized subtarget is
        /// <paramref name="profileSubtarget"/> (null when unknown). Pure.
        /// </summary>
        public static ServerTargetState Evaluate(BuildTargetGroup group, BuildTarget target, StandaloneBuildSubtarget subtarget, string profileName, int? profileSubtarget)
        {
            string profile = string.IsNullOrWhiteSpace(profileName) ? null : profileName.Trim();
            if (profile != null && profileSubtarget.HasValue)
            {
                // An active profile whose subtarget is known decides alone: its settings are what the Editor compiles with.
                bool profileIsServer = profileSubtarget.Value == (int)StandaloneBuildSubtarget.Server;
                return new ServerTargetState(profileIsServer, profileIsServer ? profile : null, PlatformName(target));
            }

            bool subtargetIsServer = group == BuildTargetGroup.Standalone && subtarget == StandaloneBuildSubtarget.Server;
            return new ServerTargetState(subtargetIsServer, null, PlatformName(target));
        }

        /// <summary>The window's line, or null when the Editor is not on a server target. Pure.</summary>
        public static string WindowMessage(ServerTargetState state)
        {
            if (state == null || !state.IsServer)
            {
                return null;
            }

            return state.ProfileName != null
                ? $"Your active build profile is {state.ProfileName}, a dedicated server profile, so client scenes cannot run in Play mode. {Fix}"
                : $"Your active build target is {state.Platform} Dedicated Server, so client scenes cannot run in Play mode. {Fix}";
        }

        /// <summary>
        /// The one error Play mode logs, or null: only on a server target AND when the open scenes hold components whose
        /// scripts are missing (<paramref name="missingComponents"/>), so playing a server scene on purpose logs nothing. Pure.
        /// </summary>
        public static string PlayModeMessage(ServerTargetState state, int missingComponents)
        {
            if (state == null || !state.IsServer || missingComponents <= 0)
            {
                return null;
            }

            string cause = state.ProfileName != null
                ? $"the active build profile is {state.ProfileName}, a dedicated server profile"
                : $"the active build target is {state.Platform} Dedicated Server";
            string components = missingComponents == 1 ? "1 component" : missingComponents + " components";
            return $"[PingCore] Play mode started with {components} in the open scenes missing their scripts, because {cause}: "
                + "the Dedicated Server subtarget compiles every script meant only for players (UNITY_SERVER) out of the Editor, so a client scene plays with no UI. "
                + "Fix: File > Build Profiles, pick the Windows or macOS platform (or a player build profile), press Switch Platform (Switch Profile for a build profile), then press Play again.";
        }

        /// <summary>The Editor's state now. Never throws; an unreadable profile counts as none.</summary>
        public static ServerTargetState Current()
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            BuildProfile profile = ActiveTarget.ActiveProfileOrNull();
            int? profileSubtarget = null;
            string profileName = null;
            if (profile != null)
            {
                try
                {
                    profileName = profile.name;
                    profileSubtarget = BuildProfiles.Describe(profile, AssetDatabase.GetAssetPath(profile))?.Subtarget;
                }
                catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is NullReferenceException)
                {
                    profileName = null;
                }
            }

            return Evaluate(BuildPipeline.GetBuildTargetGroup(target), target, EditorUserBuildSettings.standaloneBuildSubtarget, profileName, profileSubtarget);
        }

        /// <summary>Opens File &gt; Build Profiles; never switches anything.</summary>
        public static void OpenBuildProfiles()
        {
            if (!EditorApplication.ExecuteMenuItem("File/Build Profiles"))
            {
                BuildPlayerWindow.ShowBuildPlayerWindow();
            }
        }

        private static string PlatformName(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneLinux64:
                    return "Linux";
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    return "Windows";
                case BuildTarget.StandaloneOSX:
                    return "macOS";
                default:
                    return target.ToString();
            }
        }
    }

    /// <summary>
    /// Editor-only (this assembly never ships): when Play starts on a Dedicated Server profile or subtarget and the open scenes
    /// hold components with missing scripts, logs ONE error naming the cause and the fix (<see cref="ServerTargetCheck.PlayModeMessage"/>),
    /// before Unity's own "The referenced script (Unknown) on this Behaviour is missing!" lines. It never switches the target.
    /// </summary>
    [InitializeOnLoad]
    public static class ServerTargetPlayModeGuard
    {
        static ServerTargetPlayModeGuard()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingEditMode)
            {
                return;
            }

            try
            {
                ServerTargetState state = ServerTargetCheck.Current();
                if (!state.IsServer)
                {
                    return;
                }

                string message = ServerTargetCheck.PlayModeMessage(state, MissingScriptScan.CountInOpenScenes());
                if (message != null)
                {
                    Debug.LogError(message);
                }
            }
            catch (Exception e)
            {
                // A check that fails must never stop Play mode.
                Debug.LogWarning("[PingCore] the Play mode build target check failed (" + e.GetType().Name + ")");
            }
        }
    }
}
