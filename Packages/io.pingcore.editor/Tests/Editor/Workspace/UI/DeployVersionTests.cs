using System;
using System.IO;
using NUnit.Framework;
using PingCore.Editor.Workspace.UI.Ship;

namespace PingCore.Editor.Workspace.Tests.UI
{
    /// <summary>Ship's build version: the default from the date and the git short sha, and the version rule of the server builds.</summary>
    public sealed class DeployVersionTests
    {
        [Test]
        public void TheDefaultVersionIsTheDateAndTheShortShaOrLocal()
        {
            var day = new DateTime(2026, 10, 6, 23, 59, 0);

            Assert.That(DeployVersion.Default(day, "c0ffee1"), Is.EqualTo("2026.10.06-c0ffee1"));
            Assert.That(DeployVersion.Default(day, null), Is.EqualTo("2026.10.06-local"));
            Assert.That(DeployVersion.IsValid(DeployVersion.Default(day, "c0ffee1")), Is.True);
        }

        [TestCase("2026.10.06-editor1", true)]
        [TestCase("a", true)]
        [TestCase("-leading", false)]
        [TestCase("has space", false)]
        [TestCase("", false)]
        public void TheVersionRuleMatchesTheServerBuilds(string version, bool valid)
        {
            Assert.That(DeployVersion.IsValid(version), Is.EqualTo(valid));
        }

        [Test]
        public void TheGitShortShaIsReadFromALooseRefAPackedRefAndADetachedHead()
        {
            string root = Path.Combine(Path.GetTempPath(), "pingcore-git-" + Guid.NewGuid().ToString("N"));
            string git = Path.Combine(root, ".git");
            string project = Path.Combine(root, "SampleGame");
            try
            {
                Directory.CreateDirectory(Path.Combine(git, "refs", "heads"));
                Directory.CreateDirectory(project);
                File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/master\n");
                File.WriteAllText(Path.Combine(git, "refs", "heads", "master"), "c0ffee10123456789abcdef0123456789abcdef0\n");
                Assert.That(DeployVersion.GitShortSha(project), Is.EqualTo("c0ffee1"));

                File.Delete(Path.Combine(git, "refs", "heads", "master"));
                File.WriteAllText(Path.Combine(git, "packed-refs"), "# pack-refs with: peeled fully-peeled sorted\nfedcba9876543210fedcba9876543210fedcba98 refs/heads/master\n");
                Assert.That(DeployVersion.GitShortSha(project), Is.EqualTo("fedcba9"));

                File.WriteAllText(Path.Combine(git, "HEAD"), "0011223344556677889900112233445566778899\n");
                Assert.That(DeployVersion.GitShortSha(project), Is.EqualTo("0011223"));

                File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/none\n");
                Assert.That(DeployVersion.GitShortSha(project), Is.Null, "an unborn branch has no sha");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
