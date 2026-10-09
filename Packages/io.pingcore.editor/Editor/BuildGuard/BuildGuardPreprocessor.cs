using System;
using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEngine;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// The early half of the build guard. It fails before the player compiles when one of the project's
    /// instrumentation defines (<see cref="BuildGuardRules"/>) is set for a build that does not write inside the
    /// rules' instrumented output folder, when a forbidden assembly (an Editor assembly, or a restricted one
    /// outside an instrumented build) is a player assembly, when a configured heartbeat token is unconfirmed, or
    /// when a token-shaped string is in the C# source of a player assembly, a text asset under
    /// <c>Assets/</c> or an <c>io.pingcore.*</c> package, a build scene or anything it depends on, or
    /// any file under a <c>Resources/</c> or <c>StreamingAssets/</c> folder of the project or any
    /// package. <see cref="BuildGuardPostprocessor"/> is authoritative; this stage exists to fail fast.
    /// </summary>
    public sealed class BuildGuardPreprocessor : IPreprocessBuildWithReport
    {
        public const string Stage = "preprocess";

        public int callbackOrder => int.MinValue + 100;

        public void OnPreprocessBuild(BuildReport report)
        {
            string projectRoot = BuildGuardContext.ProjectRoot;
            string outputPath = report.summary.outputPath;
            BuildGuardVerdictFile.Write(projectRoot,
                BuildGuardVerdict.Create(BuildGuardVerdict.Pending, Stage, outputPath, null, null));
            try
            {
                Check(report, projectRoot, outputPath);
            }
            catch (BuildFailedException)
            {
                throw;
            }
            catch (Exception e)
            {
                // Any failure to finish the checks (an unreadable file, an unlistable folder, a package
                // query that throws) fails the build closed with scan_error.
                BuildGuardContext.Clear();
                throw BuildGuardContext.FailClosed(projectRoot, Stage, outputPath, e);
            }
        }

        private static void Check(BuildReport report, string projectRoot, string outputPath)
        {
            BuildGuardScope scope = BuildGuardScope.For(BuildGuardRules.Read(projectRoot), projectRoot, outputPath);
            var findings = new List<BuildGuardFinding>();
            var warnings = new List<string>();

            findings.AddRange(BuildGuardPolicy.CheckDefines(BuildGuardContext.CollectDefines(report), scope));
            if (findings.Count > 0)
            {
                Fail(outputPath, findings, warnings);
            }

            Assembly[] assemblies = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies);
            var names = new List<string>();
            foreach (Assembly assembly in assemblies)
            {
                names.Add(assembly.name);
            }

            findings.AddRange(BuildGuardPolicy.CheckAssemblies(names, scope));

            BuildGuardTokenResolution tokens = BuildGuardContext.ResolveHeartbeatTokens();
            findings.AddRange(tokens.Findings);

            var scanner = new BuildSourceScanner(projectRoot, tokens.Allowed);
            foreach (Assembly assembly in assemblies)
            {
                foreach (string sourceFile in assembly.sourceFiles)
                {
                    scanner.ScanSourceCode(sourceFile);
                }
            }

            scanner.ScanTextAssets();
            scanner.ScanSceneDependencies(BuildGuardContext.CollectScenes(report));
            scanner.ScanShippedFolders();
            findings.AddRange(scanner.Findings);

            if (findings.Count > 0)
            {
                Fail(outputPath, findings, warnings);
            }

            Debug.Log("[PingCore build guard] preprocess passed: " + assemblies.Length + " player assemblies, "
                + scanner.ScannedFiles + " files scanned, " + scanner.AllowedHits + " confirmed heartbeat token hit(s)"
                + (scope.IsInstrumentedOutput ? " (instrumented output)" : string.Empty));
        }

        private static void Fail(string outputPath, IReadOnlyList<BuildGuardFinding> findings, IReadOnlyList<string> warnings)
        {
            BuildGuardContext.Report(Stage, outputPath, findings, warnings);
            BuildGuardContext.Clear();
            throw new BuildFailedException(BuildGuardContext.Summarize(Stage, findings));
        }
    }
}
