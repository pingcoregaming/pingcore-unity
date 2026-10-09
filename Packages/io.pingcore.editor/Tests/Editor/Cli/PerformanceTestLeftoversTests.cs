using System;
using System.IO;
using NUnit.Framework;
using PingCore.Editor.BuildGuard;
using PingCore.Editor.Cli;
using UnityEditor;

namespace PingCore.Editor.Tests.Cli
{
    /// <summary>
    /// What a failed build's cleanup deletes under <c>Assets/Resources</c>: the performance test framework's run files and
    /// their <c>.meta</c> files, and the folder with <c>Assets/Resources.meta</c> only when the folder was the framework's.
    /// The selection is pure; the removal runs on a temporary project root and once on the open project.
    /// </summary>
    public sealed class PerformanceTestLeftoversTests
    {
        private string root;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "pingcore-perf-leftovers-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        [TestCase("PerformanceTestRunInfo.json")]
        [TestCase("PerformanceTestRunInfo.json.meta")]
        [TestCase("PerformanceTestRunSettings.json")]
        [TestCase("PerformanceTestRunSettings.json.meta")]
        [TestCase("performancetestruninfo.JSON")]
        public void TheFrameworksRunFilesAndTheirMetasAreSelected(string name)
        {
            Assert.That(PerformanceTestLeftovers.IsRunFile(name), Is.True);
        }

        [TestCase("PerformanceTestRunInfo.txt")]
        [TestCase("PerformanceTestRunInfo.json.bak")]
        [TestCase("MyPerformanceTestRunInfo.json")]
        [TestCase("PerformanceTest.json")]
        [TestCase("GameConfig.json")]
        [TestCase("GameConfig.json.meta")]
        [TestCase("Sub/PerformanceTestRunInfo.json")]
        [TestCase("Sub\\PerformanceTestRunInfo.json")]
        [TestCase("")]
        [TestCase(null)]
        public void AnythingElseIsNotSelected(string name)
        {
            Assert.That(PerformanceTestLeftovers.IsRunFile(name), Is.False);
        }

        [Test]
        public void SelectionKeepsTheRunFilesInOrderAndNothingElse()
        {
            var names = new[] { "Config.asset", "PerformanceTestRunInfo.json", "Config.asset.meta", "PerformanceTestRunInfo.json.meta", "PerformanceTestRunSettings.json" };
            Assert.That(PerformanceTestLeftovers.SelectRunFiles(names),
                Is.EqualTo(new[] { "PerformanceTestRunInfo.json", "PerformanceTestRunInfo.json.meta", "PerformanceTestRunSettings.json" }));
            Assert.That(PerformanceTestLeftovers.SelectRunFiles(null), Is.Empty);
        }

        [Test]
        public void TheFolderIsTheFrameworksWhenItWasMissingOrHeldOnlyRunFiles()
        {
            Assert.That(PerformanceTestLeftovers.FolderIsLeftover(false, Array.Empty<string>()), Is.True);
            Assert.That(PerformanceTestLeftovers.FolderIsLeftover(true, new[] { "PerformanceTestRunInfo.json", "PerformanceTestRunInfo.json.meta" }), Is.True);
            Assert.That(PerformanceTestLeftovers.FolderIsLeftover(true, Array.Empty<string>()), Is.False, "an empty folder that existed is the project's");
            Assert.That(PerformanceTestLeftovers.FolderIsLeftover(true, new[] { "PerformanceTestRunInfo.json", "Config.asset" }), Is.False);
            Assert.That(PerformanceTestLeftovers.FolderIsLeftover(true, new[] { "Sub" }), Is.False);
        }

        [Test]
        public void TheFolderGoesOnlyWhenItIsTheFrameworksAndEmpty()
        {
            Assert.That(PerformanceTestLeftovers.ShouldRemoveFolder(true, 0), Is.True);
            Assert.That(PerformanceTestLeftovers.ShouldRemoveFolder(true, 1), Is.False);
            Assert.That(PerformanceTestLeftovers.ShouldRemoveFolder(false, 0), Is.False);
        }

        [Test]
        public void AFolderTheBuildCreatedIsRemovedWithItsMeta()
        {
            bool leftover = PerformanceTestLeftovers.FolderIsLeftover(root);
            Plant("PerformanceTestRunInfo.json", "PerformanceTestRunInfo.json.meta", "PerformanceTestRunSettings.json", "PerformanceTestRunSettings.json.meta");
            File.WriteAllText(Path.Combine(root, "Assets", "Resources.meta"), "guid: 0\n");

            Assert.That(PerformanceTestLeftovers.RemoveFromDisk(root, leftover), Is.EqualTo(new[]
            {
                "Assets/Resources/PerformanceTestRunInfo.json", "Assets/Resources/PerformanceTestRunInfo.json.meta",
                "Assets/Resources/PerformanceTestRunSettings.json", "Assets/Resources/PerformanceTestRunSettings.json.meta",
                "Assets/Resources", "Assets/Resources.meta",
            }));
            Assert.That(Directory.Exists(Path.Combine(root, "Assets", "Resources")), Is.False);
            Assert.That(File.Exists(Path.Combine(root, "Assets", "Resources.meta")), Is.False);
        }

