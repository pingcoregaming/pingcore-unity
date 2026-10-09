using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Editor.Workspace.Pipeline;

namespace PingCore.Editor.Workspace.Tests.Pipeline
{
    /// <summary>
    /// Which pingctl a push runs: the developer's own, then the bundled binary checked against its pinned manifest
    /// before every use (a mismatch refused, never skipped for another binary), and never one found on PATH. Plus the
    /// manifest the package actually ships.
    /// </summary>
    public sealed class PingctlLocatorTests
    {
        private const string Bundle = @"C:\pkg\Tools~\pingctl";
        private const string Bundled = @"C:\pkg\Tools~\pingctl\windows-amd64\pingctl.exe";
        private static readonly byte[] BinaryBytes = Encoding.ASCII.GetBytes("pretend pingctl 0.1.1 for windows");

        private Dictionary<string, byte[]> files;
        private string manifest;

        // The digest of the planted bytes, computed here with the platform's SHA-256, not by the code under test.
        private static string Digest(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
            }
        }

        private static string Manifest(string windowsDigest, string version = "0.1.1")
        {
            var binaries = new JObject { ["windows-amd64"] = new JObject { ["file"] = "pingctl.exe", ["sha256"] = windowsDigest } };
            binaries["linux-amd64"] = new JObject { ["file"] = "pingctl", ["sha256"] = new string('0', 64) };
            return new JObject { ["format"] = PingctlBundle.Format, ["version"] = version, ["binaries"] = binaries }.ToString();
        }

