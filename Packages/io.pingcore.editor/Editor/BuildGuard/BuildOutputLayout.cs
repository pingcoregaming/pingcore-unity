using System;
using System.Collections.Generic;
using System.IO;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// Where a player build puts its files, as pure path arithmetic. For an output path
    /// <c>Builds/X/Game.x86_64</c> the build root is <c>Builds/X</c> and the data folder is
    /// <c>Builds/X/Game_Data</c>. The guard scans and deletes only what <see cref="SelectProducedPaths"/>
    /// names, never anything else that happens to sit in the build root.
    /// </summary>
    public static class BuildOutputLayout
    {
        public const string ScriptingAssembliesFile = "ScriptingAssemblies.json";

        /// <summary>
        /// Top-level entries a standalone player writes next to its executable that do not carry the
        /// executable's name.
        /// </summary>
        private static readonly string[] RuntimeEntryNames =
        {
            "UnityPlayer.so", "UnityPlayer.dll", "UnityPlayer.dylib", "UnityPlayer_s.debug", "UnityPlayer.pdb",
            "LinuxPlayer_s.debug", "WindowsPlayer.pdb", "lib_burst_generated.so", "lib_burst_generated.dll",
            "UnityCrashHandler64.exe", "UnityCrashHandler32.exe", "MonoBleedingEdge", "D3D12", "WinPixEventRuntime.dll",
            "libdecor-0.so.0", "libdecor-cairo.so", "GameAssembly.so", "GameAssembly.dll",
        };

        /// <summary>Top-level entries a folder output (WebGL, an exported project) writes.</summary>
        private static readonly string[] FolderOutputEntryNames = { "Build", "TemplateData", "index.html", "StreamingAssets" };

        private const string BurstDebugSuffix = "_BurstDebugInformation_DoNotShip";
        private const string Il2CppBackupSuffix = "_BackUpThisFolder_ButDontShipItWithYourGame";

        /// <summary>
        /// The closed list of suffixes Unity appends to the executable's stem for the entries it writes
        /// beside the executable: the data folder, debug symbols, the Burst debug information and the
        /// IL2CPP backup folder. Nothing else named after the stem (<c>Game.env</c>, <c>Game_secrets.txt</c>,
        /// a sibling folder <c>Game/</c>) is ever treated as produced.
        /// </summary>
        private static readonly string[] StemSuffixes = { "_Data", "_s.debug", ".pdb", ".debug", BurstDebugSuffix, Il2CppBackupSuffix };

        /// <summary>The two folders Unity may name after the PRODUCT rather than the executable.</summary>
        private static readonly string[] ProductSuffixes = { BurstDebugSuffix, Il2CppBackupSuffix };

        private static readonly StringComparison NameComparison =
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>
        /// The folder that holds the build: the output itself when it is a folder the build writes into
        /// (WebGL, an exported project), else its parent. For a macOS <c>.app</c> bundle that is the
        /// folder holding the bundle, so the bundle is one produced entry of it.
        /// </summary>
        public static string GetBuildRoot(string outputPath, bool outputIsDirectory)
        {
            string full = BuildGuardPolicy.NormalizeFullPath(outputPath);
            if (IsFolderOutput(full, outputIsDirectory))
            {
                return full;
            }

            int slash = full.LastIndexOf('/');
            return slash > 0 ? full.Substring(0, slash) : full;
        }

        /// <summary>True when the output is a folder the build writes into (not a macOS <c>.app</c> bundle).</summary>
        public static bool IsFolderOutput(string outputPath, bool outputIsDirectory) =>
            outputIsDirectory && !outputPath.TrimEnd('/', '\\').EndsWith(".app", StringComparison.OrdinalIgnoreCase);

        /// <summary>The last segment of the output path: the executable, bundle or folder name.</summary>
        public static string GetOutputName(string outputPath)
        {
            string full = BuildGuardPolicy.NormalizeFullPath(outputPath);
            int slash = full.LastIndexOf('/');
            return slash >= 0 ? full.Substring(slash + 1) : full;
        }

        /// <summary>The executable's name without extension, which prefixes the data folder.</summary>
        public static string GetExecutableStem(string outputPath) => StemOf(GetOutputName(outputPath));

        private static string StemOf(string name)
        {
            int dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        /// <summary>The player data folder: <c>&lt;stem&gt;_Data</c> beside the executable, or <c>Contents/Resources/Data</c> in a macOS app.</summary>
        public static string GetDataFolder(string outputPath)
        {
            string full = BuildGuardPolicy.NormalizeFullPath(outputPath);
            if (full.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return full + "/Contents/Resources/Data";
            }

            int slash = full.LastIndexOf('/');
            string parent = slash > 0 ? full.Substring(0, slash) : full;
            return parent + "/" + GetExecutableStem(outputPath) + "_Data";
        }

        /// <summary>
        /// The exact top-level names a build of <paramref name="outputName"/> can produce beside it:
        /// the output itself, its stem plus each suffix of the closed list, the Burst and IL2CPP backup
        /// folders under <paramref name="productName"/>, and the Unity runtime files. For a folder
        /// output, the entries WebGL and exported projects write inside it.
        /// </summary>
        public static IReadOnlyList<string> CandidateEntryNames(string outputName, string productName = null, bool isFolderOutput = false)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(outputName))
            {
                return names;
            }

            if (isFolderOutput)
            {
                names.AddRange(FolderOutputEntryNames);
                return names;
            }

            string stem = StemOf(outputName);
            names.Add(outputName);
            foreach (string suffix in StemSuffixes)
            {
                names.Add(stem + suffix);
            }

            if (!string.IsNullOrEmpty(productName))
            {
                foreach (string suffix in ProductSuffixes)
                {
                    names.Add(productName + suffix);
                }
            }

            names.AddRange(RuntimeEntryNames);
            return names;
        }

        /// <summary>
        /// Picks the top-level entries of the build root that this build produced: each entry whose
        /// whole name is one of <see cref="CandidateEntryNames"/>, plus any Xcode project in a folder
        /// output. Matching is by whole name, never by prefix or glob.
        /// </summary>
        public static IReadOnlyList<string> SelectProducedEntries(IEnumerable<string> topLevelEntryNames, string outputName,
            string productName = null, bool isFolderOutput = false)
        {
            var produced = new List<string>();
            if (topLevelEntryNames == null || string.IsNullOrEmpty(outputName))
            {
                return produced;
            }

            IReadOnlyList<string> candidates = CandidateEntryNames(outputName, productName, isFolderOutput);
            foreach (string entry in topLevelEntryNames)
            {
                if (string.IsNullOrEmpty(entry))
                {
                    continue;
                }

                bool exact = false;
                foreach (string candidate in candidates)
                {
                    exact |= string.Equals(candidate, entry, NameComparison);
                }

                bool xcode = isFolderOutput && entry.EndsWith(BuildGuardCoverage.XcodeProjectExtension, StringComparison.OrdinalIgnoreCase);
                if (exact || xcode)
                {
                    produced.Add(entry);
                }
            }

            return produced;
        }

        /// <summary>
        /// Full paths (normalized) of everything this build produced under <paramref name="buildRoot"/>:
        /// the entries <see cref="SelectProducedEntries"/> picks, plus each file the build report lists
        /// that lies inside the build root and is not already inside one of them. A file the report
        /// lists outside the build root is ignored. Nothing else in the build root is ever included,
        /// so an unrelated file beside the build is never read or deleted.
        /// </summary>
        public static IReadOnlyList<string> SelectProducedPaths(string buildRoot, IEnumerable<string> topLevelEntryNames, string outputName,
            string productName, bool isFolderOutput, IEnumerable<string> reportFiles)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(buildRoot))
            {
                return result;
            }

            string root = BuildGuardPolicy.NormalizeFullPath(buildRoot);
            foreach (string name in SelectProducedEntries(topLevelEntryNames, outputName, productName, isFolderOutput))
            {
                result.Add(BuildGuardPolicy.NormalizeFullPath(Path.Combine(root, name)));
            }

            if (reportFiles == null)
            {
                return result;
            }

            foreach (string file in reportFiles)
            {
                if (string.IsNullOrWhiteSpace(file))
                {
                    continue;
                }

                string full = BuildGuardPolicy.NormalizeFullPath(Path.IsPathRooted(file) ? file : Path.Combine(root, file));
                if (string.Equals(full, root, NameComparison) || !BuildGuardPolicy.IsSameOrInside(full, root))
                {
                    continue;
                }

                if (!result.Exists(entry => BuildGuardPolicy.IsSameOrInside(full, entry)))
                {
                    result.Add(full);
                }
            }

            return result;
        }

        /// <summary>
        /// True when the guard may delete produced entries under <paramref name="buildRoot"/>: never
        /// when the build root is the project root, an ancestor of it, or one of the project's own
        /// folders (<c>Assets</c>, <c>Packages</c>, <c>ProjectSettings</c>, <c>Library</c>, <c>UserSettings</c>).
        /// Only <see cref="SelectProducedPaths"/> is ever deleted, wherever the build root is.
        /// </summary>
        public static bool IsSafeToDelete(string buildRoot, string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(buildRoot) || string.IsNullOrWhiteSpace(projectRoot))
            {
                return false;
            }

            string root = BuildGuardPolicy.NormalizeFullPath(projectRoot);
            if (BuildGuardPolicy.IsSameOrInside(root, buildRoot))
            {
                return false;
            }

            foreach (string reserved in new[] { "Assets", "Packages", "ProjectSettings", "Library", "UserSettings" })
            {
                if (BuildGuardPolicy.IsSameOrInside(buildRoot, root + "/" + reserved))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
