using System;
using System.IO;
using NUnit.Framework;
using PingCore.Editor.BuildGuard;

namespace PingCore.Editor.Tests
{
    /// <summary>
    /// The heartbeat-token exemption: a configured token passes only with a confirmation whose digest
    /// matches it. Token-shaped values are assembled from fragments so this file never holds one.
    /// </summary>
    public sealed class BuildGuardConfirmationTests
    {
        private const string Tail16 = "0123456789abcdef";
        private const string AssetPath = "Assets/Resources/PingCoreClientSettings.asset";

        // SHA-256 of the UTF-8 bytes of the test token below, computed outside Unity (sha256sum).
        private const string TokenDigest = "0c025ab414b861a4e9f281bd4952d9fcb8f8ffb2d20934712497200d77a57cc0";

        private const string ExpectedUnconfirmedDetail = "openRegistrationHeartbeatToken is set but its scope is unconfirmed; "
            + "confirm it on Window > PingCore, Player hosting";

        private static readonly string Token = "dsc" + "_" + "open" + Tail16;
        private static readonly string PublicId = "dscp" + "_" + "00112233445566778899aabbccddeeff";

        private static BuildGuardConfiguredToken[] Configured => new[] { new BuildGuardConfiguredToken(AssetPath, Token) };

        private static BuildGuardConfirmation Confirmation(string digest, string scope = "heartbeat", string mode = "open",
            string confirmedAt = "2026-10-02T12:00:00Z", string appPublicId = null) =>
            new BuildGuardConfirmation
            {
                appPublicId = appPublicId ?? PublicId,
                tokenDigest = digest,
                scope = scope,
                registrationMode = mode,
                confirmedAt = confirmedAt,
            };

        [Test]
        public void TheDigestIsTheLowercaseHexSha256OfTheToken()
        {
            Assert.That(BuildGuardConfirmations.Digest(Token), Is.EqualTo(TokenDigest));
        }

        [Test]
        public void AnUnconfirmedConfiguredTokenFailsAsASecretLiteralWithTheScopeDetail()
        {
            BuildGuardTokenResolution resolution = BuildGuardConfirmations.Resolve(Configured, Array.Empty<BuildGuardConfirmation>());

            Assert.That(resolution.Allowed, Is.Empty);
            Assert.That(resolution.Findings.Count, Is.EqualTo(1));
            Assert.That(resolution.Findings[0].Code, Is.EqualTo("secret_literal"));
            Assert.That(resolution.Findings[0].Location, Is.EqualTo(AssetPath));
            Assert.That(resolution.Findings[0].Detail, Is.EqualTo(ExpectedUnconfirmedDetail));
            Assert.That(resolution.Findings[0].ToString(), Does.Not.Contain("open" + Tail16));

            // The unconfirmed token is not exempt anywhere it appears.
            BuildGuardScanResult scan = BuildGuardPolicy.ScanText("openRegistrationHeartbeatToken: " + Token, AssetPath, resolution.Allowed);
            Assert.That(scan.Findings.Count, Is.EqualTo(1));
            Assert.That(scan.AllowedDscTokenHits, Is.EqualTo(0));
        }

        [Test]
        public void AMatchingConfirmationAllowsTheToken()
        {
            BuildGuardTokenResolution resolution = BuildGuardConfirmations.Resolve(Configured, new[] { Confirmation(TokenDigest) });

            Assert.That(resolution.Findings, Is.Empty);
            Assert.That(resolution.Allowed, Is.EquivalentTo(new[] { Token }));
            BuildGuardScanResult scan = BuildGuardPolicy.ScanText("openRegistrationHeartbeatToken: " + Token, AssetPath, resolution.Allowed);
            Assert.That(scan.Findings, Is.Empty);
            Assert.That(scan.AllowedDscTokenHits, Is.EqualTo(1));
        }

        [Test]
        public void ATokenPastedWithSurroundingWhitespaceIsConfirmedAndAllowedAsTheTrimmedToken()
        {
            var padded = new[] { new BuildGuardConfiguredToken(AssetPath, "  " + Token + "\n") };

            BuildGuardTokenResolution resolution = BuildGuardConfirmations.Resolve(padded, new[] { Confirmation(TokenDigest) });

            Assert.That(resolution.Findings, Is.Empty, "[mutation: digest the untrimmed asset value]");
            Assert.That(resolution.Allowed, Is.EquivalentTo(new[] { Token }), "the allowed value is the token a build can carry, without the whitespace");
            Assert.That(BuildGuardConfirmations.Create(" " + Token + " ", PublicId, new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc)).tokenDigest,
                Is.EqualTo(TokenDigest), "the writer's record digests the same trimmed value");
            Assert.That(BuildGuardConfirmations.Confirms(Confirmation(TokenDigest), Token + "\t"), Is.True);
        }

