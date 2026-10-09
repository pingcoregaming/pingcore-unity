using System;
using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// The source-side scan of a finished build, run as the FIRST postprocess step: other
    /// postprocessors may delete files the build packed (the performance test framework removes its
    /// <c>Assets/Resources/PerformanceTestRun*</c> files), so the guard reads them before they go.
    /// A clean or findings-bearing scan is only recorded; <see cref="BuildGuardPostprocessor"/>, which
    /// runs last, decides. A scan that could not finish fails the build here and now (<c>scan_error</c>).
    /// </summary>
    public sealed class BuildGuardSourceStage : IPostprocessBuildWithReport
    {
        /// <summary>The verdict's stage when this step fails the build.</summary>
        public const string Stage = "source";

        public int callbackOrder => int.MinValue + 100;

        public void OnPostprocessBuild(BuildReport report)
        {
            string outputPath = report.summary.outputPath;
            BuildGuardSourceScan scan = Scan(report);
            if (scan.Error != null)
            {
                IReadOnlyList<string> reportFiles = Array.Empty<string>();
                try
                {
                    reportFiles = BuildGuardPostprocessor.ReportFilePaths(report);
                }
                catch (Exception)
                {
                    // Deletion falls back to the exact candidate names; the failure below still stands.
                }

                BuildGuardContext.Clear();
                FailIfIncomplete(BuildGuardContext.ProjectRoot, outputPath, reportFiles, scan);
            }

            BuildGuardContext.StoreSourceScan(outputPath, scan);
        }

        /// <summary>
        /// When <paramref name="scan"/> could not finish, deletes what the build produced, writes a
        /// failing verdict with <c>scan_error</c> and throws <see cref="BuildFailedException"/>.
        /// Otherwise does nothing.
        /// </summary>
        internal static void FailIfIncomplete(string projectRoot, string outputPath, IEnumerable<string> reportFiles, BuildGuardSourceScan scan)
        {
            if (scan?.Error == null)
            {
                return;
            }

            throw BuildGuardFailClosed.Fail(Stage, projectRoot, outputPath, reportFiles, scan.Error);
        }

        /// <summary>
        /// Scans the source file of every packed asset, the build scenes and their dependencies, and
        /// every <c>Resources/</c> and <c>StreamingAssets/</c> folder. Anything that stops the scan is
        /// returned as the result's error, so the build fails closed.
        /// </summary>
        internal static BuildGuardSourceScan Scan(BuildReport report) =>
            Scan(BuildGuardContext.ProjectRoot, BuildGuardContext.ResolveHeartbeatTokens, scanner =>
            {
                scanner.ScanPackedAssets(report);
                scanner.ScanSceneDependencies(BuildGuardContext.CollectScenes(report));
                scanner.ScanShippedFolders();
            });

        /// <summary>
        /// Runs <paramref name="steps"/> on a scanner for <paramref name="projectRoot"/>. Any exception
        /// (a file it could not read, a folder it could not list, a package query that threw) is caught
        /// and returned as <see cref="BuildGuardSourceScan.Error"/>, never swallowed.
        /// </summary>
        internal static BuildGuardSourceScan Scan(string projectRoot, Func<BuildGuardTokenResolution> resolveTokens, Action<BuildSourceScanner> steps)
        {
            BuildGuardTokenResolution tokens = BuildGuardTokenResolution.None;
            BuildSourceScanner scanner = null;
            try
            {
                tokens = resolveTokens();
                scanner = new BuildSourceScanner(projectRoot, tokens.Allowed);
                steps(scanner);
                return new BuildGuardSourceScan(tokens, scanner, null);
            }
            catch (BuildFailedException)
            {
                throw;
            }
            catch (Exception e)
            {
                return new BuildGuardSourceScan(tokens, scanner ?? new BuildSourceScanner(projectRoot, tokens.Allowed), e);
            }
        }
    }

    /// <summary>The result of <see cref="BuildGuardSourceStage.Scan(BuildReport)"/>.</summary>
    internal sealed class BuildGuardSourceScan
    {
        public BuildGuardSourceScan(BuildGuardTokenResolution tokens, BuildSourceScanner scanner, Exception error)
        {
            Tokens = tokens;
            Scanner = scanner;
            Error = error;
        }

        public BuildGuardTokenResolution Tokens { get; }
        public BuildSourceScanner Scanner { get; }

        /// <summary>Why the scan could not finish, or null.</summary>
        public Exception Error { get; }
    }
}
