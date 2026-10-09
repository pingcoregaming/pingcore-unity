using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>Inputs the preprocessor and the postprocessor share: project paths, defines, scenes and the heartbeat tokens.</summary>
    public static class BuildGuardContext
    {
        /// <summary>The class name of the SDK's client settings asset, read by name so this package does not reference the SDK.</summary>
        public const string ClientSettingsTypeName = "PingCoreClientSettings";

        /// <summary>The one field that may hold a <c>dsc_</c> token: the open-registration app's heartbeat token.</summary>
        public const string HeartbeatTokenField = "openRegistrationHeartbeatToken";

        private static string capturedLocation;
        private static string[] capturedExtraDefines = Array.Empty<string>();
        private static string[] capturedScenes = Array.Empty<string>();
        private static BuildGuardSourceScan storedSourceScan;
        private static string storedSourceScanOutput;

        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        internal static void Capture(BuildPlayerOptions options)
        {
            Clear();
            capturedLocation = options.locationPathName;
            capturedExtraDefines = options.extraScriptingDefines ?? Array.Empty<string>();
            capturedScenes = options.scenes ?? Array.Empty<string>();
        }

        internal static void Clear()
        {
            capturedLocation = null;
            capturedExtraDefines = Array.Empty<string>();
            capturedScenes = Array.Empty<string>();
            storedSourceScan = null;
            storedSourceScanOutput = null;
        }

        /// <summary>Keeps the early source-side scan of the build writing <paramref name="outputPath"/> for the authoritative postprocessor.</summary>
        internal static void StoreSourceScan(string outputPath, BuildGuardSourceScan scan)
        {
            storedSourceScan = scan;
            storedSourceScanOutput = outputPath;
        }

        /// <summary>The stored source-side scan of the build writing <paramref name="outputPath"/>, or null; it is handed out once.</summary>
        internal static BuildGuardSourceScan TakeSourceScan(string outputPath)
        {
            BuildGuardSourceScan scan = string.Equals(storedSourceScanOutput, outputPath, StringComparison.Ordinal) ? storedSourceScan : null;
            storedSourceScan = null;
            storedSourceScanOutput = null;
            return scan;
        }

        /// <summary>Whether <see cref="BuildGuardOptionsCapture"/> saw the options of the build that wrote <paramref name="report"/>.</summary>
        public static bool HasCapturedOptionsFor(BuildReport report)
        {
            if (string.IsNullOrEmpty(capturedLocation) || string.IsNullOrEmpty(report.summary.outputPath))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    BuildGuardPolicy.NormalizeFullPath(Path.Combine(ProjectRoot, capturedLocation)),
                    BuildGuardPolicy.NormalizeFullPath(Path.Combine(ProjectRoot, report.summary.outputPath)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>The player defines for the report's target plus any captured <c>extraScriptingDefines</c>.</summary>
        public static IReadOnlyList<string> CollectDefines(BuildReport report)
        {
            var defines = new List<string>();
            NamedBuildTarget target = GetNamedBuildTarget(report);
            defines.AddRange(BuildGuardPolicy.SplitDefines(PlayerSettings.GetScriptingDefineSymbols(target)));
            if (HasCapturedOptionsFor(report))
            {
                foreach (string extra in capturedExtraDefines)
                {
                    defines.AddRange(BuildGuardPolicy.SplitDefines(extra));
                }
            }

            return defines;
        }

        /// <summary>The scenes of the build: the captured option scenes, else the enabled scenes in the Build Settings.</summary>
        public static IReadOnlyList<string> CollectScenes(BuildReport report)
        {
            if (HasCapturedOptionsFor(report) && capturedScenes.Length > 0)
            {
                return capturedScenes;
            }

            var scenes = new List<string>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled && !string.IsNullOrEmpty(scene.path))
                {
                    scenes.Add(scene.path);
                }
            }

            return scenes;
        }

        public static NamedBuildTarget GetNamedBuildTarget(BuildReport report)
        {
            BuildSummary summary = report.summary;
            if (summary.platformGroup == BuildTargetGroup.Standalone
                && summary.GetSubtarget<StandaloneBuildSubtarget>() == StandaloneBuildSubtarget.Server)
            {
                return NamedBuildTarget.Server;
            }

            return NamedBuildTarget.FromBuildTargetGroup(summary.platformGroup);
        }

        /// <summary>
        /// Every non-empty <c>openRegistrationHeartbeatToken</c> of a <c>PingCoreClientSettings</c>
        /// asset in the project, read through <see cref="SerializedObject"/>, with its asset path.
        /// The values are compared, never logged.
        /// </summary>
        public static IReadOnlyList<BuildGuardConfiguredToken> ReadConfiguredTokens()
        {
            var tokens = new List<BuildGuardConfiguredToken>();
            foreach (string guid in AssetDatabase.FindAssets("t:" + ClientSettingsTypeName))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (asset == null || asset.GetType().Name != ClientSettingsTypeName)
                    {
                        continue;
                    }

                    using (var serialized = new SerializedObject(asset))
                    {
                        SerializedProperty field = serialized.FindProperty(HeartbeatTokenField);
                        if (field != null && field.propertyType == SerializedPropertyType.String
                            && !string.IsNullOrWhiteSpace(field.stringValue))
                        {
                            tokens.Add(new BuildGuardConfiguredToken(path, field.stringValue.Trim()));
                        }
                    }
                }
            }

            return tokens;
        }

        /// <summary>The configured heartbeat tokens resolved against <c>ProjectSettings/PingCoreBuildGuard.json</c>.</summary>
        public static BuildGuardTokenResolution ResolveHeartbeatTokens() =>
            BuildGuardConfirmations.Resolve(ReadConfiguredTokens(), BuildGuardConfirmationFile.Read(ProjectRoot));

        /// <summary>Logs every finding and the summary, then writes the verdict file. Never logs a token.</summary>
        internal static void Report(string stage, string outputPath, IReadOnlyList<BuildGuardFinding> findings, IReadOnlyList<string> warnings,
            BuildGuardOutputCoverage coverage = null) =>
            Report(ProjectRoot, stage, outputPath, findings, warnings, coverage);

        /// <summary>As above, for the project at <paramref name="projectRoot"/>.</summary>
        internal static void Report(string projectRoot, string stage, string outputPath, IReadOnlyList<BuildGuardFinding> findings,
            IReadOnlyList<string> warnings, BuildGuardOutputCoverage coverage)
        {
            foreach (string warning in warnings)
            {
                Debug.LogWarning("[PingCore build guard] " + warning);
            }

            string verdict = findings.Count == 0 ? BuildGuardVerdict.Pass : BuildGuardVerdict.Fail;
            foreach (BuildGuardFinding finding in findings)
            {
                Debug.LogError("[PingCore build guard] " + finding);
            }

            BuildGuardVerdict written = BuildGuardVerdict.Create(verdict, stage, outputPath, findings, warnings);
            if (coverage != null)
            {
                written.outputScan = coverage.OutputScan;
                written.outputScanNotes = new List<string>(coverage.Notes).ToArray();
            }

            BuildGuardVerdictFile.Write(projectRoot, written);
        }

        /// <summary>
        /// Writes a failing verdict with one <c>scan_error</c> finding for a check the guard could not
        /// finish, and returns the exception to throw. The detail names the exception's type only (a
        /// <see cref="BuildGuardReadException"/> carries the guard's own message, which names the file
        /// and the underlying type, and a <see cref="BuildGuardRulesException"/> names the rules file and
        /// each problem, quoting nothing but a key name), never another exception's message, which could
        /// quote file content.
        /// </summary>
        internal static BuildFailedException FailClosed(string projectRoot, string stage, string outputPath, Exception error)
        {
            string detail = DescribeScanError(error);
            Debug.LogError("[PingCore build guard] " + BuildGuardReasons.ScanError + ": " + detail);
            var finding = new BuildGuardFinding(BuildGuardReason.ScanError, outputPath, detail);
            BuildGuardVerdictFile.Write(projectRoot, BuildGuardVerdict.Create(BuildGuardVerdict.Fail, stage, outputPath,
                new[] { finding }, null, new[] { detail }));
            return new BuildFailedException("PingCore build guard (" + stage + ") rejected the build: " + BuildGuardReasons.ScanError + " (" + detail
                + "; details in " + BuildGuardVerdictFile.RelativePath + ")");
        }

        /// <summary>The token-free description of an exception that stopped a scan.</summary>
        internal static string DescribeScanError(Exception error)
        {
            if (error is BuildGuardReadException || error is BuildGuardRulesException)
            {
                return error.Message;
            }

            return "the guard could not finish its scan (" + (error == null ? "unknown error" : error.GetType().Name)
                + "); it fails closed rather than pass a build it could not see";
        }

        /// <summary>A project-relative path with forward slashes when <paramref name="file"/> is inside the project.</summary>
        internal static string ToDisplayPath(string projectRoot, string file)
        {
            string normalized = BuildGuardPolicy.NormalizeFullPath(file);
            string root = BuildGuardPolicy.NormalizeFullPath(projectRoot) + "/";
            return normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? normalized.Substring(root.Length) : normalized;
        }

        internal static string Summarize(string stage, IReadOnlyList<BuildGuardFinding> findings)
        {
            var codes = new List<string>();
            foreach (BuildGuardFinding finding in findings)
            {
                if (!codes.Contains(finding.Code))
                {
                    codes.Add(finding.Code);
                }
            }

            return "PingCore build guard (" + stage + ") rejected the build: " + string.Join(", ", codes)
                + " (" + findings.Count + " finding(s); details in the console and " + BuildGuardVerdictFile.RelativePath + ")";
        }
    }
}
