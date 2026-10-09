using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using PingCore.Editor.Cli;

namespace PingCore.Editor.Tests.Cli
{
    /// <summary>
    /// The final check of <see cref="BuildHousekeeping.Finish"/>: <see cref="BuildHousekeeping.FindProblems"/> on a
    /// temporary project root. A settings file written over after its restore, or a run file planted after the cleanup,
    /// is a problem, so <c>Finish</c> returns false and the build fails with exit 1.
    /// </summary>
    public sealed class BuildHousekeepingTests
    {
        private const string Original = "PlayerSettings:\n  targetPixelDensity: 0\n";
        private const string Reserialised = "PlayerSettings:\n  targetPixelDensity: 30\n";

        // SHA-256 of Original, computed outside Unity (node:crypto), so the expectation does not come from the code under test.
        private const string OriginalSha256 = "3f938c4593281a395a800d1fe3f8739730921f9ae85fa8d1fc055bf5b7855532";

        private string root;
        private string settingsPath;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "pingcore-housekeeping-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            settingsPath = Path.Combine(root, "ProjectSettings", "ProjectSettings.asset");
            File.WriteAllBytes(settingsPath, Encoding.UTF8.GetBytes(Original));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void ARestoredProjectWithNoLeftoversHasNoProblems()
        {
            SettingsFileSnapshot snapshot = SettingsFileSnapshot.Capture(settingsPath);
            File.WriteAllBytes(settingsPath, Encoding.UTF8.GetBytes(Reserialised));
            Assert.That(snapshot.Restore(), Is.True);

            Assert.That(BuildHousekeeping.FindProblems(root, true, new[] { snapshot }), Is.Empty);
        }

        [Test]
        public void ASettingsFileWrittenOverAfterItsRestoreIsAProblem()
        {
            SettingsFileSnapshot snapshot = SettingsFileSnapshot.Capture(settingsPath);
            File.WriteAllBytes(settingsPath, Encoding.UTF8.GetBytes(Reserialised));
            Assert.That(snapshot.Restore(), Is.True);

            // The planted post-restore mutation: something wrote the file again after the restore.
            File.WriteAllBytes(settingsPath, Encoding.UTF8.GetBytes(Reserialised));

            Assert.That(BuildHousekeeping.FindProblems(root, true, new[] { snapshot }), Is.EqualTo(new[]
            {
                "ProjectSettings/ProjectSettings.asset still differs from before the build (sha256 " + OriginalSha256 + ")",
            }));
        }

        [Test]
        public void ARunFileLeftAfterTheCleanupIsAProblemAndTheFolderWithIt()
        {
            SettingsFileSnapshot snapshot = SettingsFileSnapshot.Capture(settingsPath);
            bool leftover = PerformanceTestLeftovers.FolderIsLeftover(root);
            string folder = Path.Combine(root, "Assets", "Resources");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "PerformanceTestRunInfo.json"), "{}");
            PerformanceTestLeftovers.RemoveFromDisk(root, leftover);
            Assert.That(BuildHousekeeping.FindProblems(root, leftover, new[] { snapshot }), Is.Empty, "the cleanup removed everything");

            // The planted mutation: a run file written after the cleanup.
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "PerformanceTestRunInfo.json"), "{}");

            Assert.That(BuildHousekeeping.FindProblems(root, leftover, new[] { snapshot }), Is.EqualTo(new[]
            {
                "Assets/Resources/PerformanceTestRunInfo.json is still on disk after the cleanup",
                "Assets/Resources is still on disk after the cleanup",
            }));
            Assert.That(File.Exists(Path.Combine(folder, "PerformanceTestRunInfo.json")), Is.True, "the check deletes nothing");
        }

        [Test]
        public void TheProjectsOwnResourcesFolderIsNotAProblem()
        {
            string folder = Path.Combine(root, "Assets", "Resources");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Config.asset"), "x");
            bool leftover = PerformanceTestLeftovers.FolderIsLeftover(root);

            Assert.That(BuildHousekeeping.FindProblems(root, leftover, Array.Empty<SettingsFileSnapshot>()), Is.Empty);
        }
    }
}
