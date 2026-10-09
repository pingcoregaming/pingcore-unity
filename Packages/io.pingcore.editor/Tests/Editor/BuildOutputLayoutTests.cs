using System.IO;
using NUnit.Framework;
using PingCore.Editor.BuildGuard;

namespace PingCore.Editor.Tests
{
    /// <summary>Where the postprocessor looks for the shipped assembly list, and what it may delete.</summary>
    public sealed class BuildOutputLayoutTests
    {
        private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pingcore-layout-project"));

        private static string N(string path) => BuildGuardPolicy.NormalizeFullPath(path);

        [Test]
        public void TheDataFolderOfALinuxPlayerIsTheStemWithDataBesideTheExecutable()
        {
            string output = Path.Combine(Root, "Builds", "GuardDemo", "clean", "GuardDemo.x86_64");
            Assert.That(BuildOutputLayout.GetDataFolder(output), Is.EqualTo(N(Path.Combine(Root, "Builds", "GuardDemo", "clean", "GuardDemo_Data"))));
            Assert.That(BuildOutputLayout.GetBuildRoot(output, false), Is.EqualTo(N(Path.Combine(Root, "Builds", "GuardDemo", "clean"))));
            Assert.That(BuildOutputLayout.GetExecutableStem(output), Is.EqualTo("GuardDemo"));
        }

        [Test]
        public void TheDataFolderOfAMacAppIsInsideTheBundle()
        {
            string output = Path.Combine(Root, "Builds", "Mac", "Game.app");
            Assert.That(BuildOutputLayout.GetDataFolder(output), Is.EqualTo(N(output) + "/Contents/Resources/Data"));
            Assert.That(BuildOutputLayout.GetBuildRoot(output, true), Is.EqualTo(N(Path.Combine(Root, "Builds", "Mac"))));
        }

        [Test]
        public void ProducedEntriesAreTheOutputTheClosedStemSuffixesAndTheUnityRuntimeFiles()
        {
            var entries = new[]
            {
                "GuardDemo.x86_64", "GuardDemo_Data", "GuardDemo_s.debug", "GuardDemo.pdb", "GuardDemo.debug",
                "GuardDemo_BurstDebugInformation_DoNotShip", "GuardDemo_BackUpThisFolder_ButDontShipItWithYourGame", "UnityPlayer.so",
                "libdecor-0.so.0", "libdecor-cairo.so", "notes.txt", "OtherGame.x86_64", "GuardDemoExtra",
            };
            Assert.That(BuildOutputLayout.SelectProducedEntries(entries, "GuardDemo.x86_64"), Is.EqualTo(new[]
            {
                "GuardDemo.x86_64", "GuardDemo_Data", "GuardDemo_s.debug", "GuardDemo.pdb", "GuardDemo.debug",
                "GuardDemo_BurstDebugInformation_DoNotShip", "GuardDemo_BackUpThisFolder_ButDontShipItWithYourGame", "UnityPlayer.so",
                "libdecor-0.so.0", "libdecor-cairo.so",
            }));
        }

        [Test]
        public void EntriesThatOnlyShareTheStemAreNeverProduced()
        {
            // The old <stem>.*, <stem>_* and bare <stem> globs picked all of these up.
            var entries = new[]
            {
                "Game.x86_64", "Game_Data", "Game.env", "Game_secrets.txt", "Game", "Game.x86_64.bak", "Game_Data_old", "Game.zip",
                "Other_BurstDebugInformation_DoNotShip",
            };
            Assert.That(BuildOutputLayout.SelectProducedEntries(entries, "Game.x86_64", "Product"), Is.EqualTo(new[] { "Game.x86_64", "Game_Data" }));
        }

        [Test]
        public void AnOutputWithoutAnExtensionMatchesItsExactNameOnly()
        {
            var entries = new[] { "Game", "Game_Data", "Game.env", "Game_secrets.txt" };
            Assert.That(BuildOutputLayout.SelectProducedEntries(entries, "Game"), Is.EqualTo(new[] { "Game", "Game_Data" }));
        }

