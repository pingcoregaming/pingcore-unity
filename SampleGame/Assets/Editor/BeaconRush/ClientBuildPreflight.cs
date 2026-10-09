using System.Collections.Generic;
using PingCore.Editor.Cli;
using UnityEditor;

namespace BeaconRush.Editor
{
    /// <summary>
    /// What <see cref="ClientBuild"/> checks before it builds, so a client never ships its scene with missing scripts.
    /// <para>
    /// The editor compiles its scripts for the active build target. A Dedicated Server build leaves the Server subtarget
    /// active, whose <c>UNITY_SERVER</c> define compiles out <c>BeaconRush.Client</c> (and any other <c>!UNITY_SERVER</c>
    /// assembly), so <c>Client.unity</c>'s components resolve to nothing and the player build serialises
    /// them as missing scripts while still reporting success. Setting <c>BuildPlayerOptions.subtarget</c> does not help, and
    /// neither does <c>EditorUserBuildSettings.SwitchActiveBuildTarget</c> inside <c>-executeMethod</c>: the editor's scripts
    /// recompile and its domain reloads only after the method returns, so the build would still run on the server-compiled
    /// scripts. The editor has to be launched on the right target instead, with <see cref="TargetFlags"/>.
    /// </para>
    /// </summary>
    public static class ClientBuildPreflight
    {
        /// <summary>The editor command-line flags that launch it on the client's target.</summary>
        public const string TargetFlags = "-buildTarget Win64 -standaloneBuildSubtarget Player";

        /// <summary>True when this editor's scripts were compiled with <c>UNITY_SERVER</c> (the Dedicated Server subtarget).</summary>
        public static bool EditorCompiledForServer
        {
            get
            {
#if UNITY_SERVER
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Why the editor cannot build the client as launched, or null when its active target is
        /// <c>StandaloneWindows64</c> with the Player subtarget and its scripts were compiled without <c>UNITY_SERVER</c>. Pure.
        /// </summary>
        public static string TargetProblem(BuildTarget activeTarget, StandaloneBuildSubtarget activeSubtarget, bool compiledForServer)
        {
            const string Consequence = ", so BeaconRush.Client (!UNITY_SERVER) is not compiled in this editor"
                + " and Client.unity's components would build as missing scripts";
            const string Remedy = ". Relaunch the editor with " + TargetFlags + " (a switch inside -executeMethod takes effect only after the method returns)";
            if (compiledForServer)
            {
                return "WRONG BUILD TARGET: the editor's scripts are compiled with UNITY_SERVER (the Dedicated Server subtarget)" + Consequence + Remedy;
            }

            if (activeTarget != BuildTarget.StandaloneWindows64 || activeSubtarget != StandaloneBuildSubtarget.Player)
            {
                string consequence = activeSubtarget == StandaloneBuildSubtarget.Server ? Consequence : ", not the client's target";
                return "WRONG BUILD TARGET: the editor's active build target is " + activeTarget + " with the " + activeSubtarget
                    + " subtarget, not StandaloneWindows64 with Player" + consequence + Remedy;
            }

            return null;
        }

        /// <summary>Why the scene cannot be built, naming it and every object with a missing script, or null when there are none. Pure.</summary>
        public static string MissingScriptProblem(string scenePath, IReadOnlyList<MissingScriptFinding> findings)
        {
            string described = MissingScriptScan.Describe(findings);
            if (described == null)
            {
                return null;
            }

            return "MISSING SCRIPTS in " + scenePath + " or a prefab it uses (" + described + "): the player would ship these components"
                + " as missing scripts. Check the editor was launched with " + TargetFlags + " and that every script the scene uses compiles";
        }
    }
}
