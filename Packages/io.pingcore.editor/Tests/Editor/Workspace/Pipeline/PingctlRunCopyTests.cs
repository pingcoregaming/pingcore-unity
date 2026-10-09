using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.Process;

namespace PingCore.Editor.Workspace.Tests.Pipeline
{
    /// <summary>
    /// The pingctl a push runs: the bundled binary only as a private copy, checked against the manifest's digest through
    /// a handle held until the push ends (on Windows nothing can write or delete the copy meanwhile), then deleted; a
    /// developer's own pingctl in place. Then the shipped binary for this Editor run for real from its held copy.
    /// </summary>
    public sealed class PingctlRunCopyTests
    {
        private static readonly byte[] Bytes = Encoding.ASCII.GetBytes("pretend pingctl 0.1.1");

        private string root;
        private string binary;

        private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private string RunRoot => Path.Combine(root, "Library", "PingCore", "pingctl-run");

        // The digest of the planted bytes with the platform's SHA-256, not the code under test.
        private static string Digest(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
            }
        }

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "pingcore-runcopy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "pkg"));
            binary = Path.Combine(root, "pkg", "pingctl.exe");
            File.WriteAllBytes(binary, Bytes);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        private PingctlLocation BundledLocation(string digest) => new PingctlLocation(binary, PingctlLocator.SourceBundled + " 0.1.1", null, digest);

        [Test]
        public void YourOwnPingctlRunsInPlaceAndNothingIsCopied()
        {
            using (PingctlRunCopy run = PingctlRunCopy.Prepare(root, new PingctlLocation(binary, PingctlLocator.SourceOverride, null)))
            {
                Assert.That((run.Ok, run.Path, run.Bundled), Is.EqualTo((true, binary, false)));
            }

            Assert.That(Directory.Exists(RunRoot), Is.False);
        }

        [Test]
        public void ALocationThatWasNotFoundIsRefusedWithItsProblem()
        {
            using (PingctlRunCopy run = PingctlRunCopy.Prepare(root, new PingctlLocation(null, null, "Your own pingctl, set under Ship, does not exist.")))
            {
                Assert.That(run.Ok, Is.False);
                Assert.That(run.Problem, Is.EqualTo("Your own pingctl, set under Ship, does not exist."));
            }
        }