        [Test]
        public void AnEarlierFailedBuildsFolderIsRemovedToo()
        {
            Plant("PerformanceTestRunInfo.json", "PerformanceTestRunInfo.json.meta");
            File.WriteAllText(Path.Combine(root, "Assets", "Resources.meta"), "guid: 0\n");
            bool leftover = PerformanceTestLeftovers.FolderIsLeftover(root);

            PerformanceTestLeftovers.RemoveFromDisk(root, leftover);

            Assert.That(leftover, Is.True);
            Assert.That(Directory.Exists(Path.Combine(root, "Assets", "Resources")), Is.False);
            Assert.That(File.Exists(Path.Combine(root, "Assets", "Resources.meta")), Is.False);
        }

        [Test]
        public void TheProjectsOwnResourcesAndFolderAreKept()
        {
            Plant("Config.asset", "Config.asset.meta");
            File.WriteAllText(Path.Combine(root, "Assets", "Resources.meta"), "guid: 0\n");
            bool leftover = PerformanceTestLeftovers.FolderIsLeftover(root);
            Plant("PerformanceTestRunInfo.json", "PerformanceTestRunInfo.json.meta");

            Assert.That(PerformanceTestLeftovers.RemoveFromDisk(root, leftover),
                Is.EqualTo(new[] { "Assets/Resources/PerformanceTestRunInfo.json", "Assets/Resources/PerformanceTestRunInfo.json.meta" }));
            Assert.That(File.Exists(Path.Combine(root, "Assets", "Resources", "Config.asset")), Is.True);
            Assert.That(File.Exists(Path.Combine(root, "Assets", "Resources", "Config.asset.meta")), Is.True);
            Assert.That(File.Exists(Path.Combine(root, "Assets", "Resources.meta")), Is.True);
        }

        [Test]
        public void AnEmptyFolderTheProjectHadIsKept()
        {
            Directory.CreateDirectory(Path.Combine(root, "Assets", "Resources"));
            bool leftover = PerformanceTestLeftovers.FolderIsLeftover(root);
            Plant("PerformanceTestRunInfo.json");

            PerformanceTestLeftovers.RemoveFromDisk(root, leftover);

            Assert.That(Directory.Exists(Path.Combine(root, "Assets", "Resources")), Is.True);
            Assert.That(Directory.GetFileSystemEntries(Path.Combine(root, "Assets", "Resources")), Is.Empty);
        }

        [Test]
        public void NothingToCleanRemovesNothing()
        {
            Assert.That(PerformanceTestLeftovers.RemoveFromDisk(root, PerformanceTestLeftovers.FolderIsLeftover(root)), Is.Empty);
        }

        /// <summary>
        /// The cleanup on the open project: a planted run file, imported so Unity writes its <c>.meta</c> and the folder's,
        /// is gone afterwards with the folder, and the AssetDatabase no longer knows the folder. Skipped when the project
        /// has its own <c>Assets/Resources</c>, which this test must not touch.
        /// </summary>
        [Test]
        public void APlantedRunFileInTheOpenProjectIsRemovedWithTheFolderAndItsMeta()
        {
            string projectRoot = BuildGuardContext.ProjectRoot;
            string folder = Path.Combine(projectRoot, PerformanceTestLeftovers.ResourcesFolder);
            if (Directory.Exists(folder) || File.Exists(folder + ".meta"))
            {
                Assert.Ignore("the project has its own Assets/Resources");
            }

            bool leftover = PerformanceTestLeftovers.FolderIsLeftover(projectRoot);
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "PerformanceTestRunInfo.json"), "{}");
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                Assert.That(File.Exists(Path.Combine(folder, "PerformanceTestRunInfo.json.meta")), Is.True, "Unity imported the planted file");
                Assert.That(File.Exists(folder + ".meta"), Is.True, "Unity wrote the folder's .meta");

                PerformanceTestLeftovers.RemoveFromDisk(projectRoot, leftover);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                Assert.That(Directory.Exists(folder), Is.False);
                Assert.That(File.Exists(folder + ".meta"), Is.False);
                Assert.That(AssetDatabase.IsValidFolder(PerformanceTestLeftovers.ResourcesFolder), Is.False);
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }

                if (File.Exists(folder + ".meta"))
                {
                    File.Delete(folder + ".meta");
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }

        private void Plant(params string[] names)
        {
            string folder = Path.Combine(root, "Assets", "Resources");
            Directory.CreateDirectory(folder);
            foreach (string name in names)
            {
                File.WriteAllText(Path.Combine(folder, name), "{}");
            }
        }
    }
}
