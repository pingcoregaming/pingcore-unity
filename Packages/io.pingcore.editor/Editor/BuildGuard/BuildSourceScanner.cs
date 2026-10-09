using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// The source side of the guard: scans the project files a build packs, whatever their package
    /// or extension, so a token is caught even when the output is compressed or not a standalone
    /// player. Every file is byte-scanned once per stage (text assets are UTF-8 YAML; binaries use
    /// the same scanner); a file that cannot be read fails the build.
    /// </summary>
    internal sealed class BuildSourceScanner
    {
        private const string ResourcesFolder = "Resources";
        private const string StreamingAssetsFolder = "StreamingAssets";

        /// <summary>Extensions scanned as text assets by the preprocessor's early sweep of <c>Assets/</c> and the <c>io.pingcore.*</c> packages.</summary>
        private static readonly HashSet<string> TextAssetExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".json", ".xml", ".yaml", ".yml", ".bytes", ".csv", ".tsv", ".html", ".htm", ".md",
            ".asset", ".prefab", ".unity", ".mat", ".controller", ".overrideController", ".anim", ".playable",
            ".signal", ".preset", ".uss", ".uxml", ".tss", ".mixer", ".lighting", ".spriteatlas", ".spriteatlasv2",
            ".inputactions", ".asmdef", ".asmref", ".rsp", ".shadergraph", ".shadersubgraph", ".vfx", ".ini", ".cfg",
            ".properties", ".env",
        };

        private readonly string projectRoot;
        private readonly IReadOnlyCollection<string> allowed;
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<BuildGuardFinding> findings = new List<BuildGuardFinding>();

        public BuildSourceScanner(string projectRoot, IReadOnlyCollection<string> allowedDscTokens)
        {
            this.projectRoot = projectRoot;
            allowed = allowedDscTokens;
        }

        public IReadOnlyList<BuildGuardFinding> Findings => findings;
        public int AllowedHits { get; private set; }
        public int ScannedFiles { get; private set; }

        /// <summary>How many packed files the report listed; zero means the report did not say what was packed.</summary>
        public int PackedAssetFiles { get; private set; }

        /// <summary>The C# source of a player assembly, scanned as text with its byte order mark honoured.</summary>
        public void ScanSourceCode(string assetOrPhysicalPath)
        {
            string physical = ToPhysicalPath(assetOrPhysicalPath);
            if (physical == null || !seen.Add(physical))
            {
                return;
            }

            Add(BuildGuardPolicy.ScanText(BuildGuardFileScanner.ReadText(physical), assetOrPhysicalPath, allowed));
        }

        /// <summary>The source file of every asset in <see cref="BuildReport.packedAssets"/>, except Unity's built-in resources.</summary>
        public void ScanPackedAssets(BuildReport report)
        {
            PackedAssets[] packed = report.packedAssets ?? Array.Empty<PackedAssets>();
            PackedAssetFiles = packed.Length;
            foreach (PackedAssets file in packed)
            {
                foreach (PackedAssetInfo info in file.contents)
                {
                    ScanAsset(info.sourceAssetPath);
                }
            }
        }

        /// <summary>Every scene of the build and everything it depends on, recursively.</summary>
        public void ScanSceneDependencies(IReadOnlyList<string> scenes)
        {
            if (scenes == null || scenes.Count == 0)
            {
                return;
            }

            foreach (string dependency in AssetDatabase.GetDependencies(new List<string>(scenes).ToArray(), true))
            {
                ScanAsset(dependency);
            }
        }

        /// <summary>Every file under a <c>Resources</c> or <c>StreamingAssets</c> folder of <c>Assets/</c> and of every package, any extension.</summary>
        public void ScanShippedFolders()
        {
            foreach ((string root, string assetPrefix) in ProjectRoots(false))
            {
                ScanFolder(root, assetPrefix, false);
            }
        }

        /// <summary>Text assets under <c>Assets/</c> and the <c>io.pingcore.*</c> packages, whether or not they ship (fail-fast sweep).</summary>
        public void ScanTextAssets()
        {
            foreach ((string root, string assetPrefix) in ProjectRoots(true))
            {
                ScanFolder(root, assetPrefix, true);
            }
        }

        /// <summary>
        /// Scans the files <see cref="IsSelected"/> picks under one root (<c>Assets/</c> or a package's
        /// resolved folder), reporting each under <paramref name="assetPrefix"/>.
        /// </summary>
        internal void ScanFolder(string root, string assetPrefix, bool textAssets)
        {
            foreach (string file in EnumerateImportedFiles(root, textAssets))
            {
                ScanPhysical(file, ToAssetDisplayPath(root, assetPrefix, file));
            }
        }

        private void ScanAsset(string assetPath)
        {
            if (BuildGuardCoverage.IsBuiltInSourceAsset(assetPath))
            {
                return;
            }

            ScanPhysical(ToPhysicalPath(assetPath), assetPath);
        }

        /// <summary>Scans one file once, reporting it at <paramref name="displayPath"/> (its asset path where it has one).</summary>
        private void ScanPhysical(string physical, string displayPath)
        {
            if (physical == null || !seen.Add(physical))
            {
                return;
            }

            Add(BuildGuardFileScanner.ScanFile(physical, displayPath ?? BuildGuardContext.ToDisplayPath(projectRoot, physical), allowed));
        }

        /// <summary>
        /// The asset path of a file found under a scan root: <c>Assets/...</c> or <c>Packages/&lt;name&gt;/...</c>,
        /// whatever folder the package resolves to on disk, so a finding names the package it came from.
        /// </summary>
        internal static string ToAssetDisplayPath(string root, string assetPrefix, string file)
        {
            string normalizedRoot = BuildGuardPolicy.NormalizeFullPath(root) + "/";
            string normalizedFile = BuildGuardPolicy.NormalizeFullPath(file);
            return normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? assetPrefix + "/" + normalizedFile.Substring(normalizedRoot.Length)
                : normalizedFile;
        }

        private void Add(BuildGuardScanResult result)
        {
            findings.AddRange(result.Findings);
            AllowedHits += result.AllowedDscTokenHits;
            ScannedFiles++;
        }

        /// <summary>
        /// <c>Assets/</c> and the resolved folder of every registered package (or only the <c>io.pingcore.*</c>
        /// ones), each with the asset path prefix its files are reported under.
        /// </summary>
        private static IEnumerable<(string Root, string AssetPrefix)> ProjectRoots(bool pingcoreOnly)
        {
            yield return (Application.dataPath, "Assets");
            foreach (UnityEditor.PackageManager.PackageInfo package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if ((!pingcoreOnly || package.name.StartsWith("io.pingcore.", StringComparison.Ordinal))
                    && !string.IsNullOrEmpty(package.resolvedPath) && Directory.Exists(package.resolvedPath))
                {
                    yield return (package.resolvedPath, string.IsNullOrEmpty(package.assetPath) ? "Packages/" + package.name : package.assetPath);
                }
            }
        }

        /// <summary>A folder Unity never imports: its name ends in <c>~</c> or starts with <c>.</c>.</summary>
        internal static bool IsIgnoredFolder(string name) =>
            name.EndsWith("~", StringComparison.Ordinal) || name.StartsWith(".", StringComparison.Ordinal);

        /// <summary>
        /// The pure selection rule for a file at <paramref name="relativePath"/> (forward slashes) under
        /// a scan root. A file inside an ignored folder, a <c>.meta</c> file and a dot-file are never
        /// selected. With <paramref name="textAssets"/> it selects text assets anywhere plus every file in
        /// a <c>StreamingAssets</c> folder; without it, every file, any extension, inside a
        /// <c>Resources</c> or <c>StreamingAssets</c> folder at any depth.
        /// </summary>
        internal static bool IsSelected(string relativePath, bool textAssets)
        {
            string[] segments = relativePath.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return false;
            }

            bool shipped = false;
            bool streaming = false;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (IsIgnoredFolder(segments[i]))
                {
                    return false;
                }

                streaming |= segments[i] == StreamingAssetsFolder;
                shipped |= segments[i] == ResourcesFolder || segments[i] == StreamingAssetsFolder;
            }

            string fileName = segments[segments.Length - 1];
            string extension = Path.GetExtension(fileName);
            if (string.Equals(extension, ".meta", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith(".", StringComparison.Ordinal))
            {
                return false;
            }

            return textAssets ? streaming || TextAssetExtensions.Contains(extension) : shipped;
        }

        /// <summary>
        /// Files Unity imports under a root that <see cref="IsSelected"/> picks. Ignored folders are not
        /// descended into.
        /// </summary>
        private static IEnumerable<string> EnumerateImportedFiles(string root, bool textAssets)
        {
            string normalizedRoot = BuildGuardPolicy.NormalizeFullPath(root);
            var pending = new Stack<string>();
            pending.Push(normalizedRoot);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string child in Directory.GetDirectories(directory))
                {
                    if (!IsIgnoredFolder(Path.GetFileName(child)))
                    {
                        pending.Push(child);
                    }
                }

                foreach (string file in Directory.GetFiles(directory))
                {
                    string normalized = BuildGuardPolicy.NormalizeFullPath(file);
                    if (IsSelected(normalized.Substring(normalizedRoot.Length).TrimStart('/'), textAssets))
                    {
                        yield return normalized;
                    }
                }
            }
        }

        /// <summary>
        /// The file behind an asset path (<c>Packages/...</c> paths resolve into the package folder).
        /// A path that does not resolve is still returned, so reading it fails the build instead of
        /// skipping the file.
        /// </summary>
        private string ToPhysicalPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return null;
            }

            string physical = FileUtil.GetPhysicalPath(assetPath);
            string path = string.IsNullOrEmpty(physical) ? assetPath : physical;
            return BuildGuardPolicy.NormalizeFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path));
        }
    }
}
