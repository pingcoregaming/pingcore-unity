using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEngine;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// The authoritative half of the build guard. After the player is written it checks the source
    /// side (the player assemblies, every packed asset's source file, the build scenes and their
    /// dependencies, every <c>Resources/</c> and <c>StreamingAssets/</c> folder) and the output side
    /// (the shipped assembly list and the bytes of every file this build produced). When the output
    /// cannot be byte-scanned the verdict rests on the source side plus the compiled player
    /// assemblies from the build's staging, recorded as <c>outputScan: "assemblies-only"</c>. On a
    /// failure, including a check that could not finish, it deletes what this build produced, writes
    /// the verdict file and throws <see cref="BuildFailedException"/>, which Unity turns into
    /// <c>BuildResult.Failed</c>.
    /// </summary>
    public sealed class BuildGuardPostprocessor : IPostprocessBuildWithReport
    {
        public const string Stage = "postprocess";

        /// <summary>Runs last, so files added by other postprocessors are scanned too.</summary>
        public int callbackOrder => int.MaxValue - 100;

        public void OnPostprocessBuild(BuildReport report)
        {
            string outputPath = report.summary.outputPath;
            try
            {
                Run(new BuildGuardPostprocessInputs(BuildGuardContext.ProjectRoot, outputPath, () => ReportFilePaths(report),
                    () => BuildGuardContext.CollectDefines(report), PlayerAssemblyNames, () => BuildGuardRules.Read(BuildGuardContext.ProjectRoot),
                    () => BuildGuardContext.TakeSourceScan(outputPath) ?? BuildGuardSourceStage.Scan(report)));
            }
            finally
            {
                BuildGuardContext.Clear();
            }
        }

        /// <summary>
        /// The whole postprocess decision on injectable inputs. A finding fails the build after the
        /// produced output is deleted; any other exception (an unreadable file, an unlistable folder, a
        /// malformed <c>ScriptingAssemblies.json</c>, a failed source-side scan) fails it closed with
        /// <c>scan_error</c>, also after deleting the produced output.
        /// </summary>
        internal static void Run(BuildGuardPostprocessInputs inputs)
        {
            IReadOnlyList<string> reportFiles = Array.Empty<string>();
            try
            {
                reportFiles = inputs.ReportFiles();
                Check(inputs, reportFiles);
            }
            catch (BuildFailedException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw BuildGuardFailClosed.Fail(Stage, inputs.ProjectRoot, inputs.OutputPath, reportFiles, e);
            }
        }

        private static void Check(BuildGuardPostprocessInputs inputs, IReadOnlyList<string> reportFiles)
        {
            string projectRoot = inputs.ProjectRoot;
            string outputPath = inputs.OutputPath;
            string outputFull = BuildOutputScanner.ResolveOutputPath(projectRoot, outputPath);
            IReadOnlyList<string> produced = BuildOutputScanner.ProducedPaths(outputFull, reportFiles);
            // The rules are read inside the guarded block: a malformed rules file fails the build closed with scan_error.
            BuildGuardScope scope = BuildGuardScope.For(inputs.Rules(), projectRoot, outputPath);
            var findings = new List<BuildGuardFinding>();
            findings.AddRange(BuildGuardPolicy.CheckDefines(inputs.Defines(), scope));
            findings.AddRange(BuildGuardPolicy.CheckAssemblies(inputs.PlayerAssemblies(), scope));

            // The source side was scanned by the first postprocess step, before other postprocessors could
            // delete packed files; it is scanned here only when that step did not run for this build.
            BuildGuardSourceScan sourceScan = inputs.SourceScan();
            if (sourceScan.Error != null)
            {
                // Rethrown as itself, so the scan_error verdict names the original exception type.
                ExceptionDispatchInfo.Capture(sourceScan.Error).Throw();
            }

            BuildGuardTokenResolution tokens = sourceScan.Tokens;
            BuildSourceScanner source = sourceScan.Scanner;
            findings.AddRange(tokens.Findings);
            findings.AddRange(source.Findings);

            BuildGuardOutputEvaluation evaluation = BuildOutputScanner.Evaluate(projectRoot, outputPath, scope, tokens.Allowed,
                reportFiles, source.PackedAssetFiles > 0);
            findings.AddRange(evaluation.Findings);

            var warnings = new List<string>();
            foreach (string note in evaluation.Coverage.Notes)
            {
                warnings.Add("output scan " + evaluation.Coverage.OutputScan + ": " + note + "; the verdict rests on the source-side scan and "
                    + evaluation.StagedAssembliesScanned + " compiled player assembly file(s) from the build's staging, not on the archive's contents");
            }

            if (findings.Count == 0)
            {
                BuildGuardContext.Report(projectRoot, Stage, outputPath, findings, warnings, evaluation.Coverage);
                Debug.Log("[PingCore build guard] postprocess passed: " + evaluation.AssemblyCount + " shipped assemblies ("
                    + evaluation.AssemblySource + "), " + evaluation.ScannedFiles + " output files, " + evaluation.StagedAssembliesScanned
                    + " staged assembly files and " + source.ScannedFiles + " source files scanned (" + source.PackedAssetFiles
                    + " packed files listed), output scan " + evaluation.Coverage.OutputScan);
                return;
            }

            BuildOutputScanner.DeleteProduced(projectRoot, outputFull, produced);
            BuildGuardContext.Report(projectRoot, Stage, outputPath, findings, warnings, evaluation.Coverage);
            throw new BuildFailedException(BuildGuardContext.Summarize(Stage, findings));
        }

        /// <summary>
        /// Checks the files of a finished build at <paramref name="outputPath"/>: the shipped assemblies
        /// and the bytes of every file it produced. Has no side effects. Without a build report only the
        /// entries named after the executable and the Unity runtime files count as produced, and the
        /// source-side scan is taken as not run. <paramref name="scope"/> is the project's rules as they apply to this output
        /// (<see cref="BuildGuardScope.For"/>); null is <see cref="BuildGuardScope.Plain"/>.
        /// </summary>
        public static BuildGuardOutputEvaluation Evaluate(string projectRoot, string outputPath, BuildGuardScope scope,
            IReadOnlyCollection<string> allowedDscTokens, IEnumerable<string> reportFiles = null, bool sourceScanRan = false) =>
            BuildOutputScanner.Evaluate(projectRoot, outputPath, scope, allowedDscTokens, reportFiles, sourceScanRan);

        /// <summary>The paths of every file the build report lists.</summary>
        internal static IReadOnlyList<string> ReportFilePaths(BuildReport report)
        {
            var paths = new List<string>();
            foreach (BuildFile file in report.GetFiles())
            {
                if (!string.IsNullOrEmpty(file.path))
                {
                    paths.Add(file.path);
                }
            }

            return paths;
        }

        private static IReadOnlyList<string> PlayerAssemblyNames()
        {
            var names = new List<string>();
            foreach (Assembly assembly in CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies))
            {
                names.Add(assembly.name);
            }

            return names;
        }
    }

    /// <summary>What <see cref="BuildGuardPostprocessor.Run"/> reads, each behind a delegate so a test can make any of them throw.</summary>
    internal sealed class BuildGuardPostprocessInputs
    {
        public BuildGuardPostprocessInputs(string projectRoot, string outputPath, Func<IReadOnlyList<string>> reportFiles,
            Func<IReadOnlyList<string>> defines, Func<IReadOnlyList<string>> playerAssemblies, Func<BuildGuardRules> rules,
            Func<BuildGuardSourceScan> sourceScan)
        {
            ProjectRoot = projectRoot;
            OutputPath = outputPath;
            ReportFiles = reportFiles;
            Defines = defines;
            PlayerAssemblies = playerAssemblies;
            Rules = rules ?? (() => BuildGuardRules.None);
            SourceScan = sourceScan;
        }

        public string ProjectRoot { get; }
        public string OutputPath { get; }
        public Func<IReadOnlyList<string>> ReportFiles { get; }
        public Func<IReadOnlyList<string>> Defines { get; }
        public Func<IReadOnlyList<string>> PlayerAssemblies { get; }
        public Func<BuildGuardRules> Rules { get; }
        public Func<BuildGuardSourceScan> SourceScan { get; }
    }
}
