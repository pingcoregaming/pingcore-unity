using System;
using System.Collections.Generic;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// What the guard could see of a build, as pure decisions: which output entries cannot be
    /// byte-scanned, which packed-asset source paths are Unity's own built-in resources, and the
    /// verdict when the output cannot be scanned.
    /// </summary>
    public static class BuildGuardCoverage
    {
        /// <summary>Every output file was byte-scanned and the shipped assembly list was read.</summary>
        public const string OutputScanComplete = "complete";

        /// <summary>
        /// Part of the output could not be scanned, so the verdict rests on the source-side scan plus a
        /// byte scan of the compiled player assemblies from the build's staging
        /// (<see cref="BuildGuardStagedAssemblies"/>). The archive's own contents were not opened.
        /// </summary>
        public const string OutputScanAssembliesOnly = "assemblies-only";

        /// <summary>Part of the output could not be scanned and nothing vouches for it: the build fails with <c>output_unscannable</c>.</summary>
        public const string OutputScanUnavailable = "unavailable";

        /// <summary>
        /// Archive formats whose contents a byte scan cannot see: Android packages and expansion files,
        /// compressed player data, Brotli and Gzip WebGL files, iOS packages and zip archives.
        /// </summary>
        private static readonly string[] CompressedExtensions = { ".apk", ".aab", ".obb", ".unity3d", ".br", ".gz", ".ipa", ".zip" };

        /// <summary>An Xcode project folder, which an iOS build writes instead of a player.</summary>
        public const string XcodeProjectExtension = ".xcodeproj";

        /// <summary>Source paths a packed asset can name that are Unity's built-in resources, not project files.</summary>
        private static readonly string[] BuiltInSourcePrefixes =
        {
            "Built-in ", "Resources/unity_builtin_extra", "Library/unity default resources", "Library/unity editor resources",
        };

        /// <summary>True when a file or folder name is a format the byte scan cannot see inside.</summary>
        public static bool IsUnscannableOutputName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            if (name.EndsWith(XcodeProjectExtension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (string extension in CompressedExtensions)
            {
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True for an empty source path or one of Unity's built-in resources, which are not project files to scan.</summary>
        public static bool IsBuiltInSourceAsset(string sourceAssetPath)
        {
            if (string.IsNullOrWhiteSpace(sourceAssetPath))
            {
                return true;
            }

            foreach (string prefix in BuiltInSourcePrefixes)
            {
                if (sourceAssetPath.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Decides how far the output scan reaches. Complete when the assembly list was read and no
        /// entry is unscannable. Otherwise the build may pass as <c>assemblies-only</c> only when the
        /// source-side scan ran AND at least one compiled player assembly from the build's staging was
        /// byte-scanned (<paramref name="stagedAssembliesScanned"/>); it fails closed with
        /// <c>output_unscannable</c> when either is missing.
        /// </summary>
        public static BuildGuardOutputCoverage Decide(bool hasAssemblyList, IReadOnlyList<string> unscannableEntries, bool sourceScanRan,
            int stagedAssembliesScanned, string outputLocation)
        {
            var notes = new List<string>();
            if (!hasAssemblyList)
            {
                notes.Add("no shipped assembly list (" + BuildOutputLayout.ScriptingAssembliesFile + " or Managed/*.dll)");
            }

            if (unscannableEntries != null)
            {
                foreach (string entry in unscannableEntries)
                {
                    notes.Add("cannot scan inside " + entry);
                }
            }

            if (notes.Count == 0)
            {
                return new BuildGuardOutputCoverage(OutputScanComplete, notes, null);
            }

            if (sourceScanRan && stagedAssembliesScanned > 0)
            {
                return new BuildGuardOutputCoverage(OutputScanAssembliesOnly, notes, null);
            }

            var missing = new List<string>();
            if (!sourceScanRan)
            {
                missing.Add("the source-side scan of the packed assets did not run");
            }

            if (stagedAssembliesScanned <= 0)
            {
                missing.Add("no compiled player assembly was found to scan (a .dll in the build report, " + BuildGuardStagedAssemblies.BeePlayerScriptAssemblies
                    + ", " + BuildGuardStagedAssemblies.LegacyPlayerScriptAssemblies + " or an IL2CPP " + BuildGuardStagedAssemblies.Il2CppMetadataFile + ")");
            }

            var finding = new BuildGuardFinding(BuildGuardReason.OutputUnscannable, outputLocation ?? string.Empty,
                "the output cannot be byte-scanned (" + string.Join("; ", notes) + ") and " + string.Join(" and ", missing));
            return new BuildGuardOutputCoverage(OutputScanUnavailable, notes, finding);
        }
    }

    /// <summary>The result of <see cref="BuildGuardCoverage.Decide"/>.</summary>
    public sealed class BuildGuardOutputCoverage
    {
        public BuildGuardOutputCoverage(string outputScan, IReadOnlyList<string> notes, BuildGuardFinding finding)
        {
            OutputScan = outputScan;
            Notes = notes ?? Array.Empty<string>();
            Finding = finding;
        }

        /// <summary><see cref="BuildGuardCoverage.OutputScanComplete"/>, <see cref="BuildGuardCoverage.OutputScanAssembliesOnly"/> or <see cref="BuildGuardCoverage.OutputScanUnavailable"/>.</summary>
        public string OutputScan { get; }

        /// <summary>Why the output scan is not complete; empty when complete.</summary>
        public IReadOnlyList<string> Notes { get; }

        /// <summary>The <c>output_unscannable</c> finding, or null.</summary>
        public BuildGuardFinding Finding { get; }
    }
}
