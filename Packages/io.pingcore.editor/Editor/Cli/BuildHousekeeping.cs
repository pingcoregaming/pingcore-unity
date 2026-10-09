using System;
using System.Collections.Generic;
using System.IO;
using PingCore.Editor.BuildGuard;
using UnityEditor;
using UnityEngine;

namespace PingCore.Editor.Cli
{
    /// <summary>
    /// Puts the project back the way a headless build found it. Call <see cref="Begin"/> before the build changes any
    /// player setting, and <see cref="Finish"/> in an outer <c>finally</c> of its own, around the <c>try</c>/<c>finally</c>
    /// that restores the setting itself, so it also runs for a failed or throwing build and when restoring the setting
    /// throws. <see cref="Finish"/> saves Unity's pending settings first, then removes the
    /// <see cref="PerformanceTestLeftovers"/>, then writes <see cref="SettingsFiles"/> back byte for byte
    /// (<see cref="SettingsFileSnapshot"/> says why the bytes and not just the setting), then checks the result
    /// (<see cref="FindProblems"/>) and returns false when anything is left over or differs; the caller fails the build
    /// with <see cref="NotRestoredProblem"/>. Used by <see cref="BuildServer"/>, and by the sample's client build and guard demo.
    /// </summary>
    public sealed class BuildHousekeeping
    {
        /// <summary>The settings files a build is known to re-serialise, relative to the project.</summary>
        public static readonly IReadOnlyList<string> SettingsFiles = new[] { "ProjectSettings/ProjectSettings.asset" };

        /// <summary>Why a build whose <see cref="Finish"/> returned false does not count as a pass, for its FAIL line.</summary>
        public const string NotRestoredProblem = "the project was not put back as the build found it (see the housekeeping errors above)";

        private readonly string projectRoot;
        private readonly List<SettingsFileSnapshot> settings;
        private readonly bool resourcesFolderIsLeftover;

        private BuildHousekeeping(string projectRoot, List<SettingsFileSnapshot> settings, bool resourcesFolderIsLeftover)
        {
            this.projectRoot = projectRoot;
            this.settings = settings;
            this.resourcesFolderIsLeftover = resourcesFolderIsLeftover;
        }

        /// <summary>Snapshots the settings files and the state of <c>Assets/Resources</c> of the open project.</summary>
        public static BuildHousekeeping Begin()
        {
            string projectRoot = BuildGuardContext.ProjectRoot;
            var settings = new List<SettingsFileSnapshot>();
            foreach (string file in SettingsFiles)
            {
                settings.Add(SettingsFileSnapshot.Capture(Path.Combine(projectRoot, file)));
            }

            return new BuildHousekeeping(projectRoot, settings, PerformanceTestLeftovers.FolderIsLeftover(projectRoot));
        }

        /// <summary>
        /// Saves, cleans up and restores as described above, logging each action with <paramref name="logPrefix"/>, and
        /// returns true only when the final check finds nothing (<see cref="FindProblems"/>): false when a settings file
        /// still differs from before the build or a leftover is still on disk, each logged as an error. Never throws, so it
        /// cannot hide the build's own exception; a step that fails is logged as an error, the remaining steps still run,
        /// and a check that cannot run counts as false.
        /// </summary>
        public bool Finish(string logPrefix)
        {
            logPrefix = logPrefix ?? string.Empty;

            // Unity writes its re-serialised settings now, while they are still dirty; the restore below then has the
            // last word and leaves the editor nothing unsaved to write over it on exit.
            Try(logPrefix, "saving the project", () => AssetDatabase.SaveAssets());
            Try(logPrefix, "removing the performance test framework's run files", () =>
            {
                IReadOnlyList<string> removed = PerformanceTestLeftovers.RemoveFromDisk(projectRoot, resourcesFolderIsLeftover);
                if (removed.Count > 0)
                {
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    Debug.Log(logPrefix + "removed " + string.Join(", ", removed));
                }
            });

            foreach (SettingsFileSnapshot snapshot in settings)
            {
                string relative = Relative(projectRoot, snapshot.FullPath);
                Try(logPrefix, "restoring " + relative, () =>
                {
                    if (snapshot.Restore())
                    {
                        Debug.Log(logPrefix + "restored " + relative + " byte for byte (sha256 " + snapshot.OriginalHash + ")");
                    }
                });
            }

            bool clean = false;
            Try(logPrefix, "checking the project", () =>
            {
                IReadOnlyList<string> problems = FindProblems(projectRoot, resourcesFolderIsLeftover, settings);
                foreach (string problem in problems)
                {
                    Debug.LogError(logPrefix + problem);
                }

                clean = problems.Count == 0;
            });
            return clean;
        }

        /// <summary>
        /// What is still wrong with the project after the cleanup and the restore: each settings file that differs from
        /// its snapshot, and each <see cref="PerformanceTestLeftovers"/> path still on disk. Empty when the project is as
        /// the build found it. Plain file IO, no AssetDatabase, so the tests run it on a temporary folder.
        /// </summary>
        public static IReadOnlyList<string> FindProblems(string projectRoot, bool resourcesFolderIsLeftover, IEnumerable<SettingsFileSnapshot> settings)
        {
            var problems = new List<string>();
            foreach (SettingsFileSnapshot snapshot in settings ?? Array.Empty<SettingsFileSnapshot>())
            {
                if (!snapshot.IsIntact())
                {
                    problems.Add(Relative(projectRoot, snapshot.FullPath) + " still differs from before the build (sha256 "
                        + (snapshot.OriginalHash ?? "none, the file did not exist") + ")");
                }
            }

            foreach (string leftover in PerformanceTestLeftovers.FindOnDisk(projectRoot, resourcesFolderIsLeftover))
            {
                problems.Add(leftover + " is still on disk after the cleanup");
            }

            return problems;
        }

        private static string Relative(string projectRoot, string fullPath) =>
            fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(projectRoot.Length).TrimStart('/', '\\').Replace('\\', '/')
                : fullPath;

        private static void Try(string logPrefix, string step, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogError(logPrefix + "housekeeping failed " + step + ": " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