        [Test]
        public void AConfirmationWithADifferentDigestFails()
        {
            string otherDigest = "1" + TokenDigest.Substring(1);
            BuildGuardTokenResolution resolution = BuildGuardConfirmations.Resolve(Configured, new[] { Confirmation(otherDigest) });

            Assert.That(resolution.Allowed, Is.Empty);
            Assert.That(resolution.Findings.Count, Is.EqualTo(1));
            Assert.That(resolution.Findings[0].Detail, Is.EqualTo(ExpectedUnconfirmedDetail));
        }

        [Test]
        public void AConfirmationOfOneTokenDoesNotAllowAnotherConfiguredToken()
        {
            string other = "dsc" + "_" + "private" + Tail16;
            var configured = new[] { new BuildGuardConfiguredToken(AssetPath, Token), new BuildGuardConfiguredToken("Assets/Other.asset", other) };
            BuildGuardTokenResolution resolution = BuildGuardConfirmations.Resolve(configured, new[] { Confirmation(TokenDigest) });

            Assert.That(resolution.Allowed, Is.EquivalentTo(new[] { Token }));
            Assert.That(resolution.Findings.Count, Is.EqualTo(1));
            Assert.That(resolution.Findings[0].Location, Is.EqualTo("Assets/Other.asset"));
        }

        [TestCase("allocate", "open", "2026-10-02T12:00:00Z", "dscp_")]
        [TestCase("both", "open", "2026-10-02T12:00:00Z", "dscp_")]
        [TestCase("heartbeat", "private", "2026-10-02T12:00:00Z", "dscp_")]
        [TestCase("heartbeat", "open", "", "dscp_")]
        [TestCase("heartbeat", "open", "2026-10-02T12:00:00Z", "app_")]
        public void AConfirmationWithTheRightDigestButAnotherScopeModeDateOrIdFails(string scope, string mode, string confirmedAt, string idPrefix)
        {
            BuildGuardConfirmation confirmation = Confirmation(TokenDigest, scope, mode, confirmedAt, idPrefix + "00112233445566778899aabbccddeeff");
            BuildGuardTokenResolution resolution = BuildGuardConfirmations.Resolve(Configured, new[] { confirmation });

            Assert.That(resolution.Allowed, Is.Empty);
            Assert.That(resolution.Findings.Count, Is.EqualTo(1));
        }

        [Test]
        public void TheConfirmationFileHoldsTheDigestAndNeverTheToken()
        {
            string root = Path.Combine(Path.GetTempPath(), "pingcore-confirmation-" + Guid.NewGuid().ToString("N"));
            try
            {
                var record = new BuildGuardConfirmationRecord
                {
                    confirmations = new[] { BuildGuardConfirmations.Create(Token, PublicId, new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc)) },
                };
                BuildGuardConfirmationFile.Write(root, record);

                string text = File.ReadAllText(Path.Combine(root, "ProjectSettings", "PingCoreBuildGuard.json"));
                Assert.That(text, Does.Not.Contain("open" + Tail16));
                Assert.That(text, Does.Contain(TokenDigest));
                Assert.That(text, Does.Contain("\"heartbeat\""));
                Assert.That(text, Does.Contain("\"open\""));
                Assert.That(text, Does.Contain("2026-10-02T12:00:00Z"));

                var readBack = BuildGuardConfirmationFile.Read(root);
                Assert.That(BuildGuardConfirmations.Resolve(Configured, readBack).Allowed, Is.EquivalentTo(new[] { Token }));
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
        public void AMissingConfirmationFileLeavesEveryTokenUnconfirmed()
        {
            string root = Path.Combine(Path.GetTempPath(), "pingcore-confirmation-missing-" + Guid.NewGuid().ToString("N"));
            Assert.That(BuildGuardConfirmationFile.Read(root), Is.Empty);
            Assert.That(BuildGuardConfirmations.Resolve(Configured, BuildGuardConfirmationFile.Read(root)).Findings.Count, Is.EqualTo(1));
        }
    }
}
