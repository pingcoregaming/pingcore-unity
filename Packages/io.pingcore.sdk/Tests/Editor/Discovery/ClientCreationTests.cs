using System;
using NUnit.Framework;
using PingCore.Core.Discovery;
using UnityEngine;

namespace PingCore.Discovery.Client.Tests.Editor
{
    /// <summary>The client-embeddable rule: only a <c>dscp_</c> id and runtime player tokens; every other credential shape is refused at creation.</summary>
    public sealed class ClientCreationTests
    {
        private const string Base = FakeDiscoveryTransport.BaseUrl;

        private static DiscoveryClientOptions Options(string appId, string baseUrl = Base, string profile = "")
        {
            var scheduler = new TestScheduler();
            return new DiscoveryClientOptions
            {
                BaseUrl = baseUrl,
                AppPublicId = appId,
                Profile = profile,
                Transport = new FakeDiscoveryTransport(scheduler),
                Scheduler = scheduler,
                TokenStore = new MemoryTokenStore(),
            };
        }

        // Fragments, so this file never holds a token-shaped literal the build guard or a scanner would flag.
        private static string Shaped(string prefix) => prefix + "_" + new string('A', 24);

        [TestCase("usr")]
        [TestCase("sys")]
        [TestCase("cdnpush")]
        [TestCase("dsc")]
        public void ACredentialShapedAppIdIsRefusedWithoutQuotingIt(string prefix)
        {
            string value = Shaped(prefix);
            var e = Assert.Throws<ArgumentException>(() => DiscoveryClient.Create(Options(value)));
            Assert.That(e.Message, Does.Not.Contain(value));
            Assert.That(e.Message, Does.Contain("credential-shaped"));
        }

        [Test]
        public void ACredentialHiddenInTheBaseUrlOrProfileIsRefusedToo()
        {
            Assert.Throws<ArgumentException>(() => DiscoveryClient.Create(Options(ClientHarness.AppId, Base + "/?k=" + Shaped("dsc"))));
            Assert.Throws<ArgumentException>(() => DiscoveryClient.Create(Options(ClientHarness.AppId, Base, Shaped("usr"))));
        }

        [Test]
        public void ADscpIdIsAcceptedAndTheShapeCheckHasNoFalsePositiveOnIt()
        {
            using (DiscoveryClient client = DiscoveryClient.Create(Options(ClientHarness.AppId)))
            {
                Assert.That(client.AppPublicId, Is.EqualTo(ClientHarness.AppId));
                Assert.That(client.BaseUrl, Is.EqualTo(Base));
            }

            Assert.That(DiscoveryClient.LooksLikeCredential("dscp_" + new string('a', 32)), Is.False);
            Assert.That(DiscoveryClient.LooksLikeCredential("mydsc_" + new string('a', 32)), Is.False, "preceded by a letter, as the build guard reads it");
            Assert.That(DiscoveryClient.LooksLikeCredential("x " + Shaped("dsc")), Is.True);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("dscp_")]
        [TestCase("app-42")]
        [TestCase("dscp_has space")]
        public void AnythingButAPublicIdIsRefused(string appId)
        {
            Assert.Throws<ArgumentException>(() => DiscoveryClient.Create(Options(appId)));
        }

        [Test]
        public void ABadBaseUrlProfileOrAttemptCountIsRefused()
        {
            Assert.Throws<ArgumentException>(() => DiscoveryClient.Create(Options(ClientHarness.AppId, "ftp://discovery.test")));
            Assert.Throws<ArgumentException>(() => DiscoveryClient.Create(Options(ClientHarness.AppId, "not a url")));
            Assert.Throws<ArgumentException>(() => DiscoveryClient.Create(Options(ClientHarness.AppId, Base, "a.b")));
            DiscoveryClientOptions zero = Options(ClientHarness.AppId);
            zero.MaxAttempts = 0;
            Assert.Throws<ArgumentException>(() => DiscoveryClient.Create(zero));
        }

        [Test]
        public void TheSettingsAssetOverloadUsesItsBaseUrl()
        {
            var settings = ScriptableObject.CreateInstance<PingCoreClientSettings>();
            try
            {
                using (DiscoveryClient client = DiscoveryClient.Create(settings, ClientHarness.AppId))
                {
                    Assert.That(client.BaseUrl, Is.EqualTo("https://discovery.pingcore.io"));
                    Assert.That(client.Tokens.Current, Is.Null, "creation sends nothing and issues nothing");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }
    }
}
