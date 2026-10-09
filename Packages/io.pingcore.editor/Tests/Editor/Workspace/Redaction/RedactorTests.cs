using NUnit.Framework;
using PingCore.Editor.Workspace.Redaction;

namespace PingCore.Editor.Workspace.Tests.Redaction
{
    /// <summary>
    /// The redactor table with synthetic hits (every token here is built at run time, so no
    /// token-shaped literal sits in the source). Each row that must mask also proves its input held
    /// the secret, and each near miss proves it is left alone.
    /// </summary>
    public sealed class RedactorTests
    {
        private static string Tail(int n) => new string('a', n - 4) + "B7c9";

        [TestCase("usr_")]
        [TestCase("sys_")]
        [TestCase("cdnpush_")]
        [TestCase("dsc_")]
        public void APrefixedTokenOfSixteenOrMoreIsMaskedKeepingThePrefix(string prefix)
        {
            string token = prefix + Tail(16);
            string line = "pushing with " + token + " now";
            string redacted = Redactor.PatternsOnly.Redact(line);
            Assert.That(redacted, Does.Not.Contain(token));
            Assert.That(redacted, Is.EqualTo("pushing with " + prefix + Redactor.Mask + " now"));
            Assert.That(Redactor.ContainsSecret(line), Is.True);
        }

        [Test]
        public void NearMissesAreLeftAlone()
        {
            string fifteen = "usr_" + Tail(15);
            string publicId = "dscp_" + new string('a', 32);
            string glued = "xusr_" + Tail(20);
            foreach (string line in new[] { fifteen, publicId, glued, "plain text, no secret", new string('f', 63), "12-" + new string('a', 31) })
            {
                Assert.That(Redactor.PatternsOnly.Redact(line), Is.EqualTo(line), line.Length.ToString());
                Assert.That(Redactor.ContainsSecret(line), Is.False);
            }
        }

        [Test]
        public void A64HexRunIsMasked()
        {
            string hex = new string('a', 32) + new string('7', 32);
            string redacted = Redactor.PatternsOnly.Redact("{\"token\":\"" + hex + "\"}");
            Assert.That(redacted, Does.Not.Contain(hex));
            Assert.That(redacted, Is.EqualTo("{\"token\":\"" + Redactor.Mask + "\"}"));
            Assert.That(Redactor.PatternsOnly.Redact(hex + "ff"), Is.EqualTo(Redactor.Mask), "longer runs too");
        }

        [Test]
        public void AGameServerKeyIsMaskedKeepingItsId()
        {
            string key = "4711-" + new string('c', 16) + new string('9', 16);
            string redacted = Redactor.PatternsOnly.Redact("key=" + key);
            Assert.That(redacted, Does.Not.Contain(key));
            Assert.That(redacted, Is.EqualTo("key=4711-" + Redactor.Mask));
        }

        [Test]
        public void AKnownSecretIsMaskedWhateverItsShape()
        {
            string robot = "INJECTED-" + "ROBOT-SECRET-xyz";
            var redactor = new Redactor(new[] { robot, "short" });
            Assert.That(redactor.Redact("login ok " + robot), Is.EqualTo("login ok " + Redactor.Mask));
            Assert.That(redactor.Redact("short stays"), Is.EqualTo("short stays"), "values under eight characters are not masked by value");
            Assert.That(Redactor.PatternsOnly.Redact("login ok " + robot), Does.Contain(robot), "control: the patterns alone do not see it");
            Assert.That(redactor.With(new[] { "another-secret-value" }).Redact("another-secret-value " + robot), Is.EqualTo(Redactor.Mask + " " + Redactor.Mask));
        }

        [Test]
        public void ArgumentsAreRefusedFromEightCharactersAfterThePrefix()
        {
            Assert.That(Redactor.LooksLikeCredentialArgument("--token=cdnpush_" + "abcdefgh"), Is.True);
            Assert.That(Redactor.LooksLikeCredentialArgument("cdnpush_" + "abcdefg"), Is.False);
            Assert.That(Redactor.LooksLikeCredentialArgument(new string('e', 64)), Is.True);
            Assert.That(Redactor.LooksLikeCredentialArgument("--exclude"), Is.False);
            Assert.That(Redactor.LooksLikeCredentialArgument("BeaconRushServer_BurstDebugInformation_DoNotShip/"), Is.False);
            Assert.That(Redactor.LooksLikeCredentialArgument(null), Is.False);
        }

        [Test]
        public void NullAndEmptyPassThrough()
        {
            Assert.That(Redactor.PatternsOnly.Redact(null), Is.Null);
            Assert.That(Redactor.PatternsOnly.Redact(string.Empty), Is.Empty);
        }
    }
}
