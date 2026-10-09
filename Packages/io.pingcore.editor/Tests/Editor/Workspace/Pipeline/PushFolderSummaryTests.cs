using NUnit.Framework;
using PingCore.Editor.Workspace.Pipeline;

namespace PingCore.Editor.Workspace.Tests.Pipeline
{
    /// <summary>What Push says it will send before it runs: the folder, the file count and the size, without Unity's do-not-ship folders.</summary>
    public sealed class PushFolderSummaryTests
    {
        [Test]
        public void TheDoNotShipFoldersAreLeftOutOfTheCountAndTheSize()
        {
            PushFolderSummary summary = PushFolderSummary.Of("Builds/Server/v1", new[]
            {
                ("BeaconRush.x86_64", 1024L * 1024),
                ("BeaconRush_Data/level0", 512L * 1024),
                ("BeaconRush_BurstDebugInformation_DoNotShip/lib.so", 9000000L),
                ("Other_BackUpThisFolder_ButDontShipItWithYourGame\\x.cs", 7000L),
                ("UnityPlayer.so", 512L * 1024),
            }, "BeaconRush");
            Assert.That((summary.FileCount, summary.Bytes), Is.EqualTo((3, 2L * 1024 * 1024)), "[mutation: count the excluded folders]");
            Assert.That(summary.Excluded, Is.EqualTo(new[] { "BeaconRush_BurstDebugInformation_DoNotShip", "Other_BackUpThisFolder_ButDontShipItWithYourGame" }));
            Assert.That(summary.Describe(), Is.EqualTo("Builds/Server/v1: 3 files, 2.0 MB (leaving out BeaconRush_BurstDebugInformation_DoNotShip, Other_BackUpThisFolder_ButDontShipItWithYourGame)"));
        }

        [Test]
        public void OnlyTopLevelFoldersAreLeftOut()
        {
            PushFolderSummary summary = PushFolderSummary.Of("f", new[] { ("Data/X_BurstDebugInformation_DoNotShip/a", 10L), ("X_BurstDebugInformation_DoNotShip", 5L) }, "X");
            Assert.That(summary.FileCount, Is.EqualTo(2), "pingctl excludes top-level folders only, and a file of that name is not a folder");
            Assert.That(summary.Describe(), Is.EqualTo("f: 2 files, 15 bytes"));
        }

        [TestCase(0L, "0 bytes")]
        [TestCase(1023L, "1023 bytes")]
        [TestCase(2048L, "2 KB")]
        [TestCase(43305779L, "41.3 MB")]
        [TestCase(1288490189L, "1.20 GB")]
        public void SizesReadAsPeopleWriteThem(long bytes, string text)
        {
            Assert.That(PushFolderSummary.FormatSize(bytes), Is.EqualTo(text));
        }

        [Test]
        public void AFolderThatCannotBeReadSaysSo()
        {
            PushFolderSummary missing = PushFolderSummary.Read(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pingcore-no-such-" + System.Guid.NewGuid().ToString("N")), "X");
            Assert.That(missing.Problem, Does.Contain("could not be read"));
            Assert.That(missing.Describe(), Does.Contain("could not be read"));
        }
    }
}