        [Test]
        public void TheBundledBinaryRunsAsAHeldPrivateCopyThatIsDeletedAfterwards()
        {
            string copyPath;
            using (PingctlRunCopy run = PingctlRunCopy.Prepare(root, BundledLocation(Digest(Bytes))))
            {
                Assert.That(run.Ok, Is.True, run.Problem);
                Assert.That(run.Bundled, Is.True);
                copyPath = run.Path;
                Assert.That(copyPath, Is.Not.EqualTo(binary), "[mutation: run the package's binary in place]");
                Assert.That(Path.GetDirectoryName(Path.GetDirectoryName(copyPath)), Is.EqualTo(RunRoot));
                Assert.That(Path.GetFileName(Path.GetDirectoryName(copyPath)), Does.Match("^[0-9a-f]{32}$"), "a fresh, random folder per push");
                Assert.That(File.ReadAllBytes(copyPath), Is.EqualTo(Bytes));

                if (OnWindows)
                {
                    Assert.That(() => new FileStream(copyPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose(), Throws.InstanceOf<IOException>(),
                        "the held handle refuses every writer between the check and the run [mutation: close the handle after hashing]");
                    Assert.That(() => File.Delete(copyPath), Throws.Exception, "and a delete, so nothing can put another file in its place");
                }
            }

            Assert.That(File.Exists(copyPath), Is.False);
            Assert.That(Directory.GetDirectories(RunRoot), Is.Empty, "the copy's folder is deleted after the push");
        }

        [Test]
        public void ABinaryThatChangedSinceItsCheckIsRefusedAndNoCopyIsLeft()
        {
            string digest = Digest(Bytes);
            File.WriteAllBytes(binary, Encoding.ASCII.GetBytes("pretend pingctl 0.1.1 swapped"));
            using (PingctlRunCopy run = PingctlRunCopy.Prepare(root, BundledLocation(digest)))
            {
                Assert.That(run.Ok, Is.False, "[mutation: run the copy without hashing it]");
                Assert.That(run.Problem, Does.Contain("does not match its pinned SHA-256"));
            }

            Assert.That(Directory.GetDirectories(RunRoot), Is.Empty);
        }

        [Test]
        public void AnEarlierPushsLeftoverCopyIsSweptBeforeTheNextOne()
        {
            string stale = Path.Combine(RunRoot, new string('a', 32));
            Directory.CreateDirectory(stale);
            File.WriteAllBytes(Path.Combine(stale, "pingctl.exe"), Bytes);
            using (PingctlRunCopy run = PingctlRunCopy.Prepare(root, BundledLocation(Digest(Bytes))))
            {
                Assert.That(run.Ok, Is.True, run.Problem);
                Assert.That(Directory.Exists(stale), Is.False);
            }
        }

        [Test]
        public void ACopyStillHeldIsNeverSweptByTheNextPrepare()
        {
            // Detect pressed while a push holds its copy: on macOS and Linux the sweep could otherwise delete it.
            using (PingctlRunCopy first = PingctlRunCopy.Prepare(root, BundledLocation(Digest(Bytes))))
            {
                Assert.That(first.Ok, Is.True, first.Problem);
                using (PingctlRunCopy second = PingctlRunCopy.Prepare(root, BundledLocation(Digest(Bytes))))
                {
                    Assert.That(second.Ok, Is.True, second.Problem);
                    Assert.That(File.Exists(first.Path), Is.True, "[mutation: sweep every folder, held or not]");
                    Assert.That(Directory.GetDirectories(RunRoot), Has.Length.EqualTo(2));
                }

                Assert.That(File.Exists(first.Path), Is.True);
            }

            Assert.That(Directory.GetDirectories(RunRoot), Is.Empty);
        }

        [Test]
        public async Task TheShippedPingctlForThisEditorRunsFromItsHeldCopy()
        {
            string platform = PingctlBundle.PlatformHere();
            if (platform == null)
            {
                Assert.Ignore("The package carries no pingctl for this Editor's platform.");
            }

            PingctlLocation located = PingctlLocator.Locate(new PingctlLocatorInputs
            {
                BundleFolder = Path.Combine(WorkspaceFixtures.PackageRoot, "Tools~", "pingctl"),
                Platform = platform,
            });
            Assert.That(located.Found, Is.True, located.Problem);

            using (PingctlRunCopy run = PingctlRunCopy.Prepare(root, located))
            {
                Assert.That(run.Ok, Is.True, run.Problem);
                if (!OnWindows)
                {
                    // The copy need not keep the execute bit; the push sets it the same way.
                    ProcessResult chmod = await new ChildProcessRunner().RunAsync(new ProcessSpec { FileName = "/bin/chmod", Args = new[] { "u+x", run.Path }, Timeout = TimeSpan.FromSeconds(10), Step = "push" }, CancellationToken.None);
                    Assert.That(chmod.Ok, Is.True);
                }

                // `pingctl version` prints its version and calls nothing.
                ProcessResult version = await new ChildProcessRunner().RunAsync(PingctlRunner.VersionSpec(run.Path, WorkspaceEndpoint.Default, null), CancellationToken.None);
                Assert.That(version.Ok, Is.True, version.Error?.Message ?? "exit " + version.ExitCode);
                string pinned = PingctlBundle.Parse(File.ReadAllText(Path.Combine(WorkspaceFixtures.PackageRoot, "Tools~", "pingctl", PingctlBundle.ManifestName)), out _).Version;
                Assert.That(version.Lines.Select(l => l.Text), Has.Some.EqualTo("pingctl v" + pinned), "the held copy started, and it is the manifest's version");
            }
        }
    }
}
