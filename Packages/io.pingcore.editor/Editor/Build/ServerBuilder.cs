using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PingCore.Editor.BuildGuard;
using PingCore.Editor.Cli;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PingCore.Editor.Build
{
    /// <summary>
    /// Builds a Linux x86_64 Dedicated Server player: the body of <c>Cli.BuildServer</c>, called in-process by
    /// the Editor plugin's Ship section. Two forms:
    /// <list type="bullet">
    /// <item><b>A build profile</b> (the Editor and <c>-buildProfile</c>): <c>BuildPipeline.BuildPlayer(BuildPlayerWithProfileOptions)</c>
    /// with a Linux Dedicated Server Build Profile of the project, so the scenes, the scripting backend and the
    /// defines are the developer's own; nothing here chooses them.</item>
    /// <item><b>The scene form</b> (scripts that build from a scene list): the scenes on the command line, the Mono backend
    /// set and restored, and the command line's extra defines (<c>-pingcoreDefine</c>).</item>
    /// </list>
    /// Both write <c>Builds/Server/&lt;version&gt;/</c> (the scene form: <c>-pingcoreOutputRoot</c>'s folder when given) with <c>version.txt</c>,
    /// run <see cref="MissingScriptScan"/> on the build's scenes first, <see cref="BuildHousekeeping"/> after, and pass
    /// only when the build guard's verdict reads <c>pass</c> at postprocess for this executable. The exit codes are the
    /// ones <c>Cli.BuildServer</c>'s summary lists, which pingcore.io/docs/unity/the-pingcore-window publishes.
    /// <para>
    /// The active build target (Unity 6000.4.10f1): a Linux64 / Server build from an Editor on another
    /// target leaves the Editor on Linux64 / Server, and the scripts recompile with <c>UNITY_SERVER</c> at the next
    /// domain reload; and <c>BuildPlayer(BuildPlayerWithProfileOptions)</c> makes the profile it built the active build profile,
    /// which Unity keeps across restarts. Every build therefore puts the Editor back as it found it
    /// (<see cref="ActiveTargetRestore"/>).
    /// </para>
    /// </summary>
    public static class ServerBuilder
    {
        public const int ExitPass = 0;
        public const int ExitBuildFailed = 1;
        public const int ExitUsage = 2;

        /// <summary>The log prefix of an in-Editor build.</summary>
        public const string EditorLogPrefix = "[PingCore ServerBuilder] ";

        /// <summary>The player options of a scene form build, pure: the same for the CLI and the tests.</summary>
        public static BuildPlayerOptions PlayerOptions(ServerBuildOptions options)
        {
            if (options == null || !options.IsValid || options.Args.UsesBuildProfile)
            {
                throw new ArgumentException("the options are not a valid scene form build", nameof(options));
            }

            return new BuildPlayerOptions
            {
                scenes = new List<string>(options.Args.Scenes).ToArray(),
                locationPathName = options.ExecutablePath,
                target = BuildTarget.StandaloneLinux64,
                targetGroup = BuildTargetGroup.Standalone,
                subtarget = (int)StandaloneBuildSubtarget.Server,
                options = BuildOptions.None,
                extraScriptingDefines = options.Args.ExtraDefines,
            };
        }

        /// <summary>Why the Editor cannot build right now (Play mode, a compile in progress), or null. In-process builds only.</summary>
        public static string EditorStateProblem(bool playing, bool compiling)
        {
            if (playing)
            {
                return "The Editor is in Play mode; leave Play mode, then build.";
            }

            return compiling ? "Scripts are compiling; build again when the compile has finished." : null;
        }

        /// <summary>Runs one build; never exits the Editor. <paramref name="logPrefix"/> starts every log line.</summary>
        public static ServerBuildResult Build(ServerBuildOptions options, string logPrefix = EditorLogPrefix)
        {
            if (options == null || !options.IsValid)
            {
                throw new ArgumentException("the options are not valid", nameof(options));
            }

            logPrefix = logPrefix ?? string.Empty;
            if (options.InProcess)
            {
                string state = EditorStateProblem(EditorApplication.isPlayingOrWillChangePlaymode, EditorApplication.isCompiling);
                if (state != null)
                {
                    return Usage(options, logPrefix, state);
                }
            }

            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64))
            {
                return Usage(options, logPrefix, "Linux Build Support (with Dedicated Server) is not installed for this editor");
            }

            BuildProfile profile = null;
            IReadOnlyList<string> scenes = options.Args.Scenes;
            if (options.Args.UsesBuildProfile)
            {
                profile = BuildProfiles.LoadServerProfile(options.Args.BuildProfile, out string profileProblem);
                if (profile == null)
                {
                    return Usage(options, logPrefix, profileProblem);
                }

                scenes = (profile.GetScenesForBuild() ?? Array.Empty<EditorBuildSettingsScene>())
                    .Where(s => s != null && s.enabled && !string.IsNullOrEmpty(s.path))
                    .Select(s => s.path)
                    .ToList();
                if (scenes.Count == 0)
                {
                    return Usage(options, logPrefix, "build profile " + options.Args.BuildProfile + " has no enabled scene");
                }
            }

            foreach (string scene in scenes)
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scene) == null)
                {
                    return Usage(options, logPrefix, "scene not found: " + scene);
                }
            }

            // The editor serialises the scenes from its own compiled scripts: one left on the Player subtarget has none of a
            // UNITY_SERVER-only assembly, and the server would ship those components as missing scripts.
            string missing = MissingScriptScan.Describe(MissingScriptScan.ScanScenes(scenes));
            if (missing != null)
            {
                string hint = options.InProcess
                    ? "A build from the Editor serialises its scenes with the Editor's own scripts, so a component whose script is gone, or compiled only for the Dedicated Server subtarget, cannot be in a build scene; add such components at runtime"
                    : "Check the editor was launched with " + BuildServer.ServerTargetFlags + " and that every script the scenes use compiles";
                Debug.LogError(logPrefix + "MISSING SCRIPTS (" + missing + "): the server would ship these components as missing scripts. " + hint);
                return new ServerBuildResult(ExitUsage, "missing scripts: " + missing, options, null, null);
            }

            string projectRoot = BuildGuardContext.ProjectRoot;
            string outputFolder = Path.Combine(projectRoot, options.OutputFolder);
            if (Directory.Exists(outputFolder))
            {
                Directory.Delete(outputFolder, true);
            }

            // Both forms: BuildPlayer(BuildPlayerWithProfileOptions) makes the profile it built the active build profile, which
            // Unity keeps across restarts, and an in-Editor build also moves the target (ActiveTargetRestore).
            ActiveTarget targetBefore = ActiveTarget.Capture();
            ScriptingImplementation previousBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Server);
            bool forceMono = profile == null && previousBackend != ScriptingImplementation.Mono2x;

            // Before the backend changes: setting it back alone still leaves ProjectSettings.asset re-serialised with
            // this editor's defaults, and a failed build leaves the performance test framework's run files behind.
            BuildHousekeeping housekeeping = BuildHousekeeping.Begin();
            BuildReport report;
            bool restored = false;
            ActiveTargetRestore restore = null;
            try
            {
                try
                {
                    if (forceMono)
                    {
                        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Server, ScriptingImplementation.Mono2x);
                    }

                    Debug.Log(logPrefix + "building " + options.ExecutablePath
                        + (profile != null ? " with build profile " + options.Args.BuildProfile : options.Args.Defines.Count > 0 ? " (extra defines " + string.Join(", ", options.Args.Defines) + ")" : string.Empty)
                        + " from " + string.Join(", ", scenes));
                    report = profile != null
                        ? BuildPipeline.BuildPlayer(new BuildPlayerWithProfileOptions { buildProfile = profile, locationPathName = options.ExecutablePath, options = BuildOptions.None })
                        : BuildPipeline.BuildPlayer(PlayerOptions(options));
                }
                finally
                {
                    if (forceMono)
                    {
                        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Server, previousBackend);
                    }
                }
            }
            finally
            {
                // Its own finally, so a throw while restoring the backend cannot skip either. First the active profile and
                // target, for a failed, cancelled or throwing build too (never throwing: it only changes the Editor's own build
                // settings, and its recompile and domain reload come after this method returns, on the command line at the
                // next launch); then the housekeeping, so its save and its check of ProjectSettings come after anything the
                // switch back could have touched.
                restore = ActiveTargetRestore.Run(targetBefore, logPrefix, options.InProcess);
                restored = housekeeping.Finish(logPrefix);
            }

            BuildResult result = report.summary.result;
            BuildGuardVerdict verdict = BuildGuardVerdictFile.Read(projectRoot);
            string problem = Judge(projectRoot, options, result, verdict) ?? (restored ? null : BuildHousekeeping.NotRestoredProblem)
                ?? (restore != null && restore.Changed && !restore.Switched ? restore.Message : null);
            string verdictText = verdict == null
                ? "none"
                : verdict.verdict + " at " + verdict.stage + " [" + string.Join(", ", verdict.reasons ?? Array.Empty<string>()) + "]"
                  + (string.IsNullOrEmpty(verdict.outputScan) ? string.Empty : ", output scan " + verdict.outputScan);
            if (problem != null)
            {
                Debug.LogError(logPrefix + "FAIL " + options.ExecutablePath + ": " + problem + "; build " + result + ", guard verdict " + verdictText);
                return new ServerBuildResult(ExitBuildFailed, problem, options, verdictText, restore);
            }

            File.WriteAllText(Path.Combine(projectRoot, options.VersionFilePath), options.Args.Version + "\n", new UTF8Encoding(false));
            Debug.Log(logPrefix + "PASS " + options.ExecutablePath + " version " + options.Args.Version + "; build " + result + ", guard verdict " + verdictText
                + ", " + report.summary.totalSize + " bytes in " + report.summary.totalTime.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s");
            return new ServerBuildResult(ExitPass, null, options, verdictText, restore);
        }

        private static ServerBuildResult Usage(ServerBuildOptions options, string logPrefix, string problem)
        {
            Debug.LogError(logPrefix + problem);
            return new ServerBuildResult(ExitUsage, problem, options, null, null);
        }

        /// <summary>Why the finished build does not count as a pass, or null when it does.</summary>
        private static string Judge(string projectRoot, ServerBuildOptions options, BuildResult result, BuildGuardVerdict verdict)
        {
            if (result != BuildResult.Succeeded)
            {
                return "the build did not succeed";
            }

            if (verdict == null)
            {
                return "the build guard wrote no verdict";
            }

            if (verdict.verdict != BuildGuardVerdict.Pass || verdict.stage != BuildGuardPostprocessor.Stage)
            {
                return "the build guard's verdict is not pass at the postprocess stage";
            }

            string expected = BuildGuardPolicy.NormalizeFullPath(Path.Combine(projectRoot, options.ExecutablePath));
            string written = string.IsNullOrEmpty(verdict.outputPath)
                ? string.Empty
                : BuildGuardPolicy.NormalizeFullPath(Path.Combine(projectRoot, verdict.outputPath));
            if (!string.Equals(expected, written, StringComparison.OrdinalIgnoreCase))
            {
                return "the verdict file belongs to another build (" + verdict.outputPath + ")";
            }

            if (!File.Exists(Path.Combine(projectRoot, options.ExecutablePath)))
            {
                return "the executable is missing";
            }

            return null;
        }
    }
}
