using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PingCore.Editor.BuildGuard;

namespace PingCore.Editor.Tests
{
    /// <summary>
    /// Which files the source-side scan reads under <c>Assets/</c> or a package folder, and the asset
    /// path a finding names. Stands in for a real build with a third-party package's <c>Resources/</c>
    /// canary, which batchmode cannot register in the middle of an <c>-executeMethod</c> run. Token-shaped
    /// values are assembled from fragments.
    /// </summary>
    public sealed class BuildSourceScannerSelectionTests
    {
        private static readonly string UsrToken = "usr" + "_" + "0000guardpackage00000000";

        [TestCase("Resources/CanaryConfig.asset")]
        [TestCase("Runtime/Resources/Deep/data.bytes")]
        [TestCase("Resources/blob.bin")]
        [TestCase("StreamingAssets/a.bin")]
        [TestCase("Runtime/StreamingAssets/sub/b.json")]
        public void EveryFileInAResourcesOrStreamingAssetsFolderShips(string relativePath)
        {
            Assert.That(BuildSourceScanner.IsSelected(relativePath, false), Is.True);
        }

        [TestCase("Editor/Config.asset")]
        [TestCase("Runtime/Data.json")]
        [TestCase("ResourcesExtra/x.asset")]
        [TestCase("Resources")]
        [TestCase("Samples~/Resources/x.asset")]
        [TestCase("Documentation~/StreamingAssets/x.bin")]
        [TestCase(".hidden/Resources/x.asset")]
        [TestCase("Resources/x.asset.meta")]
        [TestCase("Resources/.secret")]
        [TestCase("")]
        public void FilesOutsideShippedFoldersOrInIgnoredFoldersAreNotSelected(string relativePath)
        {
            Assert.That(BuildSourceScanner.IsSelected(relativePath, false), Is.False);
        }

        [TestCase("Docs/readme.md", true)]
        [TestCase("Settings/Game.asset", true)]
        [TestCase("StreamingAssets/x.bin", true)]
        [TestCase("Data/x.bin", false)]
        [TestCase("Samples~/a.json", false)]
        [TestCase("Data/x.json.meta", false)]
        public void TheTextAssetSweepSelectsTextAnywhereAndAnyStreamingAsset(string relativePath, bool expected)
        {
            Assert.That(BuildSourceScanner.IsSelected(relativePath, true), Is.EqualTo(expected));
        }

        [Test]
        public void ATokenInAThirdPartyPackagesResourcesFolderIsFoundAndNamedByItsPackagePath()
        {
            // The package folder itself ends in "~", like SampleGame's demo canary: only folders below the root are filtered.
            string root = Path.Combine(Path.GetTempPath(), "pingcore-thirdparty-" + Guid.NewGuid().ToString("N") + "~");
            try
            {
                void Write(string relative, string text)
                {
                    string path = Path.Combine(root, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, text);
                }

                Write("package.json", "{\"name\":\"com.example.thirdparty\"}");
                Write("Resources/CanaryConfig.asset", "MonoBehaviour:\n  apiKey: " + UsrToken + "\n");
                Write("Runtime/Resources/Deep/data.bytes", UsrToken);
                Write("Editor/Config.asset", UsrToken);
                Write("Samples~/Resources/sample.asset", UsrToken);
                Write("Resources/CanaryConfig.asset.meta", UsrToken);

                var scanner = new BuildSourceScanner(Path.GetTempPath(), Array.Empty<string>());
                scanner.ScanFolder(root, "Packages/com.example.thirdparty", false);

                Assert.That(scanner.Findings.Select(f => f.Code + " " + f.Location).OrderBy(s => s, StringComparer.Ordinal), Is.EqualTo(new[]
                {
                    "secret_literal Packages/com.example.thirdparty/Resources/CanaryConfig.asset",
                    "secret_literal Packages/com.example.thirdparty/Runtime/Resources/Deep/data.bytes",
                }));
                Assert.That(scanner.ScannedFiles, Is.EqualTo(2));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Test]
        public void AFileOutsideTheScanRootKeepsItsFullPath()
        {
            string root = Path.Combine(Path.GetTempPath(), "pkg-root");
            string inside = Path.Combine(root, "Resources", "a.asset");
            string outside = Path.Combine(Path.GetTempPath(), "elsewhere", "b.asset");

            Assert.That(BuildSourceScanner.ToAssetDisplayPath(root, "Packages/com.example", inside), Is.EqualTo("Packages/com.example/Resources/a.asset"));
            Assert.That(BuildSourceScanner.ToAssetDisplayPath(root, "Packages/com.example", outside), Is.EqualTo(BuildGuardPolicy.NormalizeFullPath(outside)));
        }
    }
}
