using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PingCore.Editor.BuildGuard;
using PingCore.Editor.Cli;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace BeaconRush.Editor
{
    /// <summary>
    /// Builds the Beacon Rush Windows x64 client (subtarget Player) from <c>Assets/Client/Scenes/Client.unity</c>. Headless:
    /// <code>Unity.exe -batchmode -nographics -quit -projectPath SampleGame -buildTarget Win64 -standaloneBuildSubtarget Player
    ///   -executeMethod BeaconRush.Editor.ClientBuild.Run -pingcoreVersion &lt;v&gt; [-pingcoreBackend IL2CPP|Mono]
    ///   -logFile &lt;editor log&gt;</code>
    /// The contract:
    /// <list type="bullet">
    /// <item><b>Output</b> <c>Builds/Client/&lt;v&gt;/BeaconRushClient.exe</c> with <c>version.txt</c> beside it. An existing
    /// folder of the same version is replaced. No LZ4 or other compression: the build guard byte-scans the output, and an
    /// archive would defeat that. Code that wraps this build (<see cref="Build"/> with
    /// <see cref="ClientBuildArgs.WithInstrumentation"/>) may add scripting defines and write under another <c>Builds/</c>
    /// folder, which the build guard judges by the project's rules.</item>
    /// <item><b>Backend</b> IL2CPP by default, or Mono. The Standalone scripting backend is set for the build and restored
    /// afterwards, even on failure, and <see cref="BuildHousekeeping"/> then writes <c>ProjectSettings/ProjectSettings.asset</c>
    /// back byte for byte and removes the performance test framework's <c>Assets/Resources</c> run files. IL2CPP needs the Unity Hub module "Windows Build Support (IL2CPP)"; without it the
    /// build stops before it starts, with exit 2 and that module named.</item>
    /// <item><b>Target</b> the editor must be launched on StandaloneWindows64 with the Player subtarget (the two flags above):
    /// a server build leaves the Dedicated Server subtarget active, whose <c>UNITY_SERVER</c> define compiles the client's
    /// scripts out of the editor, and a switch inside <c>-executeMethod</c> only takes effect after it returns
    /// (<see cref="ClientBuildPreflight"/>). On any other target the build stops before it starts, with exit 2 and the flags
    /// named. The client scene and the prefabs it uses are then scanned for missing scripts, and any stops the build with
    /// exit 2, naming the scene and the objects.</item>
    /// <item><b>Exit codes</b> 0 only when the build succeeded AND the guard's verdict file reads <c>pass</c> at the
    /// <c>postprocess</c> stage, with <c>outputScan</c> <c>complete</c> and <c>outputPath</c> this executable, and
    /// <see cref="BuildHousekeeping"/> put the project back; 1 when the build or the guard failed, the project was not put
    /// back, or an exception escaped; 2 for bad arguments, a missing scene, a missing player module, the
    /// wrong active build target or a missing script in the scene.</item>
    /// </list>
    /// The client it produces is launched with no <c>-logFile</c> (its event lines go to stdout).
    /// </summary>
    public static class ClientBuild
    {
        public const int ExitPass = 0;
        public const int ExitBuildFailed = 1;
        public const int ExitUsage = 2;

        /// <summary>The Unity Hub module the IL2CPP build needs, as Unity Hub names it.</summary>
        public const string Il2CppModuleName = "Windows Build Support (IL2CPP)";

        private const string LogPrefix = "[ClientBuild] ";

        /// <summary>The <c>-executeMethod</c> entry point; exits the editor with the build's code.</summary>
        public static void Run()
        {
            int exitCode = ExitBuildFailed;
            try
            {
                ClientBuildArgs args = ClientBuildArgs.Parse(Environment.GetCommandLineArgs());
                if (!args.IsValid)
                {
                    foreach (string error in args.Errors)
                    {
                        Debug.LogError(LogPrefix + error);
                    }

                    exitCode = ExitUsage;
                }
                else
                {
                    exitCode = Build(args);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                exitCode = ExitBuildFailed;
            }

            Debug.Log(LogPrefix + "exit " + exitCode);
            EditorApplication.Exit(exitCode);
        }

        /// <summary>The path of the Windows x64 IL2CPP player variation in this editor; it exists only with the IL2CPP module.</summary>
        public static string Il2CppVariationPath => Path.Combine(EditorApplication.applicationContentsPath,
            "PlaybackEngines", "windowsstandalonesupport", "Variations", "win64_player_nondevelopment_il2cpp");

        /// <summary>Runs one build and returns its exit code; never exits the editor.</summary>
        public static int Build(ClientBuildArgs args)
        {
            if (args == null || !args.IsValid)
            {
                throw new ArgumentException("the arguments are not valid", nameof(args));
            }

            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
            {
                Debug.LogError(LogPrefix + "Windows Build Support is not installed for this editor");
                return ExitUsage;
            }

            string targetProblem = ClientBuildPreflight.TargetProblem(EditorUserBuildSettings.activeBuildTarget,
                EditorUserBuildSettings.standaloneBuildSubtarget, ClientBuildPreflight.EditorCompiledForServer);
            if (targetProblem != null)
            {
                Debug.LogError(LogPrefix + targetProblem);
                return ExitUsage;
            }

            if (args.Backend == ScriptingImplementation.IL2CPP && !Directory.Exists(Il2CppVariationPath))
            {
                Debug.LogError(LogPrefix + "IL2CPP MODULE MISSING: the Windows IL2CPP player is not installed (no " + Il2CppVariationPath + "). Install \""
                    + Il2CppModuleName + "\" for " + Application.unityVersion + " in Unity Hub (Installs, " + Application.unityVersion
                    + ", Add modules), then rerun; or pass " + ClientBuildArgs.BackendFlag + " Mono, which is not the IL2CPP proof");
                return ExitUsage;
            }

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ClientBuildArgs.ScenePath) == null)
            {
                Debug.LogError(LogPrefix + "scene not found: " + ClientBuildArgs.ScenePath + " (generate it with BeaconRush.Editor.ClientSceneBuilder.Run)");
                return ExitUsage;
            }

            IReadOnlyList<MissingScriptFinding> missingScripts = MissingScriptScan.ScanScenes(new[] { ClientBuildArgs.ScenePath });
            string missingProblem = ClientBuildPreflight.MissingScriptProblem(ClientBuildArgs.ScenePath, missingScripts);
            if (missingProblem != null)
            {
                Debug.LogError(LogPrefix + missingProblem);
                return ExitUsage;
            }

            Debug.Log(LogPrefix + "target " + EditorUserBuildSettings.activeBuildTarget + " (" + EditorUserBuildSettings.standaloneBuildSubtarget
                + "); 0 missing scripts in " + ClientBuildArgs.ScenePath + " and its prefabs");
            string projectRoot = BuildGuardContext.ProjectRoot;
            string outputFolder = Path.Combine(projectRoot, args.OutputFolder);
            if (Directory.Exists(outputFolder))
            {
                Directory.Delete(outputFolder, true);
            }

            NamedBuildTarget standalone = NamedBuildTarget.Standalone;
            ScriptingImplementation previousBackend = PlayerSettings.GetScriptingBackend(standalone);
            // Before the backend changes: setting it back alone still leaves ProjectSettings.asset re-serialised with
            // this editor's defaults, and a failed build leaves the performance test framework's run files behind.
            BuildHousekeeping housekeeping = BuildHousekeeping.Begin();
            BuildReport report;
            bool restored = false;
            try
            {
                try
                {
                    if (previousBackend != args.Backend)
                    {
                        PlayerSettings.SetScriptingBackend(standalone, args.Backend);
                    }

                    Debug.Log(LogPrefix + "building " + args.ExecutablePath + " (" + args.BackendName + (args.ExtraDefines.Length > 0 ? ", extra defines " + string.Join(", ", args.ExtraDefines) : string.Empty) + ")");
                    report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                    {
                        scenes = new[] { ClientBuildArgs.ScenePath },
                        locationPathName = args.ExecutablePath,
                        target = BuildTarget.StandaloneWindows64,
                        targetGroup = BuildTargetGroup.Standalone,
                        // Explicit, though the editor is already on it (checked above): the player's own compile follows this.
                        subtarget = (int)StandaloneBuildSubtarget.Player,
                        // No CompressWithLz4: the guard must be able to byte-scan the whole output.
                        options = BuildOptions.None,
                        extraScriptingDefines = args.ExtraDefines,
                    });
                }
                finally
                {
                    if (PlayerSettings.GetScriptingBackend(standalone) != previousBackend)
                    {
                        PlayerSettings.SetScriptingBackend(standalone, previousBackend);
                    }
                }
            }
            finally
            {
                // Its own finally, so a throw while restoring the backend cannot skip it.
                restored = housekeeping.Finish(LogPrefix);
            }

            BuildResult result = report.summary.result;
            BuildGuardVerdict verdict = BuildGuardVerdictFile.Read(projectRoot);
            string problem = Judge(projectRoot, args, result, verdict) ?? (restored ? null : BuildHousekeeping.NotRestoredProblem);
            string verdictText = verdict == null
                ? "none"
                : verdict.verdict + " at " + verdict.stage + " [" + string.Join(", ", verdict.reasons ?? Array.Empty<string>()) + "], output scan " + verdict.outputScan;
            if (problem != null)
            {
                Debug.LogError(LogPrefix + "FAIL " + args.ExecutablePath + ": " + problem + "; build " + result + ", guard verdict " + verdictText);
                return ExitBuildFailed;
            }

            File.WriteAllText(Path.Combine(projectRoot, args.VersionFilePath), args.Version + "\n", new UTF8Encoding(false));
            Debug.Log(LogPrefix + "PASS " + args.ExecutablePath + " version " + args.Version + " (" + args.BackendName + "); build " + result + ", guard verdict " + verdictText
                + ", " + report.summary.totalSize + " bytes in " + report.summary.totalTime.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s");
            return ExitPass;
        }

        /// <summary>Why the finished build does not count as a pass, or null when it does.</summary>
        private static string Judge(string projectRoot, ClientBuildArgs args, BuildResult result, BuildGuardVerdict verdict)
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

            if (verdict.outputScan != BuildGuardCoverage.OutputScanComplete)
            {
                return "the build guard's output scan is '" + verdict.outputScan + "', not complete";
            }

            string expected = BuildGuardPolicy.NormalizeFullPath(Path.Combine(projectRoot, args.ExecutablePath));
            string written = string.IsNullOrEmpty(verdict.outputPath) ? string.Empty : BuildGuardPolicy.NormalizeFullPath(Path.Combine(projectRoot, verdict.outputPath));
            if (!string.Equals(expected, written, StringComparison.OrdinalIgnoreCase))
            {
                return "the verdict file belongs to another build (" + verdict.outputPath + ")";
            }

            if (!File.Exists(Path.Combine(projectRoot, args.ExecutablePath)))
            {
                return "the executable is missing";
            }

            return null;
        }
    }

    /// <summary>
    /// The command line of <see cref="ClientBuild.Run"/>, parsed purely (EditMode tests in
    /// <c>Assets/Client/Tests/Editor/ClientBuildArgsTests.cs</c>):
    /// <c>-pingcoreVersion &lt;v&gt;</c> (required; 1 to 64 letters, digits, <c>.</c>, <c>_</c> or <c>-</c>, starting
    /// with a letter or digit, the <c>Cli.BuildServer</c> rule) and <c>-pingcoreBackend IL2CPP|Mono</c> (default IL2CPP,
    /// case-insensitive). Flags match case-insensitively; any other <c>-pingcore*</c> flag, a repeated flag or a missing
    /// value is an error. A wrapper adds extra defines and another output root with <see cref="WithInstrumentation"/>.
    /// </summary>
    public sealed class ClientBuildArgs
    {
        public const string VersionFlag = "-pingcoreVersion";
        public const string BackendFlag = "-pingcoreBackend";
        public const string Product = "BeaconRushClient";
        public const string ScenePath = "Assets/Client/Scenes/Client.unity";
        public const string ClientOutputRoot = "Builds/Client";
        public const string VersionFileName = "version.txt";

        private const string FlagPrefix = "-pingcore";
        private static readonly Regex VersionPattern = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
        private static readonly Regex DefinePattern = new Regex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);
        private static readonly Regex FolderSegmentPattern = new Regex("^[A-Za-z0-9_][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

        private readonly IReadOnlyList<string> extraDefinesList;

        private ClientBuildArgs(string version, ScriptingImplementation backend, IReadOnlyList<string> extraDefines, string outputRoot, IReadOnlyList<string> errors)
        {
            Version = version;
            Backend = backend;
            extraDefinesList = extraDefines ?? Array.Empty<string>();
            OutputRoot = outputRoot ?? ClientOutputRoot;
            Errors = errors;
        }

        /// <summary>The build version.</summary>
        public string Version { get; }

        /// <summary>The Standalone scripting backend for this build.</summary>
        public ScriptingImplementation Backend { get; }

        /// <summary>The folder the version folder goes in, relative to the project: <see cref="ClientOutputRoot"/> unless a wrapper set another.</summary>
        public string OutputRoot { get; }

        /// <summary>Every problem found; empty when the arguments are good.</summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>True when there is no problem.</summary>
        public bool IsValid => Errors.Count == 0;

        /// <summary><c>IL2CPP</c> or <c>Mono</c>.</summary>
        public string BackendName => Backend == ScriptingImplementation.IL2CPP ? "IL2CPP" : "Mono";

        /// <summary><c>&lt;output root&gt;/&lt;v&gt;</c>, by default <c>Builds/Client/&lt;v&gt;</c>, relative to the project.</summary>
        public string OutputFolder => OutputRoot + "/" + Version;

        /// <summary>The executable, relative to the project.</summary>
        public string ExecutablePath => OutputFolder + "/" + Product + ".exe";

        /// <summary><c>version.txt</c> beside the executable, relative to the project.</summary>
        public string VersionFilePath => OutputFolder + "/" + VersionFileName;

        /// <summary>The extra scripting defines (<c>BuildPlayerOptions.extraScriptingDefines</c>); none by default.</summary>
        public string[] ExtraDefines => new List<string>(extraDefinesList).ToArray();

        /// <summary>
        /// The same build with <paramref name="extraDefines"/> and written under <paramref name="outputRoot"/> (a folder
        /// under <c>Builds/</c>), for code that wraps this build. A define that is not a symbol or a folder outside
        /// <c>Builds/</c> is an error of the returned arguments. Pure.
        /// </summary>
        public ClientBuildArgs WithInstrumentation(IReadOnlyList<string> extraDefines, string outputRoot)
        {
            var errors = new List<string>(Errors);
            var defines = new List<string>();
            foreach (string define in extraDefines ?? Array.Empty<string>())
            {
                if (define == null || !DefinePattern.IsMatch(define))
                {
                    errors.Add("an extra define must be a scripting define: a letter or '_', then letters, digits or '_'");
                }
                else if (!defines.Contains(define))
                {
                    defines.Add(define);
                }
            }

            string[] segments = (outputRoot ?? string.Empty).Split('/');
            bool rootOk = outputRoot != null && segments.Length >= 2 && segments[0] == "Builds";
            foreach (string segment in segments)
            {
                rootOk &= segment != "." && segment != ".." && FolderSegmentPattern.IsMatch(segment);
            }

            if (!rootOk)
            {
                errors.Add("the output root " + outputRoot + " must be a folder under Builds/ with forward slashes and no . or .. part");
            }

            return new ClientBuildArgs(Version, Backend, defines, rootOk ? outputRoot : OutputRoot, errors);
        }

        /// <summary>Parses the editor's command line. Never throws.</summary>
        public static ClientBuildArgs Parse(IReadOnlyList<string> args)
        {
            var errors = new List<string>();
            string version = null;
            string backend = null;
            for (int i = 0; args != null && i < args.Count; i++)
            {
                string arg = args[i] ?? string.Empty;
                if (!arg.StartsWith(FlagPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool isVersion = Is(arg, VersionFlag);
                bool isBackend = Is(arg, BackendFlag);
                if (!isVersion && !isBackend)
                {
                    errors.Add("unknown flag " + arg + "; ClientBuild takes " + VersionFlag + " and " + BackendFlag);
                    continue;
                }

                if (i + 1 >= args.Count || string.IsNullOrEmpty(args[i + 1]) || args[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    errors.Add(arg + " needs a value");
                    continue;
                }

                string value = args[++i];
                if (isVersion)
                {
                    if (version != null)
                    {
                        errors.Add(VersionFlag + " is given twice");
                    }

                    version = value;
                }
                else
                {
                    if (backend != null)
                    {
                        errors.Add(BackendFlag + " is given twice");
                    }

                    backend = value;
                }
            }

            if (version == null)
            {
                errors.Add(VersionFlag + " is required");
            }
            else if (!VersionPattern.IsMatch(version))
            {
                errors.Add(VersionFlag + " must be 1 to 64 letters, digits, '.', '_' or '-', starting with a letter or digit");
            }

            ScriptingImplementation implementation = ScriptingImplementation.IL2CPP;
            if (backend != null)
            {
                if (string.Equals(backend, "IL2CPP", StringComparison.OrdinalIgnoreCase))
                {
                    implementation = ScriptingImplementation.IL2CPP;
                }
                else if (string.Equals(backend, "Mono", StringComparison.OrdinalIgnoreCase))
                {
                    implementation = ScriptingImplementation.Mono2x;
                }
                else
                {
                    errors.Add(BackendFlag + " must be IL2CPP or Mono");
                }
            }

            return new ClientBuildArgs(version, implementation, null, null, errors);
        }

        private static bool Is(string arg, string flag) => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase);
    }
}
