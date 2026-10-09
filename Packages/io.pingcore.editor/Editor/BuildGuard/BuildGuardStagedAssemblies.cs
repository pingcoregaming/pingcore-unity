using System;
using System.Collections.Generic;
using System.IO;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// The compiled player code a build used, found in the build's own staging, for when the output
    /// itself cannot be byte-scanned (an <c>.apk</c>, <c>.aab</c>, compressed player data, an Xcode
    /// project, no shipped assembly list). The source-side scan reads C# text, not compiled IL, so a
    /// constant-folded token (<c>"usr" + "_" + ...</c>) is only visible in these files. They are read,
    /// never deleted. The archive itself is still not opened: an asset packed only inside it is
    /// covered by the source-side scan alone.
    /// </summary>
    public static class BuildGuardStagedAssemblies
    {
        /// <summary>Where Unity 6 compiles player script assemblies.</summary>
        public const string BeePlayerScriptAssemblies = "Library/Bee/PlayerScriptAssemblies";

        /// <summary>Where older editors compiled player script assemblies.</summary>
        public const string LegacyPlayerScriptAssemblies = "Library/PlayerScriptAssemblies";

        /// <summary>Where IL2CPP stages its build artifacts, including the string literal metadata.</summary>
        public const string Il2CppArtifacts = "Library/Bee/artifacts";

        /// <summary>IL2CPP's metadata file, which holds every managed string literal of an IL2CPP player.</summary>
        public const string Il2CppMetadataFile = "global-metadata.dat";

        /// <summary>
        /// Picks the files to scan: every <c>.dll</c> the build report lists, wherever it lies (inside or
        /// outside the build root); only when the report lists none, the <c>.dll</c> files of the
        /// staging folders; and in both cases every IL2CPP <c>global-metadata.dat</c>. Pure: it only
        /// filters the names it is handed. Empty means nothing vouches for the compiled code.
        /// </summary>
        public static IReadOnlyList<string> Select(IEnumerable<string> reportFiles, IEnumerable<string> stagingFiles,
            IEnumerable<string> il2cppArtifactFiles)
        {
            var picked = new List<string>();
            AddMatching(picked, reportFiles, IsAssembly);
            if (picked.Count == 0)
            {
                AddMatching(picked, stagingFiles, IsAssembly);
            }

            AddMatching(picked, il2cppArtifactFiles, IsIl2CppMetadata);
            return picked;
        }

        /// <summary>
        /// Lists the staging folders under <paramref name="projectRoot"/> and applies <see cref="Select"/>.
        /// A relative report path is taken relative to the project.
        /// </summary>
        public static IReadOnlyList<string> Find(string projectRoot, IEnumerable<string> reportFiles)
        {
            var absoluteReportFiles = new List<string>();
            foreach (string file in reportFiles ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(file))
                {
                    absoluteReportFiles.Add(Path.IsPathRooted(file) ? file : Path.Combine(projectRoot, file));
                }
            }

            var staging = new List<string>();
            foreach (string folder in new[] { BeePlayerScriptAssemblies, LegacyPlayerScriptAssemblies })
            {
                string path = Path.Combine(projectRoot, folder);
                if (Directory.Exists(path))
                {
                    staging.AddRange(Directory.GetFiles(path, "*.dll", SearchOption.TopDirectoryOnly));
                }
            }

            var metadata = new List<string>();
            string artifacts = Path.Combine(projectRoot, Il2CppArtifacts);
            if (Directory.Exists(artifacts))
            {
                metadata.AddRange(Directory.GetFiles(artifacts, Il2CppMetadataFile, SearchOption.AllDirectories));
            }

            var existing = new List<string>();
            foreach (string file in Select(absoluteReportFiles, staging, metadata))
            {
                if (File.Exists(file))
                {
                    existing.Add(file);
                }
            }

            return existing;
        }

        private static bool IsAssembly(string path) => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

        private static bool IsIl2CppMetadata(string path) =>
            string.Equals(Path.GetFileName(path.Replace('\\', '/')), Il2CppMetadataFile, StringComparison.OrdinalIgnoreCase);

        private static void AddMatching(List<string> picked, IEnumerable<string> files, Func<string, bool> matches)
        {
            if (files == null)
            {
                return;
            }

            foreach (string file in files)
            {
                if (string.IsNullOrWhiteSpace(file) || !matches(file))
                {
                    continue;
                }

                string normalized = BuildGuardPolicy.NormalizeFullPath(file);
                if (!picked.Exists(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    picked.Add(normalized);
                }
            }
        }
    }
}