        [SetUp]
        public void SetUp()
        {
            files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [Bundled] = BinaryBytes,
                [@"C:\mine\pingctl.exe"] = new byte[] { 1 },
                [@"C:\env\pingctl.exe"] = new byte[] { 2 },
                [@"C:\onpath\pingctl.exe"] = new byte[] { 3 },
            };
            manifest = Manifest(Digest(BinaryBytes));
        }

        private PingctlLocatorInputs Inputs(string configured = null, string bin = null, string platform = "windows-amd64", string bundle = Bundle)
        {
            return new PingctlLocatorInputs
            {
                ConfiguredPath = configured,
                BinVariable = bin,
                BundleFolder = bundle,
                Platform = platform,
                FileExists = p => files.ContainsKey(p) || (manifest != null && string.Equals(p, Path.Combine(Bundle, "manifest.json"), StringComparison.OrdinalIgnoreCase)),
                DirectoryExists = p => string.Equals(p, Bundle, StringComparison.OrdinalIgnoreCase),
                ReadText = p => manifest,
                Sha256Of = p => Digest(files[p]),
            };
        }

        [Test]
        public void YourOwnPingctlComesFirstThenPingctlBinThenTheBundledOne()
        {
            PingctlLocation own = PingctlLocator.Locate(Inputs(@"C:\mine\pingctl.exe", @"C:\env\pingctl.exe"));
            Assert.That((own.Source, own.ExpectedSha256), Is.EqualTo((PingctlLocator.SourceOverride, (string)null)), "your own pingctl has no manifest digest: it runs in place, unchecked");
            Assert.That(PingctlLocator.Locate(Inputs(null, @"C:\env\pingctl.exe")).Source, Is.EqualTo("PINGCTL_BIN"));

            PingctlLocation bundled = PingctlLocator.Locate(Inputs());
            Assert.That((bundled.Path, bundled.Source), Is.EqualTo((Bundled, "bundled 0.1.1")));
            Assert.That(bundled.ExpectedSha256, Is.EqualTo(Digest(BinaryBytes)), "the push checks its run copy against the manifest's digest");
        }

        [Test]
        public void APlatformWithoutABundledBinaryIsToldToSetItsOwnAndNothingIsTakenFromPath()
        {
            // A pingctl.exe on PATH is never even looked for: the inputs no longer carry PATH at all.
            PingctlLocation armLinux = PingctlLocator.Locate(Inputs(platform: "linux-arm64"));
            Assert.That(armLinux.Found, Is.False, "[mutation: fall back to a pingctl found on PATH]");
            Assert.That(armLinux.Problem, Is.EqualTo("The PingCore Editor package carries no pingctl for linux-arm64. Set your own pingctl under Ship (or PINGCTL_BIN); the plugin never runs a pingctl it finds on PATH."));

            PingctlLocation noBundle = PingctlLocator.Locate(Inputs(bundle: null, platform: null));
            Assert.That(noBundle.Found, Is.False);
            Assert.That(noBundle.Problem, Does.Contain("carries no pingctl for this platform"));
            Assert.That(PingctlLocator.Locate(Inputs(@"C:\mine\pingctl.exe", platform: "linux-arm64")).Found, Is.True, "your own pingctl works there");
            Assert.That(typeof(PingctlLocatorInputs).GetProperty("PathVariable"), Is.Null, "the locator reads no PATH");
        }

        [Test]
        public void ABundledBinaryThatDoesNotMatchItsManifestIsRefusedNeverSkippedForAnother()
        {
            files[Bundled] = Encoding.ASCII.GetBytes("pretend pingctl 0.1.1 for windowz");
            PingctlLocation refused = PingctlLocator.Locate(Inputs());
            Assert.That(refused.Found, Is.False, "[mutation: fall through to another binary on a mismatch]");
            Assert.That(refused.Problem, Does.Contain("does not match its pinned SHA-256").And.Contain("windows-amd64"));
            Assert.That(PingctlLocator.Locate(Inputs(@"C:\mine\pingctl.exe")).Found, Is.True, "your own pingctl is still yours to use");
        }

        [TestCase("{ not json", "not valid JSON", TestName = "a manifest that is not JSON")]
        [TestCase("{\"format\":\"other/1\",\"version\":\"0.1.1\",\"binaries\":{}}", "is not in format", TestName = "a manifest in another format")]
        [TestCase("{\"format\":\"pingcore-pingctl-bundle/1\",\"version\":\"latest\",\"binaries\":{}}", "no x.y.z version", TestName = "a manifest without a pinned version")]
        [TestCase("{\"format\":\"pingcore-pingctl-bundle/1\",\"version\":\"0.1.1\",\"binaries\":{\"windows-amd64\":{\"file\":\"pingctl.exe\",\"sha256\":\"ABC\"}}}", "bad entry for windows-amd64", TestName = "a digest that is not 64 lower-case hex")]
        [TestCase("{\"format\":\"pingcore-pingctl-bundle/1\",\"version\":\"0.1.1\",\"binaries\":{\"windows-amd64\":\"pingctl.exe\"}}", "bad entry for windows-amd64", TestName = "an entry that is not an object")]
        [TestCase("{\"format\":{\"name\":\"pingcore-pingctl-bundle/1\"},\"version\":\"0.1.1\",\"binaries\":{}}", "is not in format", TestName = "a format that is not a string")]
        public void AManifestThatCannotBeTrustedRefusesTheBundledBinary(string text, string problem)
        {
            manifest = text;
            PingctlLocation located = PingctlLocator.Locate(Inputs());
            Assert.That(located.Found, Is.False);
            Assert.That(located.Problem, Does.Contain(problem));
        }

        [Test]
        public void AManifestNamingAFileOtherThanPingctlIsRefused()
        {
            // A well-formed digest of the right file, so only the name can be what refuses it.
            JObject doc = JObject.Parse(Manifest(Digest(BinaryBytes)));
            doc["binaries"]["windows-amd64"]["file"] = "../evil.exe";
            manifest = doc.ToString();
            PingctlLocation located = PingctlLocator.Locate(Inputs());
            Assert.That(located.Found, Is.False, "[mutation: accept any file name]");
            Assert.That(located.Problem, Does.Contain("bad entry for windows-amd64"));
        }

        [Test]
        public void AMissingManifestOrAMissingListedBinaryIsRefused()
        {
            manifest = null;
            Assert.That(PingctlLocator.Locate(Inputs()).Problem, Does.Contain("has no manifest.json"));

            manifest = Manifest(Digest(BinaryBytes));
            files.Remove(Bundled);
            Assert.That(PingctlLocator.Locate(Inputs()).Problem, Does.Contain("is missing from the package"));
        }

        [Test]
        public void ConfiguredPathsThatDoNotExistAreReportedNeverSkipped()
        {
            Assert.That(PingctlLocator.Locate(Inputs(@"C:\gone\pingctl.exe")).Problem, Does.Contain("Your own pingctl, set under Ship, does not exist"));
            Assert.That(PingctlLocator.Locate(Inputs(null, @"C:\gone\pingctl.exe")).Problem, Does.Contain("PINGCTL_BIN"));
        }

        [TestCase("windows", Architecture.X64, "windows-amd64")]
        [TestCase("macos", Architecture.Arm64, "macos-arm64")]
        [TestCase("macos", Architecture.X64, "macos-amd64")]
        [TestCase("linux", Architecture.X64, "linux-amd64")]
        [TestCase("linux", Architecture.Arm64, null)]
        [TestCase("windows", Architecture.Arm64, null)]
        [TestCase("windows", Architecture.X86, null)]
        [TestCase(null, Architecture.X64, null)]
        public void ThePlatformKeyNamesOnlyWhatTheBundleCarries(string os, Architecture architecture, string key)
        {
            Assert.That(PingctlBundle.PlatformKey(os, architecture), Is.EqualTo(key));
        }

        [Test]
        public void ThePackagesManifestPinsOneVersionAndEachShippedBinaryMatchesIt()
        {
            string folder = Path.Combine(WorkspaceFixtures.PackageRoot, "Tools~", "pingctl");
            PingctlBundleManifest shipped = PingctlBundle.Parse(File.ReadAllText(Path.Combine(folder, PingctlBundle.ManifestName)), out string problem);
            Assert.That(problem, Is.Null);
            Assert.That(shipped.Version, Is.EqualTo("0.1.1"));
            Assert.That(shipped.Binaries.Keys, Is.EquivalentTo(PingctlBundle.Platforms));
            foreach (PingctlBundleEntry entry in shipped.Binaries.Values)
            {
                string binary = Path.Combine(folder, entry.Platform, entry.File);
                Assert.That(File.Exists(binary), Is.True, binary);
                Assert.That(Digest(File.ReadAllBytes(binary)), Is.EqualTo(entry.Sha256), entry.Platform);
                Assert.That(PingctlBundle.Sha256Of(binary), Is.EqualTo(entry.Sha256), "the locator's digest agrees with the platform's");
            }
        }
    }
}
