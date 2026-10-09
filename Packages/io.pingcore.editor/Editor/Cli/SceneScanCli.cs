using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace PingCore.Editor.Cli
{
    /// <summary>
    /// Headless missing-script check of named scenes, for CI and scripts (for example the Quickstart sample imported
    /// into a throwaway project):
    /// <code>Unity -batchmode -nographics -quit -projectPath &lt;project&gt; -buildTarget Win64 -standaloneBuildSubtarget Player
    ///   -executeMethod PingCore.Editor.Cli.SceneScanCli.Run -pingcoreScene &lt;Assets/.../Scene.unity&gt; [-pingcoreScene ...] -logFile &lt;log&gt;</code>
    /// Each scene and every prefab it depends on goes through <see cref="MissingScriptScan"/>. It logs one line starting
    /// with <see cref="LogPrefix"/>: <c>PASS</c> with the counts, or <c>FAIL</c> with <see cref="MissingScriptScan.Describe"/>.
    /// Exit codes: 0 no missing script; 1 a missing script, or an exception; 2 no <c>-pingcoreScene</c>, a scene that is not a
    /// <c>.unity</c> asset path, or a scene the project does not have.
    /// </summary>
    public static class SceneScanCli
    {
        public const int ExitPass = 0;
        public const int ExitMissingScripts = 1;
        public const int ExitUsage = 2;
        public const string SceneFlag = "-pingcoreScene";
        public const string LogPrefix = "[PingCore SceneScan] ";

        /// <summary>The <c>-executeMethod</c> entry point. Always exits the editor with one of the exit codes.</summary>
        public static void Run()
        {
            int exitCode = ExitMissingScripts;
            try
            {
                exitCode = Scan(ParseScenes(Environment.GetCommandLineArgs(), out List<string> errors), errors);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            Debug.Log(LogPrefix + "exit " + exitCode);
            EditorApplication.Exit(exitCode);
        }

        /// <summary>
        /// The scenes named by <see cref="SceneFlag"/> (repeatable; case-insensitive flag), in order. A flag with no value or a
        /// value that is not an <c>Assets/</c> or <c>Packages/</c> path ending in <c>.unity</c> adds an error. Pure.
        /// </summary>
        public static List<string> ParseScenes(IReadOnlyList<string> args, out List<string> errors)
        {
            var scenes = new List<string>();
            errors = new List<string>();
            for (int i = 0; args != null && i < args.Count; i++)
            {
                if (!string.Equals(args[i], SceneFlag, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = i + 1 < args.Count ? args[i + 1] : null;
                if (string.IsNullOrEmpty(value) || value.StartsWith("-", StringComparison.Ordinal))
                {
                    errors.Add(SceneFlag + " needs a scene path");
                    continue;
                }

                i++;
                string path = value.Replace('\\', '/');
                bool rooted = path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal);
                if (!rooted || !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || path.Contains("/../"))
                {
                    errors.Add(SceneFlag + " " + value + " is not an Assets/ or Packages/ path to a .unity file");
                    continue;
                }

                scenes.Add(path);
            }

            if (scenes.Count == 0 && errors.Count == 0)
            {
                errors.Add("no " + SceneFlag + " given");
            }

            return scenes;
        }

        private static int Scan(List<string> scenes, List<string> errors)
        {
            foreach (string scene in scenes)
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scene) == null)
                {
                    errors.Add("scene not found: " + scene);
                }
            }

            if (errors.Count > 0)
            {
                foreach (string error in errors)
                {
                    Debug.LogError(LogPrefix + "USAGE: " + error);
                }

                return ExitUsage;
            }

            IReadOnlyList<MissingScriptFinding> findings = MissingScriptScan.ScanScenes(scenes);
            string missing = MissingScriptScan.Describe(findings);
            if (missing != null)
            {
                Debug.LogError(LogPrefix + "FAIL: " + missing);
                return ExitMissingScripts;
            }

            int prefabs = 0;
            foreach (string dependency in AssetDatabase.GetDependencies(scenes.ToArray(), true))
            {
                if (dependency.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    prefabs++;
                }
            }

            Debug.Log(LogPrefix + "PASS: no missing script in " + scenes.Count + " scene(s) (" + string.Join(", ", scenes) + ") and the " + prefabs + " prefab(s) they use");
            return ExitPass;
        }
    }
}
