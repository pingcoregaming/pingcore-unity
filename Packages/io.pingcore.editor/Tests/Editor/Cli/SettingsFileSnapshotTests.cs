using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using PingCore.Editor.Cli;

namespace PingCore.Editor.Tests.Cli
{
    /// <summary>
    /// A settings file a build re-serialised is written back to the exact bytes it had before the build. Runs on a
    /// temporary folder, never on the project's own settings.
    /// </summary>
    public sealed class SettingsFileSnapshotTests
    {
        // A slice of a real ProjectSettings.asset before and after an IL2CPP build set the backend and set it back.
        private const string Original = "PlayerSettings:\n  targetPixelDensity: 0\n  buildNumber: {}\n  iOSTargetOSVersionString: \n  scriptingBackend: {}\n";
        private const string Reserialised = "PlayerSettings:\n  targetPixelDensity: 30\n  buildNumber:\n    Standalone: 0\n  iOSTargetOSVersionString: 15.0\n  scriptingBackend:\n    Standalone: 0\n";

        // SHA-256 of Original, computed outside Unity (node:crypto), so the expectation does not come from the code under test.
        private const string OriginalSha256 = "86893f2382e140c7cbd9f2c3a09abb97d80ce3d6a784769b9a0f594372d00c0a";

        private string folder;
        private string path;

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "pingcore-settings-snapshot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, "ProjectSettings.asset");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void ARestoredFileHasTheOriginalBytesAndHashAfterUnityReserialisedIt()
        {
            byte[] original = Encoding.UTF8.GetBytes(Original);
            File.WriteAllBytes(path, original);
            SettingsFileSnapshot snapshot = SettingsFileSnapshot.Capture(path);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(Reserialised));
            Assert.That(snapshot.IsIntact(), Is.False);

            Assert.That(snapshot.Restore(), Is.True);

            byte[] restored = File.ReadAllBytes(path);
            Assert.That(restored, Is.EqualTo(original));
            Assert.That(SettingsFileSnapshot.Sha256Hex(restored), Is.EqualTo(snapshot.OriginalHash));
            Assert.That(snapshot.IsIntact(), Is.True);
        }

        [Test]
        public void OnlyTheLineEndingChangingStillCountsAsADifference()
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(Original));
            SettingsFileSnapshot snapshot = SettingsFileSnapshot.Capture(path);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(Original.Replace("\n", "\r\n")));

            Assert.That(snapshot.Restore(), Is.True);
            Assert.That(File.ReadAllText(path), Is.EqualTo(Original));
        }

        [Test]
        public void AnUnchangedFileIsNotRewritten()
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(Original));
            SettingsFileSnapshot snapshot = SettingsFileSnapshot.Capture(path);
            var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, stamp);

            Assert.That(snapshot.Restore(), Is.False);
            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(stamp));
        }

        [Test]
        public void ADeletedFileIsWrittenBack()
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(Original));
            SettingsFileSnapshot snapshot = SettingsFileSnapshot.Capture(path);
            File.Delete(path);

            Assert.That(snapshot.Restore(), Is.True);
            Assert.That(File.ReadAllText(path), Is.EqualTo(Original));
        }

        [Test]
        public void AFileThatDidNotExistIsLeftAsUnityCreatedIt()
        {
            SettingsFileSnapshot snapshot = SettingsFileSnapshot.Capture(path);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(Reserialised));

            Assert.That(snapshot.Existed, Is.False);
            Assert.That(snapshot.Restore(), Is.False);
            Assert.That(File.ReadAllText(path), Is.EqualTo(Reserialised));
            Assert.That(snapshot.IsIntact(), Is.False);
        }

        [Test]
        public void NeedsRestoreIsTrueOnlyWhenTheCurrentBytesDifferFromAnOriginal()
        {
            byte[] a = { 1, 2, 3 };
            Assert.That(SettingsFileSnapshot.NeedsRestore(a, new byte[] { 1, 2, 3 }), Is.False);
            Assert.That(SettingsFileSnapshot.NeedsRestore(a, new byte[] { 1, 2, 4 }), Is.True);
            Assert.That(SettingsFileSnapshot.NeedsRestore(a, new byte[] { 1, 2 }), Is.True);
            Assert.That(SettingsFileSnapshot.NeedsRestore(a, null), Is.True);
            Assert.That(SettingsFileSnapshot.NeedsRestore(null, a), Is.False);
            Assert.That(SettingsFileSnapshot.NeedsRestore(null, null), Is.False);
        }

        [Test]
        public void TheHashIsTheLowerCaseHexSha256OfTheBytes()
        {
            Assert.That(SettingsFileSnapshot.Sha256Hex(Encoding.UTF8.GetBytes(Original)), Is.EqualTo(OriginalSha256));
            Assert.That(SettingsFileSnapshot.Sha256Hex(Array.Empty<byte>()), Is.EqualTo("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));
        }
    }
}
