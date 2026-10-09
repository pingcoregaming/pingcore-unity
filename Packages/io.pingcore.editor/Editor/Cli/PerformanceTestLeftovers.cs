using System;
using System.Collections.Generic;
using System.IO;

namespace PingCore.Editor.Cli
{
    /// <summary>
    /// The files the performance test framework (<c>com.unity.test-framework.performance</c>, pulled in by
    /// <c>com.unity.test-framework</c>) writes in its build preprocess step and deletes in its postprocess step:
    /// <c>Assets/Resources/PerformanceTestRun*.json</c> with their <c>.meta</c> files, and the <c>Assets/Resources</c>
    /// folder (with <c>Assets/Resources.meta</c>) when it had to create it. A build that fails, or that the build guard
    /// refuses at preprocess, never reaches the postprocess step, so they stay behind. The selection is pure; the
    /// removal is plain file IO on a project root, so the tests run it on a temporary folder.
    /// </summary>
    public static class PerformanceTestLeftovers
    {
        /// <summary>The folder the framework writes into, relative to the project.</summary>
        public const string ResourcesFolder = "Assets/Resources";

        /// <summary>The start of every run file's name.</summary>
        public const string RunFilePrefix = "PerformanceTestRun";

        private static readonly char[] Separators = { '/', '\\' };

        /// <summary>
        /// True for a run file or its <c>.meta</c>: a name (no folder) starting <c>PerformanceTestRun</c> and ending
        /// <c>.json</c> or <c>.json.meta</c>, matched case-insensitively as the Windows file system does. Pure.
        /// </summary>
        public static bool IsRunFile(string fileName) =>
            !string.IsNullOrEmpty(fileName)
            && fileName.IndexOfAny(Separators) < 0
            && fileName.StartsWith(RunFilePrefix, StringComparison.OrdinalIgnoreCase)
            && (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".json.meta", StringComparison.OrdinalIgnoreCase));

        /// <summary>The run files among <paramref name="fileNames"/>, in order. Pure.</summary>
        public static IReadOnlyList<string> SelectRunFiles(IEnumerable<string> fileNames)
        {
            var selected = new List<string>();
            foreach (string name in fileNames ?? Array.Empty<string>())
            {
                if (IsRunFile(name))
                {
                    selected.Add(name);
                }
            }

            return selected;
        }

        /// <summary>
        /// Whether the <c>Assets/Resources</c> folder is the framework's (so it goes once empty), judged before the build:
        /// true when it did not exist, or held nothing but run files (an earlier failed build's leftovers). A folder that
        /// existed empty, or with anything else in it, is the project's and is kept. Pure.
        /// </summary>
        public static bool FolderIsLeftover(bool existedBefore, IEnumerable<string> entryNamesBefore)
        {
            if (!existedBefore)
            {
                return true;
            }

            bool any = false;
            foreach (string name in entryNamesBefore ?? Array.Empty<string>())
            {
                if (!IsRunFile(name))
                {
                    return false;
                }

                any = true;
            }

            return any;
        }

        /// <summary>Whether to remove the folder and its <c>.meta</c> now. Pure.</summary>
        public static bool ShouldRemoveFolder(bool folderIsLeftover, int remainingEntries) => folderIsLeftover && remainingEntries == 0;

        /// <summary>Judges <see cref="FolderIsLeftover(bool, IEnumerable{string})"/> on disk under <paramref name="projectRoot"/>.</summary>
        public static bool FolderIsLeftover(string projectRoot)
        {
            string folder = Path.Combine(projectRoot, ResourcesFolder);
            bool exists = Directory.Exists(folder);
            return FolderIsLeftover(exists, exists ? EntryNames(folder) : Array.Empty<string>());
        }

        /// <summary>
        /// The project-relative paths <see cref="RemoveFromDisk"/> would delete now, in the order it deletes them: the run
        /// files and their <c>.meta</c> files, then the folder and <c>Assets/Resources.meta</c> when
        /// <paramref name="folderIsLeftover"/> and nothing but run files is in it. After a removal this lists what is still
        /// left behind, so <see cref="BuildHousekeeping"/> checks with it. Plain file IO, nothing deleted.
        /// </summary>
        public static IReadOnlyList<string> FindOnDisk(string projectRoot, bool folderIsLeftover)
        {
            var found = new List<string>();
            string folder = Path.Combine(projectRoot, ResourcesFolder);
            bool folderExists = Directory.Exists(folder);
            int remaining = 0;
            if (folderExists)
            {
                IReadOnlyList<string> runFiles = SelectRunFiles(FileNames(folder));
                foreach (string name in runFiles)
                {
                    found.Add(ResourcesFolder + "/" + name);
                }

                remaining = EntryNames(folder).Count - runFiles.Count;
            }

            if (ShouldRemoveFolder(folderIsLeftover, remaining))
            {
                if (folderExists)
                {
                    found.Add(ResourcesFolder);
                }

                if (File.Exists(folder + ".meta"))
                {
                    found.Add(ResourcesFolder + ".meta");
                }
            }

            return found;
        }

        /// <summary>
        /// Deletes the run files and their <c>.meta</c> files under <paramref name="projectRoot"/>, then the folder and
        /// <c>Assets/Resources.meta</c> when <paramref name="folderIsLeftover"/> and nothing else is left in it (the paths
        /// <see cref="FindOnDisk"/> lists). Returns the project-relative paths it deleted. Plain file IO: the caller
        /// refreshes the AssetDatabase.
        /// </summary>
        public static IReadOnlyList<string> RemoveFromDisk(string projectRoot, bool folderIsLeftover)
        {
            var removed = new List<string>();
            foreach (string relative in FindOnDisk(projectRoot, folderIsLeftover))
            {
                string full = Path.Combine(projectRoot, relative);
                if (relative == ResourcesFolder)
                {
                    Directory.Delete(full);
                }
                else
                {
                    File.Delete(full);
                }

                removed.Add(relative);
            }

            return removed;
        }

        private static IReadOnlyList<string> EntryNames(string folder)
        {
            var names = new List<string>();
            foreach (string entry in Directory.GetFileSystemEntries(folder))
            {
                names.Add(Path.GetFileName(entry));
            }

            return names;
        }

        private static IEnumerable<string> FileNames(string folder)
        {
            var names = new List<string>();
            foreach (string file in Directory.GetFiles(folder))
            {
                names.Add(Path.GetFileName(file));
            }

            return names;
        }
    }
}
