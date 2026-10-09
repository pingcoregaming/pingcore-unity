using System;
using System.Globalization;
using PingCore.Editor.Build;
using UnityEditor;
using UnityEngine;

namespace PingCore.Editor.Cli
{
    /// <summary>
    /// Builds a Linux x86_64 Dedicated Server player, headless, for CI and scripts.
    /// The build profile form:
    /// <code>Unity -batchmode -nographics -quit -projectPath &lt;project&gt; -buildTarget Linux64 -standaloneBuildSubtarget Server
    ///   -executeMethod PingCore.Editor.Cli.BuildServer.Run -buildProfile Assets/Settings/Build Profiles/Server.asset
    ///   [-pingcoreVersion 2026.10.08-abc1234] [-pingcoreProduct MyGame] -logFile &lt;log&gt;</code>
    /// The scene form (scripts that build from a scene list):
    /// <code>... -executeMethod PingCore.Editor.Cli.BuildServer.Run -pingcoreVersion 2026.10.02-abc1234
    ///   [-pingcoreScene Assets/Game/Scenes/Server.unity] [-pingcoreProduct BeaconRushServer]
    ///   [-pingcoreDefine SYMBOL ...] [-pingcoreOutputRoot Builds/Instrumented/Server]</code>
    /// The arguments are <see cref="BuildServerArgs"/>; without <c>-pingcoreVersion</c> the build profile form takes
    /// <c>yyyy.MM.dd-HHmmss</c> (UTC). The output is <c>Builds/Server/&lt;version&gt;/&lt;product&gt;.x86_64</c> (with
    /// <c>-pingcoreOutputRoot</c>: <c>&lt;root&gt;/&lt;version&gt;/</c>) with <c>version.txt</c>; an existing folder of
    /// the same version is replaced. Exit codes: 0 when the build succeeded AND the build guard's verdict
    /// (<c>Library/pingcore-build-guard.json</c>) is <c>pass</c> at the postprocess stage for this output and the
    /// housekeeping put the project back; 1 when the build or the guard failed, or the project was not put back
    /// (<see cref="BuildHousekeeping.NotRestoredProblem"/>, or the active build profile and target could not be put back,
    /// <see cref="ActiveTargetRestore"/>); 2 when the arguments or the project are unusable: a missing or
    /// non-server build profile, a missing scene, a missing Linux Dedicated Server module, or a component with a missing
    /// script in a build scene or a prefab it uses (<see cref="MissingScriptScan"/>: launch the editor with
    /// <see cref="ServerTargetFlags"/>). The build itself is <see cref="ServerBuilder"/>, shared with the Ship section; this
    /// class parses the command line, runs it with <see cref="ServerBuildOptions.FromArgs"/> (no Play-mode check; the active build
    /// profile and target are put back like an in-Editor build's, as settings only) and exits.
    /// </summary>
    public static class BuildServer
    {
        public const int ExitPass = ServerBuilder.ExitPass;
        public const int ExitBuildFailed = ServerBuilder.ExitBuildFailed;
        public const int ExitUsage = ServerBuilder.ExitUsage;

        /// <summary>The editor command-line flags that launch it on the server's target.</summary>
        public const string ServerTargetFlags = "-buildTarget Linux64 -standaloneBuildSubtarget Server";

        private const string LogPrefix = "[PingCore BuildServer] ";

        /// <summary>The <c>-executeMethod</c> entry point. Always exits the editor with one of the exit codes.</summary>
        public static void Run()
        {
            int exitCode = ExitBuildFailed;
            try
            {
                BuildServerArgs args = BuildServerArgs.Parse(Environment.GetCommandLineArgs(), DefaultVersion(DateTime.UtcNow));
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

        /// <summary>The build profile form's version when none is given: <c>yyyy.MM.dd-HHmmss</c>. Pure.</summary>
        public static string DefaultVersion(DateTime utcNow) => utcNow.ToString("yyyy.MM.dd-HHmmss", CultureInfo.InvariantCulture);

        /// <summary>Runs one build for <paramref name="args"/> and returns its exit code; never exits the editor.</summary>
        public static int Build(BuildServerArgs args)
        {
            if (args == null || !args.IsValid)
            {
                throw new ArgumentException("the arguments are not valid", nameof(args));
            }

            ServerBuildOptions options = ServerBuildOptions.FromArgs(args, PlayerSettings.productName);
            if (!options.IsValid)
            {
                foreach (string error in options.Errors)
                {
                    Debug.LogError(LogPrefix + error);
                }

                return ExitUsage;
            }

            // One code path with the Editor's Ship section: ServerBuilder holds the build, this class the command line.
            return ServerBuilder.Build(options, LogPrefix).ExitCode;
        }
    }
}
