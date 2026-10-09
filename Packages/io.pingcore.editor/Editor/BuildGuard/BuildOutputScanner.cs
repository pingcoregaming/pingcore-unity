using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// Checks the files a finished build wrote: the shipped assembly list and the bytes of every
    /// produced file (<see cref="BuildOutputLayout.SelectProducedPaths"/>). When the output cannot be
    /// byte-scanned it also scans the compiled player assemblies from the build's staging
    /// (<see cref="BuildGuardStagedAssemblies"/>), read-only. Nothing else in the build root is read,
    /// and only produced paths are ever deleted.
    /// </summary>
    public static class BuildOutputScanner
    {
        /// <summary>Resolves an output path the way the build does: relative paths are under the project.</summary>
        public static string ResolveOutputPath(string projectRoot, string outputPath) =>
            BuildGuardPolicy.NormalizeFullPath(Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(projectRoot, outputPath));

        /// <summary>
        /// Full paths of what this build produced (see <see cref="BuildOutputLayout.SelectProducedPaths"/>).
        /// <paramref name="productName"/> defaults to the project's product name.
        /// </summary>
        public static IReadOnlyList<string> ProducedPaths(string outputFullPath, IEnumerable<string> reportFiles, string productName = null)
        {
            bool outputIsDirectory = Directory.Exists(outputFullPath);
            string buildRoot = BuildOutputLayout.GetBuildRoot(outputFullPath, outputIsDirectory);
            if (!Directory.Exists(buildRoot))
            {
                return Array.Empty<string>();
            }

            var names = new List<string>();
            foreach (string entry in Directory.GetFileSystemEntries(buildRoot))
            {
                names.Add(Path.GetFileName(entry));
            }

            return BuildOutputLayout.SelectProducedPaths(buildRoot, names, BuildOutputLayout.GetOutputName(outputFullPath),
                productName ?? PlayerSettings.productName, BuildOutputLayout.IsFolderOutput(outputFullPath, outputIsDirectory), reportFiles);
        }

        /// <summary>
        /// What to delete when a check could not finish: <see cref="ProducedPaths"/>, or, when the build
        /// root cannot even be listed, every exact candidate name (<see cref="BuildOutputLayout.CandidateEntryNames"/>)
        /// plus the report's files inside the build root. Never anything else.
        /// </summary>
        internal static IReadOnlyList<string> ProducedPathsForDeletion(string outputFullPath, IEnumerable<string> reportFiles)
        {
            try
            {
                return ProducedPaths(outputFullPath, reportFiles);
            }
            catch (Exception)
            {
                bool outputIsDirectory = Directory.Exists(outputFullPath);
                bool isFolderOutput = BuildOutputLayout.IsFolderOutput(outputFullPath, outputIsDirectory);
                string outputName = BuildOutputLayout.GetOutputName(outputFullPath);
                string productName = PlayerSettings.productName;
                return BuildOutputLayout.SelectProducedPaths(BuildOutputLayout.GetBuildRoot(outputFullPath, outputIsDirectory),
                    BuildOutputLayout.CandidateEntryNames(outputName, productName, isFolderOutput), outputName, productName, isFolderOutput, reportFiles);
            }
        }

        /// <summary>Checks a finished build. Has no side effects.</summary>
        public static BuildGuardOutputEvaluation Evaluate(string projectRoot, string outputPath, BuildGuardScope scope,
            IReadOnlyCollection<string> allowedDscTokens, IEnumerable<string> reportFiles, bool sourceScanRan)
        {
            string outputFull = ResolveOutputPath(projectRoot, outputPath);
            var findings = new List<BuildGuardFinding>();
            var flagged = new List<string>();
            var unscannable = new List<string>();

            (IReadOnlyList<string> assemblies, string source) = ReadShippedAssemblies(outputFull);
            findings.AddRange(BuildGuardPolicy.CheckAssemblies(assemblies, scope));

            int allowedHits = 0;
            int scanned = 0;
            foreach (string file in EnumerateFiles(ProducedPaths(outputFull, reportFiles), unscannable, projectRoot))
            {
                if (BuildGuardCoverage.IsUnscannableOutputName(Path.GetFileName(file)))
                {
                    unscannable.Add(BuildGuardContext.ToDisplayPath(projectRoot, file));
                }

                BuildGuardScanResult result = BuildGuardFileScanner.ScanFile(file, BuildGuardContext.ToDisplayPath(projectRoot, file), allowedDscTokens);
                findings.AddRange(result.Findings);
                if (result.Findings.Count > 0)
                {
                    flagged.Add(BuildGuardPolicy.NormalizeFullPath(file));
                }

                allowedHits += result.AllowedDscTokenHits;
                scanned++;
            }

            // The output hides part of itself: scan the compiled code from the build's staging instead (read-only).
            int staged = 0;
            if (assemblies.Count == 0 || unscannable.Count > 0)
            {
                foreach (string file in BuildGuardStagedAssemblies.Find(projectRoot, reportFiles))
                {
                    BuildGuardScanResult result = BuildGuardFileScanner.ScanFile(file, BuildGuardContext.ToDisplayPath(projectRoot, file), allowedDscTokens);
                    findings.AddRange(result.Findings);
                    allowedHits += result.AllowedDscTokenHits;
                    staged++;
                }
            }

            BuildGuardOutputCoverage coverage = BuildGuardCoverage.Decide(assemblies.Count > 0, unscannable, sourceScanRan, staged,
                BuildGuardContext.ToDisplayPath(projectRoot, outputFull));
            if (coverage.Finding != null)
            {
                findings.Add(coverage.Finding);
            }

            return new BuildGuardOutputEvaluation(findings, flagged, assemblies.Count, source, scanned, staged, allowedHits, coverage);
        }

        /// <summary>
        /// The shipped assemblies: the names in <c>&lt;name&gt;_Data/ScriptingAssemblies.json</c>,
        /// falling back to the file names in <c>&lt;name&gt;_Data/Managed/*.dll</c>. A malformed list
        /// throws, and the guard then fails closed with <c>scan_error</c>.
        /// </summary>
        public static (IReadOnlyList<string> Names, string Source) ReadShippedAssemblies(string outputPath)
        {
            string dataFolder = BuildOutputLayout.GetDataFolder(outputPath);
            string listPath = Path.Combine(dataFolder, BuildOutputLayout.ScriptingAssembliesFile);
            if (File.Exists(listPath))
            {
                ScriptingAssembliesList list = JsonUtility.FromJson<ScriptingAssembliesList>(File.ReadAllText(listPath));
                if (list != null && list.names != null && list.names.Length > 0)
                {
                    return (list.names, BuildOutputLayout.ScriptingAssembliesFile);
                }
            }

            var names = new List<string>();
            string managed = Path.Combine(dataFolder, "Managed");
            if (Directory.Exists(managed))
            {
                foreach (string dll in Directory.GetFiles(managed, "*.dll", SearchOption.AllDirectories))
                {
                    names.Add(Path.GetFileName(dll));
                }
            }

            return (names, names.Count > 0 ? "Managed/*.dll" : "none");
        }

        /// <summary>
        /// Deletes the produced paths of a rejected build, unless the build root is the project or one
        /// of its own folders. Never deletes anything that is not a produced path.
        /// </summary>
        internal static void DeleteProduced(string projectRoot, string outputFullPath, IReadOnlyList<string> producedPaths)
        {
            string buildRoot = BuildOutputLayout.GetBuildRoot(outputFullPath, Directory.Exists(outputFullPath));
            if (!BuildOutputLayout.IsSafeToDelete(buildRoot, projectRoot))
            {
                Debug.LogError("[PingCore build guard] the rejected output was NOT deleted, because its folder is the project or one of its "
                    + "own folders: " + buildRoot + ". Delete it by hand and never ship it.");
                return;
            }

            foreach (string entry in producedPaths)
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, true);
                    }
                    else if (File.Exists(entry))
                    {
                        File.Delete(entry);
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    Debug.LogError("[PingCore build guard] could not delete rejected output " + entry + " (" + e.GetType().Name + ")");
                }
            }
        }

        /// <summary>Every file under the produced paths; an Xcode project folder is noted as unscannable.</summary>
        private static IEnumerable<string> EnumerateFiles(IEnumerable<string> entries, List<string> unscannable, string projectRoot)
        {
            foreach (string entry in entries)
            {
                if (File.Exists(entry))
                {
                    yield return entry;
                    continue;
                }

                if (!Directory.Exists(entry))
                {
                    continue;
                }

                foreach (string directory in Directory.GetDirectories(entry, "*", SearchOption.AllDirectories))
                {
                    if (directory.EndsWith(BuildGuardCoverage.XcodeProjectExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        unscannable.Add(BuildGuardContext.ToDisplayPath(projectRoot, directory));
                    }
                }

                if (entry.EndsWith(BuildGuardCoverage.XcodeProjectExtension, StringComparison.OrdinalIgnoreCase))
                {
                    unscannable.Add(BuildGuardContext.ToDisplayPath(projectRoot, entry));
                }

                foreach (string file in Directory.GetFiles(entry, "*", SearchOption.AllDirectories))
                {
                    yield return file;
                }
            }
        }

        [Serializable]
        private sealed class ScriptingAssembliesList
        {
            public string[] names;
        }
    }

    /// <summary>The result of <see cref="BuildOutputScanner.Evaluate"/>.</summary>
    public sealed class BuildGuardOutputEvaluation
    {
        public BuildGuardOutputEvaluation(IReadOnlyList<BuildGuardFinding> findings, IReadOnlyList<string> flaggedFiles, int assemblyCount,
            string assemblySource, int scannedFiles, int stagedAssembliesScanned, int allowedDscTokenHits, BuildGuardOutputCoverage coverage)
        {
            Findings = findings;
            FlaggedFiles = flaggedFiles;
            AssemblyCount = assemblyCount;
            AssemblySource = assemblySource;
            ScannedFiles = scannedFiles;
            StagedAssembliesScanned = stagedAssembliesScanned;
            AllowedDscTokenHits = allowedDscTokenHits;
            Coverage = coverage;
        }

        public IReadOnlyList<BuildGuardFinding> Findings { get; }

        /// <summary>Normalized full paths of the scanned output files that carried a <c>secret_literal</c> finding; always produced paths.</summary>
        public IReadOnlyList<string> FlaggedFiles { get; }

        public int AssemblyCount { get; }
        public string AssemblySource { get; }
        public int ScannedFiles { get; }

        /// <summary>How many compiled player assemblies from the build's staging were scanned because the output hid part of itself.</summary>
        public int StagedAssembliesScanned { get; }

        public int AllowedDscTokenHits { get; }

        /// <summary>How far the output scan reached.</summary>
        public BuildGuardOutputCoverage Coverage { get; }
    }
}