        [Test]
        public void TheBurstAndBackupFoldersNamedAfterTheProductAreProducedEntries()
        {
            var entries = new[]
            {
                "GuardDemo.x86_64", "SampleGame_BurstDebugInformation_DoNotShip", "SampleGame_BackUpThisFolder_ButDontShipItWithYourGame",
                "SampleGame_Notes", "BurstDebugInformation_DoNotShip.txt",
            };
            Assert.That(BuildOutputLayout.SelectProducedEntries(entries, "GuardDemo.x86_64", "SampleGame"), Is.EqualTo(new[]
            {
                "GuardDemo.x86_64", "SampleGame_BurstDebugInformation_DoNotShip", "SampleGame_BackUpThisFolder_ButDontShipItWithYourGame",
            }));
            Assert.That(BuildOutputLayout.SelectProducedEntries(entries, "GuardDemo.x86_64", "OtherProduct"), Is.EqualTo(new[] { "GuardDemo.x86_64" }),
                "another product's folders are not this build's");
        }

        [Test]
        public void AFolderUnderBuildsIsSafeToDelete()
        {
            Assert.That(BuildOutputLayout.IsSafeToDelete(Path.Combine(Root, "Builds", "GuardDemo", "clean"), Root), Is.True);
        }

        [Test]
        public void ProducedEntriesInAFolderOutsideTheProjectMayBeDeletedButNothingElseThereIsProduced()
        {
            string desktop = Path.Combine(Path.GetTempPath(), "elsewhere", "Desktop");
            Assert.That(BuildOutputLayout.IsSafeToDelete(desktop, Root), Is.True);

            var entries = new[] { "Game.exe", "Game_Data", "UnityPlayer.dll", "MonoBleedingEdge", ".env", "notes.txt", "OtherGame.exe", "Build" };
            Assert.That(BuildOutputLayout.SelectProducedPaths(desktop, entries, "Game.exe", null, false, null), Is.EqualTo(new[]
            {
                N(Path.Combine(desktop, "Game.exe")), N(Path.Combine(desktop, "Game_Data")),
                N(Path.Combine(desktop, "UnityPlayer.dll")), N(Path.Combine(desktop, "MonoBleedingEdge")),
            }));
        }

        [Test]
        public void ReportFilesInsideTheBuildRootAreAddedOnceAndFilesOutsideItAreIgnored()
        {
            string root = Path.Combine(Root, "Builds", "Linux");
            var reportFiles = new[]
            {
                Path.Combine(root, "Game_Data", "level0"),
                Path.Combine(root, "libnew_runtime.so"),
                Path.Combine(root, "libnew_runtime.so"),
                Path.Combine(Root, "Library", "elsewhere.bin"),
                Path.Combine(Path.GetTempPath(), "Desktop", ".env"),
                root,
            };

            Assert.That(BuildOutputLayout.SelectProducedPaths(root, new[] { "Game.x86_64", "Game_Data", ".env" }, "Game.x86_64", null, false, reportFiles),
                Is.EqualTo(new[]
                {
                    N(Path.Combine(root, "Game.x86_64")), N(Path.Combine(root, "Game_Data")), N(Path.Combine(root, "libnew_runtime.so")),
                }));
        }

        [Test]
        public void AFolderOutputProducesTheWebGlEntriesAndXcodeProjectsOnly()
        {
            string root = Path.Combine(Path.GetTempPath(), "elsewhere", "WebGL");
            var entries = new[] { "Build", "TemplateData", "index.html", "StreamingAssets", "Unity-iPhone.xcodeproj", "my-notes.md", ".env" };
            Assert.That(BuildOutputLayout.SelectProducedEntries(entries, "WebGL", null, true), Is.EqualTo(new[]
            {
                "Build", "TemplateData", "index.html", "StreamingAssets", "Unity-iPhone.xcodeproj",
            }));
            Assert.That(BuildOutputLayout.SelectProducedEntries(entries, "Game.x86_64", null, false), Is.Empty);
        }

        [TestCase("")]
        [TestCase("Assets")]
        [TestCase("Assets/Sub")]
        [TestCase("Packages")]
        [TestCase("ProjectSettings")]
        [TestCase("Library")]
        [TestCase("UserSettings")]
        public void TheProjectAndItsOwnFoldersAreNeverDeleted(string relative)
        {
            string buildRoot = relative.Length == 0 ? Root : Path.Combine(Root, relative);
            Assert.That(BuildOutputLayout.IsSafeToDelete(buildRoot, Root), Is.False);
        }

        [Test]
        public void AnAncestorOfTheProjectIsNeverDeleted()
        {
            Assert.That(BuildOutputLayout.IsSafeToDelete(Path.GetDirectoryName(Root), Root), Is.False);
        }
    }
}
