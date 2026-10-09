using NUnit.Framework;
using PingCore.Editor.BuildGuard;

namespace PingCore.Editor.Tests
{
    /// <summary>What the guard can and cannot see of a build, and the verdict when it cannot see the output.</summary>
    public sealed class BuildGuardCoverageTests
    {
        [TestCase("Game.apk")]
        [TestCase("Game.AAB")]
        [TestCase("main.1.io.pingcore.game.obb")]
        [TestCase("data.unity3d")]
        [TestCase("Game.wasm.br")]
        [TestCase("Game.data.gz")]
        [TestCase("Game.ipa")]
        [TestCase("Game.symbols.zip")]
        [TestCase("Unity-iPhone.xcodeproj")]
        public void CompressedArchivesAndXcodeProjectsCannotBeByteScanned(string name)
        {
            Assert.That(BuildGuardCoverage.IsUnscannableOutputName(name), Is.True);
        }

        [TestCase("Game.x86_64")]
        [TestCase("globalgamemanagers.assets")]
        [TestCase("UnityPlayer.so")]
        [TestCase("Game.wasm")]
        [TestCase("brotli.txt")]
        [TestCase("level0")]
        [TestCase("")]
        public void PlainPlayerFilesCanBeByteScanned(string name)
        {
            Assert.That(BuildGuardCoverage.IsUnscannableOutputName(name), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Resources/unity_builtin_extra")]
        [TestCase("Library/unity default resources")]
        [TestCase("Built-in Texture2D: Splash Screen Unity Logo")]
        [TestCase("Built-in Sprite: ")]
        public void UnityBuiltInResourcesAreNotProjectFilesToScan(string sourceAssetPath)
        {
            Assert.That(BuildGuardCoverage.IsBuiltInSourceAsset(sourceAssetPath), Is.True);
        }

        [TestCase("Assets/Resources/BillingMode.json")]
        [TestCase("Packages/com.example.tools/Resources/Built-in Settings.asset")]
        [TestCase("Assets/Built-in Texture.png")]
        [TestCase("ProjectSettings/ProjectSettings.asset")]
        public void ProjectAndPackageAssetsAreScanned(string sourceAssetPath)
        {
            Assert.That(BuildGuardCoverage.IsBuiltInSourceAsset(sourceAssetPath), Is.False);
        }

        [Test]
        public void AnAssemblyListAndNoArchiveIsACompleteOutputScan()
        {
            BuildGuardOutputCoverage coverage = BuildGuardCoverage.Decide(true, new string[0], false, 0, "Builds/X/Game.x86_64");
            Assert.That(coverage.OutputScan, Is.EqualTo("complete"));
            Assert.That(coverage.Notes, Is.Empty);
            Assert.That(coverage.Finding, Is.Null);
        }

        [Test]
        public void AnUnscannableOutputPassesOnlyOnTheSourceSideScanPlusStagedAssembliesAndIsMarkedAssembliesOnly()
        {
            BuildGuardOutputCoverage coverage = BuildGuardCoverage.Decide(false, new[] { "Builds/Android/Game.apk" }, true, 3, "Builds/Android/Game.apk");
            Assert.That(coverage.OutputScan, Is.EqualTo("assemblies-only"));
            Assert.That(coverage.Notes.Count, Is.EqualTo(2));
            Assert.That(coverage.Finding, Is.Null);
        }

        [Test]
        public void AnUnscannableOutputWithTheSourceSideScanButNoStagedAssemblyFailsClosed()
        {
            // Before staged assemblies were scanned this passed on the source side alone, and a folded token in IL shipped.
            BuildGuardOutputCoverage coverage = BuildGuardCoverage.Decide(true, new[] { "Builds/Android/Game.apk" }, true, 0, "Builds/Android/Game.apk");
            Assert.That(coverage.OutputScan, Is.EqualTo("unavailable"));
            Assert.That(coverage.Finding.Code, Is.EqualTo("output_unscannable"));
            Assert.That(coverage.Finding.Detail, Does.Contain("no compiled player assembly was found"));
            Assert.That(coverage.Finding.Detail, Does.Not.Contain("source-side scan of the packed assets did not run"));
        }

        [Test]
        public void AnUnscannableOutputWithoutTheSourceSideScanFailsClosed()
        {
            BuildGuardOutputCoverage coverage = BuildGuardCoverage.Decide(true, new[] { "Builds/X/Game_Data/data.unity3d" }, false, 2, "Builds/X/Game.x86_64");
            Assert.That(coverage.OutputScan, Is.EqualTo("unavailable"));
            Assert.That(coverage.Finding, Is.Not.Null);
            Assert.That(coverage.Finding.Reason, Is.EqualTo(BuildGuardReason.OutputUnscannable));
            Assert.That(coverage.Finding.Code, Is.EqualTo("output_unscannable"));
            Assert.That(coverage.Finding.Location, Is.EqualTo("Builds/X/Game.x86_64"));
            Assert.That(coverage.Finding.Detail, Does.Contain("data.unity3d"));
            Assert.That(coverage.Finding.Detail, Does.Contain("source-side scan of the packed assets did not run"));
        }

        [Test]
        public void AMissingAssemblyListAloneWithoutTheSourceSideScanFailsClosed()
        {
            BuildGuardOutputCoverage coverage = BuildGuardCoverage.Decide(false, new string[0], false, 0, "Builds/WebGL");
            Assert.That(coverage.Finding, Is.Not.Null);
            Assert.That(coverage.Finding.Code, Is.EqualTo("output_unscannable"));
        }
    }
}
