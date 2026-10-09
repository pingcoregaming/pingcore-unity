using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>
    /// What a push sends, shown before it runs: the folder, the number of files and their size, leaving out
    /// the top-level folders <c>pingctl</c> is told to exclude (Unity's do-not-ship folders,
    /// <see cref="PingctlCommand.DoNotShipSuffixes"/>, the same rule as <see cref="PingctlCommand.PushArguments"/>).
    /// </summary>
    public sealed class PushFolderSummary
    {
        private PushFolderSummary(string folder, int fileCount, long bytes, IReadOnlyList<string> excluded, string problem)
        {
            Folder = folder;
            FileCount = fileCount;
            Bytes = bytes;
            Excluded = excluded;
            Problem = problem;
        }

        public string Folder { get; }

        /// <summary>Files that will be pushed.</summary>
        public int FileCount { get; }

        /// <summary>Their total size.</summary>
        public long Bytes { get; }

        /// <summary>The top-level folders left out, by name.</summary>
        public IReadOnlyList<string> Excluded { get; }

        /// <summary>Why the folder could not be read, or null.</summary>
        public string Problem { get; }

        /// <summary>
        /// The summary of <paramref name="files"/> (paths inside the folder, either slash, with their sizes). A top-level
        /// folder whose name ends in a do-not-ship suffix, or is <paramref name="product"/> plus one, is left out. Pure.
        /// </summary>
        public static PushFolderSummary Of(string folder, IEnumerable<(string RelativePath, long Size)> files, string product)
        {
            var excluded = new SortedSet<string>(StringComparer.Ordinal);
            int count = 0;
            long bytes = 0;
            foreach ((string relativePath, long size) in files ?? Enumerable.Empty<(string, long)>())
            {
                string path = (relativePath ?? string.Empty).Replace('\\', '/');
                int slash = path.IndexOf('/');
                string top = slash < 0 ? null : path.Substring(0, slash);
                if (top != null && IsDoNotShip(top, product))
                {
                    excluded.Add(top);
                    continue;
                }

                count++;
                bytes += Math.Max(0, size);
            }

            return new PushFolderSummary(folder, count, bytes, excluded.ToList(), null);
        }

        /// <summary>Reads <paramref name="folder"/>; a folder that cannot be read gives a summary with its <see cref="Problem"/>.</summary>
        public static PushFolderSummary Read(string folder, string product)
        {
            try
            {
                string root = Path.GetFullPath(folder);
                var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Select(f => (f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), new FileInfo(f).Length))
                    .ToList();
                return Of(folder, files, product);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                return new PushFolderSummary(folder, 0, 0, Array.Empty<string>(), $"the folder could not be read ({e.GetType().Name})");
            }
        }

        /// <summary>True for a top-level folder pingctl excludes. Pure.</summary>
        public static bool IsDoNotShip(string name, string product)
        {
            return name != null && PingctlCommand.DoNotShipSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal) || name == product + s);
        }

        /// <summary><c>812 KB</c>, <c>41.3 MB</c>, <c>1.20 GB</c>. Pure.</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
            }

            if (bytes < 1024L * 1024)
            {
                return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
            }

            if (bytes < 1024L * 1024 * 1024)
            {
                return (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            }

            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        /// <summary>One line: <c>Builds/Server/v1: 214 files, 41.3 MB (leaving out X_BurstDebugInformation_DoNotShip)</c>.</summary>
        public string Describe()
        {
            if (Problem != null)
            {
                return $"{Folder}: {Problem}";
            }

            string files = FileCount == 1 ? "1 file" : FileCount.ToString(CultureInfo.InvariantCulture) + " files";
            return $"{Folder}: {files}, {FormatSize(Bytes)}" + (Excluded.Count == 0 ? string.Empty : " (leaving out " + string.Join(", ", Excluded) + ")");
        }
    }
}
